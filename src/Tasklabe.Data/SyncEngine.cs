using System.Net;
using System.Text.Json;
using Tasklabe.Core.Abstractions;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Editing;
using Tasklabe.GitHub;

namespace Tasklabe.Data;

/// <summary>
/// GitHub との同期（技術設計書 3 章）。送信キューを送ってから、変更のあった Project を取得する。
/// </summary>
public sealed class SyncEngine(GitHubApi api, SqliteTaskStore store, TimeProvider time)
{
    /// <summary>Issue の更新検出で、端末と GitHub の時計のずれを吸収する幅。</summary>
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    /// <summary>Move のときに移動先へ引き継ぐ項目（ステータスは別に送る）。</summary>
    private static readonly TaskField[] CarriedFields =
        [TaskField.Start, TaskField.Target, TaskField.ActualStart, TaskField.ActualEnd, TaskField.Estimate, TaskField.Progress, TaskField.Kind, TaskField.Repositories, TaskField.Branches];

    /// <summary>一時的な障害で送れなかったときに、失敗として示すまでの試行回数。</summary>
    public const int MaxTransientAttempts = 5;

    /// <summary>直近の取得で取り込めなかったプロジェクト。</summary>
    private readonly List<string> _pullProblems = [];

    /// <summary>調査用の記録（送信の失敗や取得できなかったプロジェクト）。アプリはログに書く。</summary>
    public event EventHandler<string>? Diagnostic;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _pushRequested;

    public SyncStatus Status { get; private set; } = new(SyncState.Idle, null);

    public event EventHandler<SyncStatus>? StatusChanged;

    /// <summary>キャッシュの内容が変わった。</summary>
    public event EventHandler? DataChanged;

    /// <summary>トークンが無効になった。再サインインが必要。引数は理由。</summary>
    public event EventHandler<string>? AuthenticationFailed;

    /// <summary>他の利用者の変更を上書きした（要件 F-SYNC-04）。</summary>
    public event EventHandler<TaskConflict>? ConflictDetected;

    /// <summary>送信に失敗した変更が生じた。</summary>
    public event EventHandler<string>? SendFailed;

    /// <summary>Organization のプロジェクトにアクセスできなくなった（新たに問題を検出したときだけ通知する）。</summary>
    public event EventHandler<Organization>? OrganizationProblemDetected;

    /// <summary>想定外の例外（ログ出力用）。同期は継続できる状態に戻す。</summary>
    public event EventHandler<Exception>? UnexpectedError;

    /// <summary>送信キューを送り、変更のあった Project を取得する。</summary>
    /// <param name="force">変更の有無にかかわらずすべて取得する。</param>
    public Task SyncAsync(bool force = false, CancellationToken ct = default) => RunAsync(pull: true, force, ct);

    /// <summary>送信キューだけを送る。同期中の場合は、その完了後に送る。</summary>
    public Task PushAsync(CancellationToken ct = default) => RunAsync(pull: false, force: false, ct);

    /// <summary>
    /// ネットワークにつながり直した（要件 F-SYNC-03）。オフラインだった、または送信待ちがあれば、次の定期の同期を待たずに送信キューを送る。
    /// </summary>
    public Task ReconnectedAsync(CancellationToken ct = default) =>
        Status.State == SyncState.Offline || Status.Pending > 0 ? PushAsync(ct) : Task.CompletedTask;

    /// <summary>送信待ちの件数を表示へ反映する。</summary>
    public async Task RefreshCountsAsync()
    {
        var (pending, failed) = await store.GetOutboxCountsAsync().ConfigureAwait(false);
        SetStatus(Status with { Pending = pending, Failed = failed });
    }

