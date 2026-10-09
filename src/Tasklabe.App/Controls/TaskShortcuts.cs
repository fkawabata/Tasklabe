using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Tasklabe.App.Services;
using Tasklabe.App.ViewModels;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Editing;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace Tasklabe.App.Controls;

/// <summary>ショートカットの対象になるタスクと、ピッカーを出す位置。</summary>
/// <param name="Task">フォーカスのあるタスク（1 件にしか効かない操作の対象）。</param>
/// <param name="Selection">複数を選んでいるときの、選んでいるすべてのタスク（UX 規約 UX-07）。</param>
public sealed record TaskTarget(TaskItem Task, FrameworkElement Anchor, Point? Position = null, WbsView? Wbs = null,
    IReadOnlyList<TaskItem>? Selection = null)
{
    /// <summary>操作の対象のすべて。</summary>
    public IReadOnlyList<TaskItem> Tasks => Selection is { Count: > 1 } ? Selection : [Task];

    public bool IsMultiple => Tasks.Count > 1;
}

/// <summary>
/// 選んでいるタスクへの操作（要件 F-KEY-02、06、UX 規約 UX-13、UX-19）。キー、値のセル、カード、右クリックのメニュー、
/// コマンドパレットのどこから始めても、この操作を通す。画面を移らない操作は、ピッカーをその場に出して選ばせる。
/// 複数を選んでいるときは、選んでいるすべてに効かせ、1 回の操作として元に戻せるようにする。
/// </summary>
public static class TaskShortcuts
{
    /// <summary>子タスクを持てるか。課題は計画に入れるまで階層を持たない（要件 F-TSK-08）。</summary>
    public static bool CanHaveChildren(TaskItem task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return task.Kind == TaskKind.Task;
    }

    /// <summary>1 件にしか効かない操作。複数を選んでいるときは、フォーカスのあるタスクにだけ効く。</summary>
    public static bool IsSingleOnly(string actionId) =>
        actionId is "task.detail" or "task.addChild" or "task.rename" or "task.predecessor" or "task.openInBrowser" or "task.copyUrl" or "task.branch";

    /// <summary>
    /// フォーカスのある要素から、選んでいるタスクを求める。一覧・表・カンバン・ガント・詳細パネルのどれでもよい。
    /// </summary>
    public static TaskTarget? FocusedTask(XamlRoot root)
    {
        WbsView? wbs = null;
        TaskTarget? found = null;
        IReadOnlyList<TaskItem>? selection = null;
        foreach (var element in VisualTree.FocusedAncestors(root))
        {
            if (found is null && element is FrameworkElement fe)
            {
                // 一覧の行（ListViewItem）は DataContext ではなく Content に行のデータを持つ
                var data = (fe as ContentControl)?.Content is { } content and not UIElement ? content : fe.DataContext;
                found = data switch
                {
                    TaskRowViewModel row => new TaskTarget(row.Task, fe),
                    WbsRowViewModel row => new TaskTarget(row.Task, fe),
                    KanbanCard card => new TaskTarget(card.Task, fe),
                    _ => fe switch
                    {
                        GanttView { SelectedTask: { } t } g => new TaskTarget(t, g, g.SelectedRowPoint),
                        TaskDetailPane { CurrentTask: { } t } p => new TaskTarget(t, p),
                        _ => null,
                    },
                };
            }

            // 複数を選んでいれば、その選択を操作の対象にする
            if (found is not null && selection is null)
            {
                selection = element switch
                {
                    KanbanView kanban => kanban.SelectedTasks,
                    ListViewBase list when list.SelectedItems.Count > 1 => [.. list.SelectedItems.Select(TaskOf).OfType<TaskItem>()],
                    _ => null,
                };
            }

            wbs ??= element as WbsView;
        }

        if (found is null)
        {
            return null;
        }

        bool includesFocused = selection?.Any(t => t.ItemId == found.Task.ItemId) == true;
        return found with { Wbs = wbs, Selection = includesFocused ? selection : null };
    }

    private static TaskItem? TaskOf(object item) => item switch
    {
        TaskRowViewModel row => row.Task,
        WbsRowViewModel row => row.Task,
        KanbanCard card => card.Task,
        _ => null,
    };

