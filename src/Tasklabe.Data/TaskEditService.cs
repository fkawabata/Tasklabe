using Tasklabe.Core.Domain;
using Tasklabe.Core.Editing;
using Tasklabe.Core.Milestones;
using Tasklabe.Core.Wbs;

namespace Tasklabe.Data;

/// <summary>
/// 画面からのタスクの編集。キャッシュへ即座に反映し、GitHub への送信は送信キューに任せる（要件 F-TSK-01、F-SYNC-02、NF-02）。
/// </summary>
/// <param name="currentLogin">サインイン中の利用者のログイン名。</param>
/// <param name="today">実績の日付などに使う今日。省略すると time の今日。</param>
public sealed class TaskEditService(SqliteTaskStore store, SyncEngine sync, TimeProvider time, Func<string?> currentLogin, Func<DateOnly>? today = null)
{
    /// <summary>ローカルでタスクが変わった。引数は変更後のタスク（削除・一括操作では null）。</summary>
    public event EventHandler<TaskItem?>? Changed;

    /// <summary>元に戻す・やり直すための履歴（要件 F-UNDO-01）。作成・削除・プロジェクトの移動は含めない。</summary>
    public EditHistory History { get; } = HistoryFor(store);

    private DateOnly Today => today?.Invoke() ?? DateOnly.FromDateTime(time.GetLocalNow().DateTime);

    public Task<TaskItem?> SetAsync(TaskItem task, TaskField field, string? value) =>
        ApplyAsync(task, TaskRules.Set(task, field, value));

    /// <summary>担当者を置き換える（要件 F-TSK-06）。</summary>
    public Task<TaskItem?> SetAssigneesAsync(TaskItem task, IEnumerable<string> logins) =>
        ApplyAsync(task, TaskRules.Set(task, TaskField.Assignees, TaskValues.Logins(logins)));

    /// <summary>後続を待たせない印を付け外しする（要件 F-DEP-07）。</summary>
    public Task<TaskItem?> SetNonBlockingAsync(TaskItem task, bool nonBlocking) =>
        ApplyAsync(task, TaskRules.Set(task, TaskField.Schedule, nonBlocking ? ProjectConventions.ScheduleOptions.NonBlocking : null));

    /// <summary>先行タスクを置き換える（要件 F-TSK-05）。</summary>
    public Task<TaskItem?> SetBlockedByAsync(TaskItem task, IEnumerable<string> blockingIssueIds) =>
        ApplyAsync(task, TaskRules.Set(task, TaskField.BlockedBy, TaskValues.IssueIds(blockingIssueIds)));

    /// <summary>課題を計画へ移す（要件 F-TSK-09）。</summary>
    public Task<TaskItem?> PromoteToPlanAsync(TaskItem task, string? parentIssueId, double sortOrder,
        DateOnly? start, DateOnly? target, double? estimate) =>
        ApplyAsync(task, TaskRules.PromoteToPlan(task, parentIssueId, sortOrder, start, target, estimate));

    /// <summary>計画のタスクを課題へ戻す（要件 F-TSK-10）。</summary>
    public Task<TaskItem?> DemoteToIssueAsync(TaskItem task) => ApplyAsync(task, TaskRules.DemoteToIssue(task));

    public Task<TaskItem?> ChangeStatusAsync(TaskItem task, StatusOption option) =>
        ApplyAsync(task, TaskRules.ChangeStatus(task, option, Today));

    public async Task<TaskItem?> ToggleDoneAsync(TaskItem task)
    {
        var project = await store.GetProjectAsync(task.ProjectId).ConfigureAwait(false);
        return project is null ? task : await ApplyAsync(task, TaskRules.ToggleDone(task, project.StatusOptions, Today)).ConfigureAwait(false);
    }

    /// <summary>複数の変更をまとめて適用する（WBS の構造の操作など）。</summary>
    public Task<TaskItem?> ApplyChangesAsync(TaskItem task, IReadOnlyList<TaskChange> changes) => ApplyAsync(task, changes);