    private async Task RunAsync(bool pull, bool force, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            _pushRequested = true; // 実行中の同期が終わったら送る
            await RefreshCountsAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            do
            {
                _pushRequested = false;
                SetStatus(Status with { State = SyncState.Syncing, Message = null });

                bool changed = await PushPendingAsync(ct).ConfigureAwait(false);
                if (pull)
                {
                    changed |= await PullAsync(force, ct).ConfigureAwait(false);
                    pull = false;
                }

                var (pending, failed) = await store.GetOutboxCountsAsync(ct).ConfigureAwait(false);
                // 一部のプロジェクトを取り込めなかったときは、エラーにはせず理由だけを添える
                var note = _pullProblems.Count > 0 ? $"次のプロジェクトを取得できませんでした: {string.Join("、", _pullProblems)}" : null;
                SetStatus(new SyncStatus(SyncState.Idle, time.GetUtcNow(), note, pending, failed));

                if (changed)
                {
                    DataChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            while (_pushRequested);
        }
        catch (GitHubUnavailableException)
        {
            await SetStateWithCountsAsync(SyncState.Offline, null).ConfigureAwait(false);
        }
        catch (GitHubAuthenticationException ex)
        {
            await SetStateWithCountsAsync(SyncState.Error, ex.Message).ConfigureAwait(false);
            AuthenticationFailed?.Invoke(this, ex.Message);
        }
        catch (OperationCanceledException)
        {
            await SetStateWithCountsAsync(SyncState.Idle, null).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is GitHubException or HttpRequestException)
        {
            await SetStateWithCountsAsync(SyncState.Error, ex.Message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await SetStateWithCountsAsync(SyncState.Error, "同期中に予期しないエラーが発生しました。").ConfigureAwait(false);
            UnexpectedError?.Invoke(this, ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ================================================================ 送信（技術設計書 3.1 節）

    /// <summary>送信キューを順に送る。送れなかった変更は、一時的な障害なら次に送り直し、そうでなければ送信失敗として残す（要件 F-SYNC-06）。</summary>
    /// <returns>キャッシュの内容を変えた（ID の置き換えなど）場合は true。</returns>
    private async Task<bool> PushPendingAsync(CancellationToken ct)
    {
        bool changed = false;
        while (await store.GetNextPendingAsync(ct).ConfigureAwait(false) is { } entry)
        {
            try
            {
                changed |= await SendAsync(entry, ct).ConfigureAwait(false);
                await store.CompleteOutboxAsync(entry.Id, ct).ConfigureAwait(false);
            }
            catch (GitHubHttpException ex) when (IsTransient(ex.StatusCode) && entry.Attempts + 1 < MaxTransientAttempts)
            {
                // 一時的な障害: 記録して次回の同期で再送する（後ろの変更の順序を保つため、ここで止める）
                await store.RecordAttemptAsync(entry.Id, ex.Message, ct).ConfigureAwait(false);
                Diagnostic?.Invoke(this, $"送信を後で再試行します（{entry.Kind} {entry.ItemId}、{entry.Attempts + 1} 回目）: {ex.Message}");
                break;
            }
            catch (Exception ex) when (IsGone(ex) && entry.Kind is OutboxKind.Field or OutboxKind.Delete
                && !entry.ItemId.StartsWith(TaskItem.LocalIdPrefix, StringComparison.Ordinal))
            {
                // 対象が GitHub 上でもう無い（削除・Project から除外）: 反映する先がないため捨て、次の同期で取り直す
                await store.CompleteOutboxAsync(entry.Id, ct).ConfigureAwait(false);
                await store.InvalidateProjectAsync(entry.ProjectId, ct).ConfigureAwait(false);
                Diagnostic?.Invoke(this, $"GitHub 上に無い対象への変更を破棄しました（{entry.Kind} {entry.ItemId} {entry.Field}）: {ex.Message}");
                changed = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not GitHubUnavailableException and not GitHubAuthenticationException)
            {
                // 再送しても成功しない、または想定外の応答: 失敗として残し、利用者に再送か破棄を選んでもらう。
                // 1 件の失敗で後ろの変更まで止めないよう、送信は続ける
                var message = ex is GitHubHttpException { StatusCode: var status } && IsTransient(status)
                    ? $"GitHub が応答しない状態が続いたため、変更を反映できませんでした（{ex.Message}）。"
                    : Describe(ex);
                await store.FailOutboxAsync(entry, message, ct).ConfigureAwait(false);
                Diagnostic?.Invoke(this, $"送信に失敗しました（{entry.Kind} {entry.ItemId} {entry.Field}）: {ex}");
                SendFailed?.Invoke(this, message);
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>送信失敗の理由を利用者向けの文に直す。</summary>
    public static string Describe(Exception ex) => ex switch
    {
        GitHubAccessException { Problem: OrganizationAccessProblem.OAuthAppNotApproved } =>
            "組織が Tasklabe の利用を承認していないため、変更を反映できませんでした。",
        GitHubAccessException { Problem: OrganizationAccessProblem.SamlSsoRequired } =>
            "組織のシングルサインオン（SAML）の承認が必要なため、変更を反映できませんでした。",
        GitHubGraphQLException g when g.ErrorTypes.Contains("FORBIDDEN") =>
            "このプロジェクトまたはリポジトリを編集する権限がないため、変更を反映できませんでした。",
        GitHubGraphQLException g when g.ErrorTypes.Contains("NOT_FOUND") =>
            "対象のタスクまたはプロジェクトが GitHub 上に見つかりません。削除された可能性があります。",
        GitHubException => ex.Message,
        _ => "GitHub から想定外の応答が返ったため、変更を反映できませんでした。",
    };

    /// <summary>対象（タスク・Issue・アイテム）が GitHub 上にもう無いことを示すエラーか。</summary>
    private static bool IsGone(Exception ex) =>
        ex is GitHubGraphQLException g && (g.ErrorTypes.Contains("NOT_FOUND")
            || g.Message.Contains("Could not resolve to a node", StringComparison.OrdinalIgnoreCase));

    private static bool IsTransient(HttpStatusCode status) =>
        (int)status >= 500 || status is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden;

    /// <returns>キャッシュの内容を変えた（ID の置き換えなど）場合は true。</returns>
    private async Task<bool> SendAsync(OutboxEntry e, CancellationToken ct)
    {
        switch (e.Kind)
        {
            case OutboxKind.Create:
                await SendCreateAsync(e, ct).ConfigureAwait(false);
                return true;
            case OutboxKind.Field when e.Field is { } field:
                await SendFieldAsync(e, field, ct).ConfigureAwait(false);
                return false;
            case OutboxKind.Delete:
                await SendDeleteAsync(e, ct).ConfigureAwait(false);
                return false;
            case OutboxKind.Move:
                await SendMoveAsync(e, ct).ConfigureAwait(false);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Issue を作ってプロジェクトに加え、手元の仮の ID を本当の ID に置き換える。</summary>
    private async Task SendCreateAsync(OutboxEntry e, CancellationToken ct)
    {
        var project = await RequireProjectAsync(e.ProjectId, ct).ConfigureAwait(false);
        var payload = JsonSerializer.Deserialize(e.Payload ?? "{}", OutboxJsonContext.Default.NewTaskPayload)
            ?? throw new GitHubException("作成するタスクの内容を読み取れません。");
        var repositoryId = project.RepositoryId ?? throw new GitHubException("プロジェクトにリポジトリがリンクされていません。");

        // 前回の送信で Issue まで作れていれば、作り直さずに続きから行う（重複を作らない）
        CreatedIssue issue;
        if (payload.CreatedIssueId is { } createdId)
        {
            issue = new CreatedIssue(createdId, payload.CreatedNumber, payload.CreatedUrl);
        }
        else
        {
            issue = await api.CreateIssueAsync(repositoryId, payload.Title, payload.Body, ct).ConfigureAwait(false);
            await store.SaveOutboxPayloadAsync(e.Id, JsonSerializer.Serialize(
                payload with { CreatedIssueId = issue.Id, CreatedNumber = issue.Number, CreatedUrl = issue.Url },
                OutboxJsonContext.Default.NewTaskPayload), ct).ConfigureAwait(false);
        }

        var itemId = await api.AddProjectItemAsync(project.Id, issue.Id, ct).ConfigureAwait(false);
        await store.ReplaceItemIdAsync(e.ItemId, itemId, issue, ct).ConfigureAwait(false);

        // 送っているあいだに手元で削除されたタスクは、手元に付け替える行がない。作った Issue を残すと次の同期で戻ってくるため、消す
        if (await store.GetTaskAsync(itemId, ct).ConfigureAwait(false) is null)
        {
            await DiscardIssueAsync(project.Id, itemId, issue.Id, ct).ConfigureAwait(false);
        }
    }

    /// <summary>項目の変更を送る。</summary>
    private async Task SendFieldAsync(OutboxEntry e, TaskField field, CancellationToken ct)
    {
        var project = await RequireProjectAsync(e.ProjectId, ct).ConfigureAwait(false);
        var task = await store.GetTaskAsync(e.ItemId, ct).ConfigureAwait(false);
        var issueId = e.IssueId ?? task?.IssueId ?? throw new GitHubException("タスクの Issue が見つかりません。");

        await DetectConflictAsync(e, field, task, ct).ConfigureAwait(false);

        switch (field)
        {
            case TaskField.Title:
                await api.UpdateIssueTitleAsync(issueId, e.NewValue ?? "", ct).ConfigureAwait(false);
                break;
            case TaskField.Body:
                await api.UpdateIssueBodyAsync(issueId, e.NewValue ?? "", ct).ConfigureAwait(false);
                break;
            case TaskField.State:
                bool close = e.NewValue is TaskValues.Closed or TaskValues.ClosedNotPlanned;
                if (close && e.OldValue is TaskValues.Closed or TaskValues.ClosedNotPlanned)
                {
                    // 閉じたままでは理由（完了・対応しない）を変えられないため、いったん開き直す
                    await api.SetIssueClosedAsync(issueId, closed: false, ct: ct).ConfigureAwait(false);
                }

                await api.SetIssueClosedAsync(issueId, close, notPlanned: e.NewValue == TaskValues.ClosedNotPlanned, ct: ct).ConfigureAwait(false);
                break;
            case TaskField.Assignees:
                // GitHub の利用者は Issue の Assignees へ、名前だけのメンバーは Project のフィールドへ、変わったほうだけ送る
                var oldAssignees = TaskValues.ParseLogins(e.OldValue);
                var newAssignees = TaskValues.ParseLogins(e.NewValue);
                var newLogins = People.Logins(newAssignees);
                if (e.OldValue is null || TaskValues.Logins(People.Logins(oldAssignees)) != TaskValues.Logins(newLogins))
                {
                    var userIds = await ResolveUserIdsAsync(task, newLogins, ct).ConfigureAwait(false);
                    await api.SetAssigneesAsync(issueId, userIds, ct).ConfigureAwait(false);
                }

                if (People.GuestNames(newAssignees) is var guests && guests != People.GuestNames(oldAssignees))
                {
                    await api.SetGuestAssigneesAsync(project, e.ItemId, guests, ct).ConfigureAwait(false);
                }

                break;
            case TaskField.Parent:
                // 別のリポジトリへ移した Issue は作り直しているため、元の親子関係は既に切れている
                if (e.NewValue is { } parentId)
                {
                    await IgnoreIfSettledAsync(() => api.AddSubIssueAsync(parentId, issueId, ct)).ConfigureAwait(false);
                }
                else if (e.OldValue is { } oldParentId)
                {
                    await IgnoreIfSettledAsync(() => api.RemoveSubIssueAsync(oldParentId, issueId, ct)).ConfigureAwait(false);
                }

                break;
            case TaskField.BlockedBy:
                var before = TaskValues.ParseIssueIds(e.OldValue);
                var now = TaskValues.ParseIssueIds(e.NewValue);

                // 既に同じ依存関係がある（または既に外れている）場合は、その項目だけ読み飛ばす
                foreach (var blocking in now.Except(before))
                {
                    await IgnoreIfSettledAsync(() => api.AddBlockedByAsync(issueId, blocking, ct)).ConfigureAwait(false);
                }

                foreach (var blocking in before.Except(now))
                {
                    await IgnoreIfSettledAsync(() => api.RemoveBlockedByAsync(issueId, blocking, ct)).ConfigureAwait(false);
                }

                break;
            case TaskField.Milestone:
                await api.SetIssueMilestoneAsync(issueId, e.NewValue, ct).ConfigureAwait(false);
                break;
            case TaskField.SortOrder:
                // 並び順は兄弟の間でだけ意味を持つため、直前の兄弟の後ろへ移す
                var after = await PreviousSiblingAsync(task, ct).ConfigureAwait(false);
                await api.MoveItemAfterAsync(project.Id, e.ItemId, after, ct).ConfigureAwait(false);
                break;
            default:
                await api.SetFieldAsync(project, e.ItemId, field, e.NewValue, ct).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>手元で削除したタスクの Issue を消す。</summary>
    private async Task SendDeleteAsync(OutboxEntry e, CancellationToken ct)
    {
        var issueId = e.IssueId ?? throw new GitHubException("タスクの Issue が見つかりません。");
        await DiscardIssueAsync(e.ProjectId, e.ItemId, issueId, ct).ConfigureAwait(false);
    }

    /// <summary>タスクを別のプロジェクトへ移す。</summary>
    private async Task SendMoveAsync(OutboxEntry e, CancellationToken ct)
    {
        var target = await RequireProjectAsync(e.ProjectId, ct).ConfigureAwait(false);
        var task = await store.GetTaskAsync(e.ItemId, ct).ConfigureAwait(false);
        var issueId = e.IssueId ?? throw new GitHubException("タスクの Issue が見つかりません。");

        // リポジトリが異なる場合: 所有者が同じなら Issue を転送し（履歴を保つ）、
        // 所有者が異なる場合は GitHub が転送を許さないため、移動先に複製して元の Issue を削除する
        CreatedIssue? transferred = null;
        string? originalIssueId = null;
        var progress = e.Payload is { Length: > 0 } json ? JsonSerializer.Deserialize(json, OutboxJsonContext.Default.MoveProgress) : null;
        if (progress is not null)
        {
            // 前回の送信で転送・複製まで済んでいれば、続きから行う
            transferred = new CreatedIssue(progress.IssueId, progress.Number, progress.Url);
            originalIssueId = progress.OriginalIssueId;
            issueId = progress.IssueId;
        }
        else if (task is not null && target.RepositoryId is { } targetRepoId
            && !string.Equals(task.RepositoryNameWithOwner, target.RepositoryNameWithOwner, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(Owner(task.RepositoryNameWithOwner), Owner(target.RepositoryNameWithOwner ?? ""), StringComparison.OrdinalIgnoreCase))
            {
                transferred = await api.TransferIssueAsync(issueId, targetRepoId, ct).ConfigureAwait(false);
            }
            else
            {
                transferred = await api.CreateIssueAsync(targetRepoId, task.Title, task.Body, ct).ConfigureAwait(false);
                originalIssueId = issueId;
            }

            issueId = transferred.Id;
            await store.SaveOutboxPayloadAsync(e.Id, JsonSerializer.Serialize(
                new MoveProgress(transferred.Id, transferred.Number, transferred.Url, originalIssueId),
                OutboxJsonContext.Default.MoveProgress), ct).ConfigureAwait(false);
        }

        var newItemId = await api.AddProjectItemAsync(target.Id, issueId, ct).ConfigureAwait(false);
        if (task is not null)
        {
            foreach (var field in CarriedFields)
            {
                if (TaskValues.Get(task, field) is { } value
                    && !(field == TaskField.Kind && value == ProjectConventions.KindOptions.Task)
                    && !(field == TaskField.Progress && value == "0"))
                {
                    await api.SetFieldAsync(target, newItemId, field, value, ct).ConfigureAwait(false);
                }
            }

            // 名前だけのメンバーは Project のフィールドにいるため、Issue と一緒には移らない。チームのプロジェクトへ移すときだけ引き継ぐ
            if (target.IsTeam && People.GuestNames(task.Assignees) is { } guests)
            {
                await api.SetGuestAssigneesAsync(target, newItemId, guests, ct).ConfigureAwait(false);
            }
        }

        if (originalIssueId is not null)
        {
            if (task?.IsClosed == true)
            {
                await api.SetIssueClosedAsync(issueId, closed: true, ct: ct).ConfigureAwait(false);
            }

            // 複製元の Issue を削除する（移動元の Project のアイテムも同時に消える）
            if (!await DeleteIssueIfExistsAsync(originalIssueId, ct).ConfigureAwait(false))
            {
                await api.SetIssueClosedAsync(originalIssueId, closed: true, notPlanned: true, ct: ct).ConfigureAwait(false);
                await DeleteSourceItemAsync(e, ct).ConfigureAwait(false);
            }
        }
        else
        {
            await DeleteSourceItemAsync(e, ct).ConfigureAwait(false);
        }

        await store.ReplaceItemIdAsync(e.ItemId, newItemId, transferred,
            transferred is null ? null : target.RepositoryNameWithOwner, ct).ConfigureAwait(false);
    }

    /// <summary>Issue を削除する。削除の権限がなければ、プロジェクトから外して「対応しない」として閉じる。</summary>
    private async Task DiscardIssueAsync(string projectId, string itemId, string issueId, CancellationToken ct)
    {
        if (!await DeleteIssueIfExistsAsync(issueId, ct).ConfigureAwait(false))
        {
            await api.DeleteProjectItemAsync(projectId, itemId, ct).ConfigureAwait(false);
            await api.SetIssueClosedAsync(issueId, closed: true, notPlanned: true, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 送信前に GitHub 上の値を確かめ、編集時の値から変わっていれば競合として記録する。
    /// 送信は後勝ちで続ける（技術設計書 3.3 節）。
    /// </summary>
    private async Task DetectConflictAsync(OutboxEntry e, TaskField field, TaskItem? task, CancellationToken ct)
    {
        // 作ったばかりのアイテムに書く初めの値は、上書きする相手がいない
        if (field is TaskField.State or TaskField.SortOrder || e.Payload == OutboxPayloads.InitialValue)
        {
            return;
        }

        var remote = await api.GetCurrentValueAsync(e.ItemId, field, ct).ConfigureAwait(false);

        // 区分が GitHub 上で未設定なら、誰も設定していないので上書きではない
        if (field == TaskField.Kind && remote is null)
        {
            return;
        }

        if (!SameValue(remote, e.OldValue) && !SameValue(remote, e.NewValue))
        {
            var title = task?.Title ?? "";
            ConflictDetected?.Invoke(this, new TaskConflict(e.ItemId, title, field, e.NewValue, remote, time.GetUtcNow()));
        }
    }

    /// <summary>既に反映済みだった場合のエラーを読み飛ばして実行する。</summary>
    private static async Task IgnoreIfSettledAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (GitHubGraphQLException ex) when (ex.ErrorTypes.Contains("UNPROCESSABLE") || ex.Message.Contains("already", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    /// <summary>同じ親を持つタスクのうち、並び順が直前のものの item ID（先頭なら null）。</summary>
    private async Task<string?> PreviousSiblingAsync(TaskItem? task, CancellationToken ct)
    {
        if (task is null)
        {
            return null;
        }

        var siblings = await store.GetTasksAsync(task.ProjectId, ct).ConfigureAwait(false);
        return siblings
            .Where(t => t.ParentIssueId == task.ParentIssueId && t.SortOrder < task.SortOrder && !t.IsLocal)
            .OrderByDescending(t => t.SortOrder)
            .FirstOrDefault()?.ItemId;
    }

    /// <summary>Issue を削除する。既に無ければ削除できたものとする。削除の権限がなければ false。</summary>
    private async Task<bool> DeleteIssueIfExistsAsync(string issueId, CancellationToken ct)
    {
        try
        {
            return await api.DeleteIssueAsync(issueId, ct).ConfigureAwait(false);
        }
        catch (GitHubGraphQLException ex) when (IsGone(ex))
        {
            return true;
        }
    }

    private async Task DeleteSourceItemAsync(OutboxEntry e, CancellationToken ct)
    {
        if (e.OldValue is not { } sourceProjectId)
        {
            return;
        }

        try
        {
            await api.DeleteProjectItemAsync(sourceProjectId, e.ItemId, ct).ConfigureAwait(false);
        }
        catch (GitHubGraphQLException ex) when (ex.ErrorTypes.Contains("NOT_FOUND") || ex.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
        {
            // 転送によって移動元のアイテムが既に無くなっている
        }
    }

    private static string Owner(string nameWithOwner) => nameWithOwner.Split('/')[0];

    /// <summary>担当者のログイン名を GitHub の利用者 ID に変換する。キャッシュにない場合は取り直す。</summary>
    private async Task<IReadOnlyList<string>> ResolveUserIdsAsync(TaskItem? task, IReadOnlyList<string> logins, CancellationToken ct)
    {
        if (logins.Count == 0)
        {
            return [];
        }

        var repo = task?.RepositoryNameWithOwner ?? throw new GitHubException("タスクのリポジトリが見つかりません。");
        var users = await store.GetAssignableUsersAsync(repo, ct).ConfigureAwait(false);
        if (logins.Any(l => !users.Any(u => string.Equals(u.Login, l, StringComparison.OrdinalIgnoreCase))))
        {
            users = await api.GetAssignableUsersAsync(repo, ct).ConfigureAwait(false);
            await store.SaveAssignableUsersAsync(repo, users, ct).ConfigureAwait(false);
        }

        return logins.Select(l => users.FirstOrDefault(u => string.Equals(u.Login, l, StringComparison.OrdinalIgnoreCase))?.Id
            ?? throw new GitHubException($"@{l} はこのリポジトリのタスクに割り当てられません。")).ToList();
    }

    private static bool SameValue(string? a, string? b) =>
        string.Equals(a ?? "", b ?? "", StringComparison.Ordinal)
        || (TaskValues.ParseNumber(a) is { } x && TaskValues.ParseNumber(b) is { } y && Math.Abs(x - y) < 1e-9);

    private async Task<Project> RequireProjectAsync(string projectId, CancellationToken ct) =>
        await store.GetProjectAsync(projectId, ct).ConfigureAwait(false)
            ?? throw new GitHubException("プロジェクトが見つかりません。");

    // ================================================================ 取得（技術設計書 3.2 節）

    /// <summary>
    /// 変更のあった Project だけを取り直す（要件 NF-05）。Project の更新日時と、リポジトリの Issue の更新から変更を見つける。
    /// </summary>
    private async Task<bool> PullAsync(bool force, CancellationToken ct)
    {
        var (remote, inaccessibleOwners) = await ListRemoteProjectsAsync(ct).ConfigureAwait(false);
        var states = await store.GetSyncStatesAsync(ct).ConfigureAwait(false);
        var issueChanges = new Dictionary<string, bool>();
        bool changed = false;

        _pullProblems.Clear();
        foreach (var summary in remote)
        {
            // 1 つのプロジェクトの取得に失敗しても、ほかのプロジェクトの同期は続ける（同期全体をエラーにしない）
            try
            {
                var startedAt = time.GetUtcNow();
                states.TryGetValue(summary.Id, out var state);

                bool needsFetch = force || state is null || state.RemoteUpdatedAt != summary.UpdatedAt;
                if (!needsFetch && state is not null && summary.RepositoryNameWithOwner is { } repo)
                {
                    var since = state.LastSyncedAt - ClockSkew;
                    var key = $"{repo}@{since:O}";
                    if (!issueChanges.TryGetValue(key, out needsFetch))
                    {
                        needsFetch = issueChanges[key] = await api.HasIssueChangesSinceAsync(repo, since, ct).ConfigureAwait(false);
                    }
                }

                if (!needsFetch)
                {
                    continue;
                }

                if (state is null)
                {
                    // 初回はフィールド構成を確認し、不足を補う
                    await api.EnsureSchemaAsync(summary.Id, ct).ConfigureAwait(false);
                }

                // 一覧への反映が遅れているアイテムを消さないよう、手元のタスクの ID も渡す
                var known = (await store.GetTasksAsync(summary.Id, ct).ConfigureAwait(false))
                    .Where(t => !t.IsLocal).Select(t => t.ItemId).ToList();

                // ほかの端末やアプリの外で作られたタスクは手元に ID がない。一覧に出る前に取り込みを済ませると、その後は変化なしと
                // 判断して取り直さないため、前回の同期以降に更新された Issue の側から、このプロジェクトのアイテムの ID を拾って渡す
                if (state is not null && summary.RepositoryNameWithOwner is { } repository)
                {
                    known.AddRange(await api.GetProjectItemIdsOfIssuesSinceAsync(
                        repository, summary.Id, state.LastSyncedAt - ClockSkew, ct).ConfigureAwait(false));
                }
                var snapshot = await api.GetProjectAsync(summary.Id, ct, known).ConfigureAwait(false);
                await store.ReplaceProjectAsync(snapshot, startedAt, ct).ConfigureAwait(false);

                // 「対応しない」で閉じたのに Done にされた Status を、中止のステータスへ戻す
                foreach (var (itemId, optionId) in snapshot.StatusRepairs ?? new Dictionary<string, string>())
                {
                    try
                    {
                        await api.SetFieldAsync(snapshot.Project, itemId, TaskField.Status, optionId, ct).ConfigureAwait(false);
                    }
                    catch (GitHubException)
                    {
                        // 次の同期でもう一度試す
                    }
                }

                if (snapshot.Project.IsTeam && snapshot.Project.RepositoryNameWithOwner is { } teamRepo)
                {
                    var users = await api.GetAssignableUsersAsync(teamRepo, ct).ConfigureAwait(false);
                    await store.SaveAssignableUsersAsync(teamRepo, users, ct).ConfigureAwait(false);
                }

                changed = true;
            }
            catch (GitHubException ex) when (ex is not GitHubAuthenticationException and not GitHubUnavailableException)
            {
                _pullProblems.Add(summary.Title);
                Diagnostic?.Invoke(this, $"プロジェクト「{summary.Title}」を取得できませんでした: {ex.Message}");
            }
        }

        // アクセスできなくなった Organization のプロジェクトは、一時的な可能性があるためキャッシュに残す
        var keep = remote.Select(p => p.Id).ToHashSet();
        foreach (var cached in await store.GetProjectsAsync(ct).ConfigureAwait(false))
        {
            if (inaccessibleOwners.Contains(cached.OwnerLogin))
            {
                keep.Add(cached.Id);
            }
        }

        changed |= await store.RemoveProjectsExceptAsync(keep, ct).ConfigureAwait(false) > 0;
        return changed;
    }

    /// <summary>
    /// 利用者が所有する Project と、所属する各 Organization の Project を集める。
    /// アクセスできない Organization は理由を記録し、取得を続ける。
    /// </summary>
    private async Task<(List<ProjectSummary> Projects, HashSet<string> InaccessibleOwners)> ListRemoteProjectsAsync(CancellationToken ct)
    {
        var projects = new List<ProjectSummary>(await api.ListViewerProjectsAsync(ct).ConfigureAwait(false));
        var inaccessible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var previous = (await store.GetOrganizationsAsync(ct).ConfigureAwait(false)).ToDictionary(o => o.Login);
        var organizations = new List<Organization>();
        foreach (var org in await api.ListOrganizationsAsync(ct).ConfigureAwait(false))
        {
            try
            {
                projects.AddRange(await api.ListOrganizationProjectsAsync(org.Login, ct).ConfigureAwait(false));
                organizations.Add(org);
            }
            catch (GitHubAccessException ex)
            {
                var problem = org with { Problem = ex.Problem, ProblemDetail = ex.Message };
                organizations.Add(problem);
                inaccessible.Add(org.Login);
                if (previous.GetValueOrDefault(org.Login)?.Problem != ex.Problem)
                {
                    OrganizationProblemDetected?.Invoke(this, problem);
                }
            }
        }

        await store.SaveOrganizationsAsync(organizations, ct).ConfigureAwait(false);
        return (projects, inaccessible);
    }

    private async Task SetStateWithCountsAsync(SyncState state, string? message)
    {
        var (pending, failed) = await store.GetOutboxCountsAsync().ConfigureAwait(false);
        SetStatus(Status with { State = state, Message = message, Pending = pending, Failed = failed });
    }

    private void SetStatus(SyncStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }
}