    /// <summary>操作を実行する。この操作の対象にならないタスクなら false。</summary>
    public static async Task<bool> RunAsync(string actionId, TaskTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        // 詳細パネルでは、パネル全体ではなく、その値の項目のボタンからピッカーを育てる。
        // 計画の表のキーの操作では、行の左端（タイトルの上）ではなく、その値のセルから育てる
        if (target.Anchor is TaskDetailPane pane && pane.FieldFor(actionId) is { } field)
        {
            target = target with { Anchor = field, Position = null };
        }
        else if (target is { Wbs: { } wbs, Anchor: ListViewItem, Position: null } && wbs.CellFor(target.Task, actionId) is { } cell)
        {
            target = target with { Anchor = cell };
        }

        var services = App.Current.Services;
        var root = target.Anchor.XamlRoot;

        // 一覧の行が古い値を持っていることがあるため、キャッシュから取り直す
        var tasks = new List<TaskItem>();
        foreach (var t in IsSingleOnly(actionId) ? [target.Task] : target.Tasks)
        {
            tasks.Add(await services.Store.GetTaskAsync(t.ItemId) ?? t);
        }

        var projects = new Dictionary<string, Project>(StringComparer.Ordinal);
        foreach (var id in tasks.Select(t => t.ProjectId).Distinct())
        {
            if (await services.Store.GetProjectAsync(id) is { } p)
            {
                projects[id] = p;
            }
        }

        tasks.RemoveAll(t => !projects.ContainsKey(t.ProjectId));
        if (tasks.Count == 0)
        {
            return false;
        }

        var task = tasks[0];
        var project = projects[task.ProjectId];
        bool many = tasks.Count > 1;
        string Subject(string verb) => many ? $"{tasks.Count} 件の{verb}" : $"「{task.Title}」の{verb}";

        switch (actionId)
        {
            case "task.detail":
                App.Current.Shell?.SelectTask(task);
                return true;

            case "task.addChild":
                // 計画の表ではその場に子の行を足し、ほかの画面では詳細パネルのサブタスクの入力欄から足す（要件 F-TSK-04）
                if (!CanHaveChildren(task))
                {
                    return false;
                }

                if (target.Wbs is { } table)
                {
                    await table.InsertChildAsync(task);
                }
                else
                {
                    App.Current.Shell?.RequestSubtask(task);
                }

                return true;

            case "task.toggleDone":
            {
                var own = await WithoutParentsAsync(tasks);
                if (own.Count > 0)
                {
                    await TaskCommands.ApplyAsync([.. own.Select(t => (t, TaskRules.ToggleDone(t, projects[t.ProjectId].StatusOptions, Today)))],
                        own.Count > 1 ? $"{own.Count} 件の完了を切り替えました" : null);
                }

                ReportParents(tasks.Count - own.Count, own.Count == 0);
                return true;
            }

            case "task.status":
            {
                var own = await WithoutParentsAsync(tasks);
                if (own.Count == 0)
                {
                    ReportParents(tasks.Count, all: true);
                    return true;
                }

                if (await ValuePickers.StatusAsync(target.Anchor, target.Position, [.. projects.Values], [.. own.Select(t => (t.StatusName, t.Category))]) is { } status)
                {
                    await TaskCommands.ApplyAsync([.. own.Select(t => (t,
                        status.Value.In(projects[t.ProjectId]) is { } option ? TaskRules.ChangeStatus(t, option, Today) : (IReadOnlyList<TaskChange>)[]))],
                        status.Value.Message(own.Count, projects.Values));
                    ReportParents(tasks.Count - own.Count, all: false);
                }

                return true;
            }

            case "task.progress":
            {
                // 完了・中止したタスクの進捗率は変えられない
                var open = tasks.Where(t => !t.IsDone).ToList();
                if (open.Count == 0)
                {
                    App.Current.Shell?.ShowInfo("進捗率を変えられません", "完了・中止したタスクの進捗率は 100 % に固定しています。");
                    return true;
                }

                if (await ValuePickers.ProgressAsync(target.Anchor, target.Position, [.. open.Select(t => t.EffectiveProgress)]) is { } progress)
                {
                    await TaskCommands.ApplyAsync([.. open.Select(t => (t, TaskRules.Set(t, TaskField.Progress, TaskValues.Number(progress.Value))))],
                        many ? $"{Subject("進捗率")}を {progress.Value:0}% にしました" : null);
                    ReportSkipped(tasks.Count - open.Count, "完了・中止したタスク");
                }

                return true;
            }

            case "task.assign":
            {
                // 担当者を持つのはチームのプロジェクトのタスクだけ
                var team = tasks.Where(t => projects[t.ProjectId].IsTeam).ToList();
                if (team.Count == 0)
                {
                    return false;
                }

                var choice = await ValuePickers.AssigneesAsync(target.Anchor, target.Position,
                    team.Select(t => projects[t.ProjectId]), [.. team.Select(t => t.Assignees)]);
                if (choice is not null)
                {
                    await TaskCommands.ApplyAsync([.. team.Select(t => (t, TaskRules.Set(t, TaskField.Assignees, TaskValues.Logins(choice.ApplyTo(t.Assignees)))))],
                        many ? $"{Subject("担当者")}を変えました" : null);
                    ReportSkipped(tasks.Count - team.Count, "個人のプロジェクトのタスク");
                }

                return true;
            }

            case "task.assignMe" when services.CurrentSettings.UserLogin is { } me:
            {
                var team = tasks.Where(t => projects[t.ProjectId].IsTeam).ToList();
                if (team.Count == 0)
                {
                    return false;
                }

                // 全員に自分が付いていれば外し、そうでなければ全員に付ける
                bool remove = team.All(t => t.Assignees.Contains(me, StringComparer.OrdinalIgnoreCase));
                await TaskCommands.ApplyAsync([.. team.Select(t => (t, TaskRules.Set(t, TaskField.Assignees, TaskValues.Logins(remove
                    ? t.Assignees.Where(a => !string.Equals(a, me, StringComparison.OrdinalIgnoreCase))
                    : t.Assignees.Append(me).Distinct(StringComparer.OrdinalIgnoreCase)))))],
                    many ? (remove ? $"{team.Count} 件の担当から自分を外しました" : $"{team.Count} 件を自分の担当にしました") : null);
                return true;
            }

            case "task.due":
                if (await ValuePickers.DateAsync(target.Anchor, target.Position, "期日（予定終了日）", [.. tasks.Select(t => t.Target)]) is { } due)
                {
                    await TaskCommands.ApplyAsync([.. tasks.Select(t => (t, TaskRules.Set(t, TaskField.Target, TaskValues.Date(due.Value))))],
                        many ? $"{Subject("期日")}を変えました" : null);
                }

                return true;

            case "task.estimate":
                if (await ValuePickers.EstimateAsync(target.Anchor, target.Position, [.. tasks.Select(t => t.EstimateHours)]) is { } estimate)
                {
                    await TaskCommands.ApplyAsync([.. tasks.Select(t => (t, TaskRules.Set(t, TaskField.Estimate,
                        estimate.Value is { } h ? TaskValues.Number(h) : null)))],
                        many ? $"{Subject("想定工数")}を変えました" : null);
                }

                return true;

            case "task.rename":
                if (target.Wbs is { } wbs)
                {
                    wbs.EditTitle();
                }
                else
                {
                    App.Current.MainWindow?.EditTitleInDetail(task);
                }

                return true;

            case "task.plan":
            {
                // フォーカスのあるタスクの区分で、計画に移すか課題へ戻すかを決める。区分の違うものは変えない
                var same = tasks.Where(t => projects[t.ProjectId].IsTeam && t.Kind == task.Kind
                    && t.ProjectId == task.ProjectId).ToList();
                if (same.Count == 0)
                {
                    return false;
                }

                if (task.Kind == TaskKind.Issue)
                {
                    await TaskCommands.PromoteAsync(root, same);
                }
                else
                {
                    await TaskCommands.DemoteAsync(same);
                }

                ReportSkipped(tasks.Count - same.Count, "区分やプロジェクトの違うタスク");
                return true;
            }

            case "task.predecessor" when project.IsTeam && task.IsPlanned:
                if (await ValuePickers.PredecessorAsync(target.Anchor, target.Position, task) is { } pred)
                {
                    await services.Edits.SetBlockedByAsync(task, task.BlockedBy.Append(pred.Value.IssueId));
                }

                return true;

            case "task.project":
                if (await ValuePickers.ProjectAsync(target.Anchor, target.Position, "別のプロジェクトへ移す",
                    [.. tasks.Select(t => t.ProjectId).Distinct()], excludeCurrent: !many) is { } destination)
                {
                    await TaskCommands.MoveAsync(tasks, destination.Value);
                }

                return true;

            // 作業するリポジトリ（要件 F-TSK-18）。複数を選んでいるときは、選ぶ・外すをすべてに当てはめる
            case "task.repository":
                if (await ValuePickers.RepositoriesAsync(target.Anchor, target.Position, [.. tasks.Select(t => t.Repositories)]) is { } repositories)
                {
                    await TaskCommands.ApplyAsync([.. tasks.Select(t => (t, TaskRules.Set(t, TaskField.Repositories, TaskValues.Repositories(repositories.ApplyTo(t.Repositories)))))],
                        many ? $"{Subject("作業するリポジトリ")}を変えました" : null);
                }

                return true;

            case "task.branch":
                await Views.Dialogs.BranchFlow.ShowAsync(target.Anchor, target.Position, task);
                return true;

            case "task.openInBrowser" when task.Url is { } url:
                Browser.Open(url);
                return true;

            // 番号（例: TLB-123）で呼べるよう、選んでいるすべての番号を 1 行ずつコピーする（要件 F-TSK-17）
            case "task.copyKey":
                var keys = tasks.Select(TaskKeys.Of).Where(k => k.Length > 0).ToList();
                if (keys.Count == 0)
                {
                    App.Current.Shell?.ShowToast("GitHub に送るまで番号はありません");
                    return true;
                }

                var keyPackage = new DataPackage();
                keyPackage.SetText(string.Join(Environment.NewLine, keys));
                Clipboard.SetContent(keyPackage);
                App.Current.Shell?.ShowToast(keys.Count == 1 ? $"{keys[0]} をコピーしました" : $"{keys.Count} 件の番号をコピーしました");
                ReportSkipped(tasks.Count - keys.Count, "GitHub にまだ送っていないタスク");
                return true;

            case "task.copyUrl" when task.Url is { } url:
                var package = new DataPackage();
                package.SetText(url);
                Clipboard.SetContent(package);
                App.Current.Shell?.ShowToast("Issue の URL をコピーしました");
                return true;

            case "task.delete":
                await TaskCommands.ConfirmAndDeleteAsync(root, tasks, target.Anchor as Control);
                return true;

            default:
                return false;
        }
    }