    /// <summary>
    /// 複数のタスクの変更をまとめて反映する（依存関係による日程の調整の確定など）。
    /// 画面の再表示と送信は最後に 1 回だけ行う。
    /// </summary>
    public async Task ApplyManyAsync(IReadOnlyList<(TaskItem Task, IReadOnlyList<TaskChange> Changes)> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);

        var applied = new List<(TaskItem Task, IReadOnlyList<TaskChange> Changes)>();
        foreach (var (task, original) in edits.Where(e => e.Changes.Count > 0))
        {
            var changes = await WithMilestoneAsync(task, original).ConfigureAwait(false);
            await store.ApplyEditAsync(task.ItemId, changes).ConfigureAwait(false);
            applied.Add((task, changes));
        }

        History.Record(new EditStep([.. applied.Select(e => new TaskEdit(e.Task.ItemId, e.Task.Title, e.Changes))]));
        await AfterEditAsync(null).ConfigureAwait(false);
    }

    /// <summary>
    /// マイルストーンを足した・変えた・消したあとに、タスクの所属を付け替える（要件 F-MS-02）。
    /// 変更前の所属が期日から自動で決まっていたタスクは変更後の自動に合わせ、手で選んだものは残す（消えたものは自動に戻す）。
    /// マイルストーンの変更そのものは元に戻す履歴に載らないため、付け替えも履歴に載せない。
    /// </summary>
    /// <param name="tasksBefore">変更前のタスク（GitHub は消した Milestone を Issue から外すため、変更前の所属から決める）。</param>
    public async Task ReassignMilestonesAsync(string projectId, IReadOnlyList<TaskItem> tasksBefore, IReadOnlyList<Milestone> before)
    {
        ArgumentNullException.ThrowIfNull(tasksBefore);
        ArgumentNullException.ThrowIfNull(before);
        if (await store.GetProjectAsync(projectId).ConfigureAwait(false) is not { } project)
        {
            return;
        }

        var previous = tasksBefore.ToDictionary(t => t.ItemId, StringComparer.Ordinal);
        bool any = false;
        foreach (var task in await store.GetTasksAsync(projectId).ConfigureAwait(false))
        {
            if (!MilestonePlan.Applies(task) || !previous.TryGetValue(task.ItemId, out var old))
            {
                continue;
            }

            var next = MilestonePlan.Reassign(old.MilestoneId, old.Target, before, project.Milestones);
            if (next != task.MilestoneId)
            {
                await store.ApplyEditAsync(task.ItemId, [new TaskChange(TaskField.Milestone, task.MilestoneId, next)]).ConfigureAwait(false);
                any = true;
            }
        }

        if (any)
        {
            await AfterEditAsync(null).ConfigureAwait(false);
        }
    }

    /// <summary>直前の操作を元に戻す。戻せる操作がなければ null。</summary>
    public Task<EditOutcome?> UndoAsync() => ReplayAsync(History.PopUndo, History.PushRedo);

    /// <summary>元に戻した操作をやり直す。やり直せる操作がなければ null。</summary>
    public Task<EditOutcome?> RedoAsync() => ReplayAsync(History.PopRedo, History.PushUndo);

    /// <summary>
    /// 覚えた操作を打ち消す変更を反映し、打ち消した内容を反対側の履歴へ積む。
    /// 戻せる項目が 1 つもない操作（その後に GitHub や別の操作で値が変わった、タスクが消えた）は飛ばして、その前の操作を扱う。
    /// </summary>
    private async Task<EditOutcome?> ReplayAsync(Func<EditStep?> pop, Action<EditStep> push)
    {
        while (pop() is { } step)
        {
            var applied = new List<TaskEdit>();
            TaskItem? last = null;
            foreach (var edit in step.Edits.Reverse())
            {
                if (await store.GetTaskAsync(edit.ItemId).ConfigureAwait(false) is not { } current)
                {
                    continue;
                }

                var inverse = EditHistory.Inverse(current, edit.Changes);
                if (inverse.Count == 0)
                {
                    continue;
                }

                last = await store.ApplyEditAsync(edit.ItemId, inverse).ConfigureAwait(false);
                applied.Insert(0, new TaskEdit(edit.ItemId, current.Title, inverse));
            }

            if (applied.Count == 0)
            {
                continue;
            }

            push(new EditStep(applied));
            await AfterEditAsync(applied.Count == 1 ? last : null).ConfigureAwait(false);
            return new EditOutcome(step.Description, last);
        }

        return null;
    }

    private static EditHistory HistoryFor(SqliteTaskStore store)
    {
        var history = new EditHistory();
        store.ItemIdReplaced += (_, ids) => history.ReplaceItemId(ids.OldItemId, ids.NewItemId);
        return history;
    }

    /// <summary>
    /// タスクを作成する。ステータスを指定しない場合は、プロジェクトの Todo の代表（なければ Backlog）とする。
    /// 親と並び順を指定すると、WBS の指定位置に作成する。
    /// </summary>
    /// <param name="body">説明（Markdown）。</param>
    /// <param name="statusOptionId">ステータスの選択肢 ID。</param>
    /// <param name="assignees">担当者のログイン名。</param>
    /// <param name="estimateHours">想定工数（h）。</param>
    /// <param name="start">予定開始日。</param>
    /// <param name="blockedBy">先行タスクの Issue の ID（同じ操作で作ったばかりのタスクでもよい。送信のときに付け替える）。</param>
    public async Task<TaskItem> CreateAsync(Project project, string title, DateOnly? target = null,
        string? parentIssueId = null, double? sortOrder = null, TaskKind kind = TaskKind.Task,
        string? body = null, string? statusOptionId = null, IEnumerable<string>? assignees = null,
        double? estimateHours = null, DateOnly? start = null, IEnumerable<string>? blockedBy = null)
    {
        var repository = project.RepositoryNameWithOwner
            ?? throw new InvalidOperationException("プロジェクトにリポジトリがリンクされていません。");
        var task = SqliteTaskStore.NewLocalTask(
            $"{TaskItem.LocalIdPrefix}{Guid.NewGuid():N}", project.Id,
            new NewTaskPayload(title.Trim(), string.IsNullOrWhiteSpace(body) ? null : body.Trim(), repository), time.GetUtcNow());

        // 区分は必ず書き込む。書き込まないと、取り込み直したときにプロジェクトの種別で決まってしまう
        var initial = new List<TaskChange> { new(TaskField.Kind, null, ProjectConventions.KindOption(kind)) };
        var status = project.StatusOptions.FirstOrDefault(o => o.Id == statusOptionId)
            ?? ProjectConventions.DefaultStatus(project.StatusOptions, StatusCategory.Todo);
        if (status is not null)
        {
            initial.Add(new TaskChange(TaskField.Status, null, status.Id));
        }

        if (body is { Length: > 0 })
        {
            initial.Add(new TaskChange(TaskField.Body, null, body.Trim()));
        }

        if (start is not null)
        {
            initial.Add(new TaskChange(TaskField.Start, null, TaskValues.Date(start)));
        }

        if (target is not null)
        {
            initial.Add(new TaskChange(TaskField.Target, null, TaskValues.Date(target)));
        }

        if (estimateHours is not null)
        {
            initial.Add(new TaskChange(TaskField.Estimate, null, TaskValues.Number(estimateHours)));
        }

        if (TaskValues.Logins(assignees ?? []) is { } logins)
        {
            initial.Add(new TaskChange(TaskField.Assignees, null, logins));
        }

        // 実績開始日は、取りかかった後のカテゴリ（In Progress、Pending）で作ったときに入れる（要件 F-TSK-07）
        if (status?.Category is StatusCategory.InProgress or StatusCategory.Pending)
        {
            initial.Add(new TaskChange(TaskField.ActualStart, null, TaskValues.Date(Today)));
        }

        if (parentIssueId is not null)
        {
            initial.Add(new TaskChange(TaskField.Parent, null, parentIssueId));
        }

        if (sortOrder is not null)
        {
            initial.Add(new TaskChange(TaskField.SortOrder, null, TaskValues.Number(sortOrder)));
        }

        if (TaskValues.IssueIds(blockedBy ?? []) is { } blocking)
        {
            initial.Add(new TaskChange(TaskField.BlockedBy, null, blocking));
        }

        // 計画のタスクは、期日から決まるマイルストーンに入れる（要件 F-MS-02）
        if (project.IsTeam && kind == TaskKind.Task && MilestonePlan.Auto(target, project.Milestones) is { } milestone)
        {
            initial.Add(new TaskChange(TaskField.Milestone, null, milestone.Id));
        }

        var created = await store.AddLocalTaskAsync(task, initial).ConfigureAwait(false);
        await AfterEditAsync(created).ConfigureAwait(false);
        return created;
    }

    public async Task DeleteAsync(TaskItem task)
    {
        await store.DeleteTaskAsync(task).ConfigureAwait(false);
        await AfterEditAsync(null).ConfigureAwait(false);
    }

    /// <summary>
    /// 別のプロジェクトへ移す（要件 F-TSK-03）。ステータスは移動先で同じ名前の選択肢、
    /// なければ同じカテゴリの選択肢に対応させる。
    /// </summary>
    public async Task<TaskItem?> MoveAsync(TaskItem task, Project target)
    {
        if (task.ProjectId == target.Id)
        {
            return task;
        }

        var current = await store.GetProjectAsync(task.ProjectId).ConfigureAwait(false);
        var currentOption = current?.StatusOptions.FirstOrDefault(o => o.Id == task.StatusOptionId);
        var option = target.StatusOptions.FirstOrDefault(o => string.Equals(o.Name, currentOption?.Name, StringComparison.OrdinalIgnoreCase))
            ?? ProjectConventions.DefaultStatus(target.StatusOptions, task.Category)
            ?? target.StatusOptions.FirstOrDefault();

        var changes = new List<TaskChange>();
        if (option is not null)
        {
            changes.Add(new TaskChange(TaskField.Status, null, option.Id));
        }

        // 個人のタスクをチームへ移す場合は、自分のマイタスクに残るよう自分を担当者にする
        if (target.IsTeam && task.Assignees.Count == 0 && currentLogin() is { } me)
        {
            changes.Add(new TaskChange(TaskField.Assignees, null, me));
        }

        // 区分は移動先に合わせる。チームへ移したタスクは、計画に組み込むまでは課題として置く（要件 F-TSK-13）。
        // 計画の中のタスクを移す場合は、置き場所を失うため親子関係と並び順も解く。
        var kind = target.IsTeam ? TaskKind.Issue : TaskKind.Task;
        if (task.Kind != kind)
        {
            changes.Add(new TaskChange(TaskField.Kind, ProjectConventions.KindOption(task.Kind), ProjectConventions.KindOption(kind)));
        }

        if (task.ParentIssueId is not null)
        {
            changes.Add(new TaskChange(TaskField.Parent, task.ParentIssueId, null));
        }

        // マイルストーンは移動元のリポジトリのものなので外す（要件 F-MS-02）
        if (task.MilestoneId is not null)
        {
            changes.Add(new TaskChange(TaskField.Milestone, task.MilestoneId, null));
        }

        var moved = await store.MoveTaskAsync(task, target.Id, changes).ConfigureAwait(false);
        await AfterEditAsync(moved).ConfigureAwait(false);
        return moved;
    }

    public async Task RetryFailedAsync()
    {
        await store.RetryFailedAsync().ConfigureAwait(false);
        await AfterEditAsync(null).ConfigureAwait(false);
    }

    public async Task DiscardFailedAsync()
    {
        await store.DiscardFailedAsync().ConfigureAwait(false);
        Changed?.Invoke(this, null);
        _ = sync.SyncAsync(); // 破棄した分を GitHub の内容で取り直す
    }

    private async Task<TaskItem?> ApplyAsync(TaskItem task, IReadOnlyList<TaskChange> original)
    {
        if (original.Count == 0)
        {
            return task;
        }

        var changes = await WithMilestoneAsync(task, original).ConfigureAwait(false);

        var updated = await store.ApplyEditAsync(task.ItemId, changes).ConfigureAwait(false);
        History.Record(new EditStep([new TaskEdit(task.ItemId, task.Title, changes)]));
        await AfterEditAsync(updated).ConfigureAwait(false);
        return updated;
    }

    /// <summary>
    /// 期日や区分を変えたときに、マイルストーンの所属を決める変更を足す（要件 F-MS-02）。
    /// 所属は約束のため期日を変えても動かさず、初めて期日が入ったときと、課題を計画へ入れたときだけ期日から決める。
    /// マイルストーンを直接選んだ変更には足さない。
    /// </summary>
    private async Task<IReadOnlyList<TaskChange>> WithMilestoneAsync(TaskItem task, IReadOnlyList<TaskChange> changes)
    {
        if (changes.Any(c => c.Field == TaskField.Milestone) || !changes.Any(c => c.Field is TaskField.Target or TaskField.Kind))
        {
            return changes;
        }

        if (await store.GetProjectAsync(task.ProjectId).ConfigureAwait(false) is not { IsTeam: true, Milestones.Count: > 0 } project)
        {
            return changes;
        }

        var after = changes.Aggregate(task, (t, c) => TaskValues.Apply(t, c, project.StatusOptions));
        if (!MilestonePlan.Applies(after))
        {
            return changes;
        }

        var next = MilestonePlan.Applies(task)
            ? MilestonePlan.Follow(task.MilestoneId, task.Target, after.Target, project.Milestones)
            : task.MilestoneId ?? MilestonePlan.Auto(after.Target, project.Milestones)?.Id;
        return next == task.MilestoneId ? changes : [.. changes, new TaskChange(TaskField.Milestone, task.MilestoneId, next)];
    }

    /// <summary>
    /// 親タスクのステータスを子に合わせる（<see cref="ParentStatus"/>）。子の増減・移動・ステータスの変更のどれのあとでも、
    /// その時点の子から求め直す。元に戻す履歴には積まない（子の変更を戻せば、親もまた子から求め直される）。
    /// </summary>
    /// <returns>直した親があれば true。</returns>
    public async Task<bool> ReconcileParentsAsync()
    {
        bool any = false;
        foreach (var project in await store.GetProjectsAsync().ConfigureAwait(false))
        {
            var plan = TaskTree.Build((await store.GetTasksAsync(project.Id).ConfigureAwait(false)).Where(t => t.IsPlanned));
            foreach (var (task, changes) in ParentStatus.Reconcile(plan, project.StatusOptions, Today))
            {
                await store.ApplyEditAsync(task.ItemId, changes).ConfigureAwait(false);
                any = true;
            }
        }

        return any;
    }

    /// <summary>同期で取り込んだあと（GitHub 上で子が変わったときなど）に、親タスクのステータスを子に合わせて送る。</summary>
    public async Task ReconcileAfterSyncAsync()
    {
        if (await ReconcileParentsAsync().ConfigureAwait(false))
        {
            Changed?.Invoke(this, null);
            _ = sync.PushAsync();
        }
    }

    private async Task AfterEditAsync(TaskItem? task)
    {
        await ReconcileParentsAsync().ConfigureAwait(false);
        Changed?.Invoke(this, task);
        await sync.RefreshCountsAsync().ConfigureAwait(false);
        _ = sync.PushAsync();
    }
}

/// <summary>元に戻した（やり直した）操作。</summary>
/// <param name="Description">操作の短い説明。</param>
/// <param name="Task">変えたタスク（複数のタスクにまたがるときは最後に変えたもの）。</param>
public sealed record EditOutcome(string Description, TaskItem? Task);