    private static DateOnly Today => AppClock.Today;

    /// <summary>
    /// 子を持たないタスクだけを返す。子を持つタスク（親タスク）のステータスは子から決まり、手では変えない（ParentStatus）。
    /// </summary>
    internal static async Task<List<TaskItem>> WithoutParentsAsync(IReadOnlyList<TaskItem> tasks)
    {
        var parents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var projectId in tasks.Select(t => t.ProjectId).Distinct())
        {
            parents.UnionWith((await App.Current.Services.Store.GetTasksAsync(projectId)).Select(t => t.ParentIssueId).OfType<string>());
        }

        return [.. tasks.Where(t => !parents.Contains(t.IssueId))];
    }

    /// <summary>親タスクのステータスを変えなかったことを知らせる。</summary>
    /// <param name="all">対象がすべて親タスクだった。</param>
    internal static void ReportParents(int count, bool all)
    {
        if (count > 0)
        {
            App.Current.Shell?.ShowInfo(all ? "ステータスを変えられません" : $"{count} 件は変更できませんでした",
                "子を持つタスクのステータスは、子のステータスから決まります。");
        }
    }

    /// <summary>複数への操作で変えなかったものがあれば、件数と理由を知らせる（UX 規約 UX-07）。</summary>
    private static void ReportSkipped(int count, string what)
    {
        if (count > 0)
        {
            App.Current.Shell?.ShowInfo($"{count} 件は変更できませんでした", $"{what}は、この操作の対象になりません。");
        }
    }
}
