using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tasklabe.Core.Abstractions;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Editing;
using Tasklabe.Core.Settings;
using Tasklabe.GitHub;

namespace Tasklabe.Data;

public sealed record ProjectSyncState(string ProjectId, DateTimeOffset LastSyncedAt, DateTimeOffset RemoteUpdatedAt);

/// <summary>
/// SQLite 上のプロジェクトとタスクのキャッシュ、および送信キュー。
/// 処理は呼び出したスレッドで同期的に行う。編集の読み取りと書き込みを直列にして、続けて行った編集が互いを上書きしないようにするため。
/// </summary>
public sealed class SqliteTaskStore(TasklabeDatabase database) : ITaskStore
{
    /// <summary>GitHub で作成・移動した結果、タスクの ID が変わった（旧 ID, 新 ID）。</summary>
    public event EventHandler<(string OldItemId, string NewItemId)>? ItemIdReplaced;

    private const string TaskColumns = """
        item_id, project_id, issue_id, repository_name, number, title, url, is_closed,
        status_option_id, status_name, category, kind, start, target, actual_start, actual_end,
        estimate_hours, progress, parent_issue_id, updated_at, body, sort_order, blocked_by, non_blocking, repositories, branches, milestone_id
        """;

    // ================================================================ 読み取り

    public Task<IReadOnlyList<Project>> GetProjectsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = database.Open();
        return Task.FromResult<IReadOnlyList<Project>>(QueryProjects(connection, null, null));
    }

    public Task<Project?> GetProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = database.Open();
        return Task.FromResult(QueryProjects(connection, null, projectId).FirstOrDefault());
    }

    public Task<IReadOnlyList<TaskItem>> GetTasksAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = database.Open();
        return Task.FromResult(QueryTasks(connection, null, ""));
    }

    public Task<IReadOnlyList<TaskItem>> GetTasksAsync(string projectId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = database.Open();
        return Task.FromResult(QueryTasks(connection, null, "WHERE project_id = $p", ("$p", projectId)));
    }

    public Task<TaskItem?> GetTaskAsync(string itemId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = database.Open();
        return Task.FromResult(ReadTask(connection, null, itemId));
    }

    public Task<IReadOnlyDictionary<string, ProjectSyncState>> GetSyncStatesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = database.Open();
        var states = Query(connection, null, "SELECT project_id, last_synced_at, remote_updated_at FROM sync_state;",
            r => new ProjectSyncState(r.GetString(0), ParseTimestamp(r.GetString(1)), ParseTimestamp(r.GetString(2))));
        return Task.FromResult<IReadOnlyDictionary<string, ProjectSyncState>>(states.ToDictionary(s => s.ProjectId));
    }

    // ================================================================ 取得結果の反映

    /// <summary>
    /// Project の内容を取得結果で置き換える。未送信の変更は置き換えた後に重ねて適用し、
    /// 利用者の編集が同期で消えないようにする（ローカルファースト）。
    /// </summary>
    public Task ReplaceProjectAsync(ProjectSnapshot snapshot, DateTimeOffset syncedAt, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var p = snapshot.Project;
        using var connection = database.Open();
        using var tx = connection.BeginTransaction();

        Execute(connection, tx, """
            INSERT INTO projects (id, number, title, kind, owner_login, url, repository_id, repository_name, closed, updated_at, settings)
            VALUES ($id, $number, $title, $kind, $owner, $url, $repoId, $repoName, $closed, $updated, $settings)
            ON CONFLICT(id) DO UPDATE SET
                number = excluded.number, title = excluded.title, kind = excluded.kind, owner_login = excluded.owner_login,
                url = excluded.url, repository_id = excluded.repository_id, repository_name = excluded.repository_name,
                closed = excluded.closed, updated_at = excluded.updated_at, settings = excluded.settings;
            """,
            ("$id", p.Id), ("$number", p.Number), ("$title", p.Title), ("$kind", (int)p.Kind), ("$owner", p.OwnerLogin),
            ("$url", p.Url), ("$repoId", p.RepositoryId), ("$repoName", p.RepositoryNameWithOwner),
            ("$closed", p.Closed), ("$updated", FormatTimestamp(p.UpdatedAt)),
            ("$settings", p.Settings.IsEmpty ? null : p.Settings.ToJson()));

        Execute(connection, tx, "DELETE FROM status_options WHERE project_id = $id;", ("$id", p.Id));
        for (int i = 0; i < p.StatusOptions.Count; i++)
        {
            var o = p.StatusOptions[i];
            Execute(connection, tx, """
                INSERT INTO status_options (project_id, id, name, color, category, sort_order)
                VALUES ($project, $id, $name, $color, $category, $order);
                """,
                ("$project", p.Id), ("$id", o.Id), ("$name", o.Name), ("$color", o.Color), ("$category", (int)o.Category), ("$order", i));
        }

        Execute(connection, tx, "DELETE FROM milestones WHERE project_id = $id;", ("$id", p.Id));
        foreach (var m in p.Milestones)
        {
            Execute(connection, tx, """
                INSERT INTO milestones (project_id, id, number, title, due, description, closed)
                VALUES ($project, $id, $number, $title, $due, $description, $closed);
                """,
                ("$project", p.Id), ("$id", m.Id), ("$number", m.Number), ("$title", m.Title), ("$due", TaskValues.Date(m.Due)),
                ("$description", m.Description), ("$closed", m.IsClosed));
        }

        Execute(connection, tx, "DELETE FROM project_fields WHERE project_id = $id;", ("$id", p.Id));
        foreach (var (kind, map) in (ReadOnlySpan<(int, IReadOnlyDictionary<string, string>)>)[(0, p.FieldIds), (1, p.KindOptionIds)])
        {
            foreach (var (name, id) in map)
            {
                Execute(connection, tx, "INSERT INTO project_fields (project_id, kind, name, id) VALUES ($p, $k, $n, $i);",
                    ("$p", p.Id), ("$k", kind), ("$n", name), ("$i", id));
            }
        }

        // この Project へ作成・移動する途中のタスクは、まだ GitHub 側にないため残す
        Execute(connection, tx, """
            DELETE FROM tasks WHERE project_id = $id
                AND item_id NOT IN (SELECT item_id FROM outbox WHERE kind IN (0, 3) AND project_id = $id);
            """, ("$id", p.Id));
        foreach (var t in snapshot.Tasks)
        {
            WriteTask(connection, tx, t);
        }

        Execute(connection, tx, """
            INSERT INTO sync_state (project_id, last_synced_at, remote_updated_at) VALUES ($id, $synced, $remote)
            ON CONFLICT(project_id) DO UPDATE SET last_synced_at = excluded.last_synced_at, remote_updated_at = excluded.remote_updated_at;
            """,
            ("$id", p.Id), ("$synced", FormatTimestamp(syncedAt)), ("$remote", FormatTimestamp(p.UpdatedAt)));

        ReapplyPending(connection, tx);

        tx.Commit();
        return Task.CompletedTask;
    }

    /// <summary>指定以外の Project をキャッシュから削除し、削除した件数を返す。</summary>
    public Task<int> RemoveProjectsExceptAsync(IReadOnlyCollection<string> projectIds, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = database.Open();
        using var tx = connection.BeginTransaction();

        var keep = projectIds.ToHashSet();
        int removed = 0;
        foreach (var id in QueryStrings(connection, tx, "SELECT id FROM projects;").Where(id => !keep.Contains(id)))
        {
            removed += Execute(connection, tx, "DELETE FROM projects WHERE id = $id;", ("$id", id));
        }

        tx.Commit();
        return Task.FromResult(removed);
    }

    /// <summary>キャッシュと送信キューをすべて削除する（サインアウト時。要件 F-AUTH-03）。</summary>
    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = database.Open();
        Execute(connection, null, "DELETE FROM projects; DELETE FROM outbox; DELETE FROM organizations; DELETE FROM assignable_users;");
        return Task.CompletedTask;
    }

    // ================================================================ ローカルでの編集

    /// <summary>変更をキャッシュへ即座に適用し、送信キューに積む。</summary>
    public Task<TaskItem?> ApplyEditAsync(string itemId, IReadOnlyList<TaskChange> changes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = database.Open();
        using var tx = connection.BeginTransaction();

        var task = ReadTask(connection, tx, itemId);
        if (task is not null && changes.Count > 0)
        {
            var options = QueryStatusOptions(connection, tx, task.ProjectId);
            foreach (var change in changes)
            {
                task = TaskValues.Apply(task, change, options);
                Enqueue(connection, tx, OutboxKind.Field, task.ProjectId, task.ItemId, task.IssueId, change.Field, change.OldValue, change.NewValue, null);
            }

            WriteTask(connection, tx, Touched(task, DateTimeOffset.UtcNow));
        }

        tx.Commit();
        return Task.FromResult(task);
    }

    /// <summary>作成待ちのタスクをキャッシュに追加し、作成と初期値の設定を送信キューに積む。</summary>
    public Task<TaskItem> AddLocalTaskAsync(TaskItem task, IReadOnlyList<TaskChange> initialValues, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = database.Open();
        using var tx = connection.BeginTransaction();

        var options = QueryStatusOptions(connection, tx, task.ProjectId);
        var payload = JsonSerializer.Serialize(new NewTaskPayload(task.Title, task.Body, task.RepositoryNameWithOwner), OutboxJsonContext.Default.NewTaskPayload);
        Enqueue(connection, tx, OutboxKind.Create, task.ProjectId, task.ItemId, null, null, null, task.Title, payload);

        foreach (var change in initialValues)
        {
            task = TaskValues.Apply(task, change, options);
            Enqueue(connection, tx, OutboxKind.Field, task.ProjectId, task.ItemId, null, change.Field, change.OldValue, change.NewValue, OutboxPayloads.InitialValue);
        }

        WriteTask(connection, tx, task);
        tx.Commit();
        return Task.FromResult(task);
    }

    /// <summary>タスクを削除する。GitHub へ未作成のタスクは送信キューからも取り除く。</summary>
    public Task DeleteTaskAsync(TaskItem task, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = database.Open();
        using var tx = connection.BeginTransaction();

        Execute(connection, tx, "DELETE FROM tasks WHERE item_id = $id;", ("$id", task.ItemId));
        if (task.IsLocal)
        {
            Execute(connection, tx, "DELETE FROM outbox WHERE item_id = $id;", ("$id", task.ItemId));
        }
        else
        {
            Enqueue(connection, tx, OutboxKind.Delete, task.ProjectId, task.ItemId, task.IssueId, null, null, null, null);
        }

        tx.Commit();
        return Task.CompletedTask;
    }

    /// <summary>
    /// タスクを別の Project へ移す。移動先の Project ではステータスの選択肢が異なるため、
    /// 呼び出し側で求めた移動先のステータスの変更（<paramref name="statusChanges"/>）を合わせて積む。
    /// </summary>
    public Task<TaskItem?> MoveTaskAsync(TaskItem task, string targetProjectId, IReadOnlyList<TaskChange> statusChanges, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = database.Open();
        using var tx = connection.BeginTransaction();

        var current = ReadTask(connection, tx, task.ItemId);
        if (current is not null)
        {
            if (current.IsLocal)
            {
                // 未作成のタスクは作成先を差し替えるだけでよい
                Execute(connection, tx, "UPDATE outbox SET project_id = $p WHERE item_id = $id;", ("$p", targetProjectId), ("$id", current.ItemId));
            }
            else
            {
                Enqueue(connection, tx, OutboxKind.Move, targetProjectId, current.ItemId, current.IssueId, null, current.ProjectId, targetProjectId, null);
            }

            current = current with { ProjectId = targetProjectId, StatusOptionId = null, StatusName = null };
            var options = QueryStatusOptions(connection, tx, targetProjectId);
            foreach (var change in statusChanges)
            {
                current = TaskValues.Apply(current, change, options);
                Enqueue(connection, tx, OutboxKind.Field, targetProjectId, current.ItemId, current.IssueId, change.Field, change.OldValue, change.NewValue, OutboxPayloads.InitialValue);
            }

            current = Touched(current, DateTimeOffset.UtcNow);
            WriteTask(connection, tx, current);
        }

        tx.Commit();
        return Task.FromResult(current);
    }

    // ================================================================ 送信キュー

    public Task<OutboxEntry?> GetNextPendingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = database.Open();
        return Task.FromResult(QueryOutbox(connection, null, "WHERE failed = 0 ORDER BY id LIMIT 1").FirstOrDefault());
    }

    public Task<IReadOnlyList<OutboxEntry>> GetFailedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = database.Open();
        return Task.FromResult<IReadOnlyList<OutboxEntry>>(QueryOutbox(connection, null, "WHERE failed = 1 ORDER BY id"));
    }

    public Task<(int Pending, int Failed)> GetOutboxCountsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = database.Open();
        return Task.FromResult(Query(connection, null, "SELECT COALESCE(SUM(failed = 0), 0), COALESCE(SUM(failed = 1), 0) FROM outbox;",
            r => (r.GetInt32(0), r.GetInt32(1)))[0]);
    }

    public Task CompleteOutboxAsync(long id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = database.Open();
        Execute(connection, null, "DELETE FROM outbox WHERE id = $id;", ("$id", id));
        return Task.CompletedTask;
    }

    /// <summary>一時的な失敗を記録する。次回の同期で再送する。</summary>
    public Task RecordAttemptAsync(long id, string error, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = database.Open();
        Execute(connection, null, "UPDATE outbox SET attempts = attempts + 1, last_error = $e WHERE id = $id;", ("$id", id), ("$e", error));
        return Task.CompletedTask;
    }

    /// <summary>
    /// 再送しても成功しない失敗を記録する。作成または移動に失敗した場合は、
    /// そのタスクの後続の変更も前提が崩れるため、すべて失敗とする。
    /// </summary>
    public Task FailOutboxAsync(OutboxEntry entry, string error, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = database.Open();
        var sql = entry.Kind is OutboxKind.Create or OutboxKind.Move
            ? "UPDATE outbox SET failed = 1, last_error = $e WHERE item_id = $item AND id >= $id;"
            : "UPDATE outbox SET failed = 1, last_error = $e WHERE id = $id;";
        Execute(connection, null, sql, ("$id", entry.Id), ("$item", entry.ItemId), ("$e", error));
        return Task.CompletedTask;
    }

    /// <summary>送信の途中経過を記録する（再送で続きから行うため）。</summary>
    public Task SaveOutboxPayloadAsync(long id, string payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = database.Open();
        Execute(connection, null, "UPDATE outbox SET payload = $p WHERE id = $id;", ("$id", id), ("$p", payload));
        return Task.CompletedTask;
    }

    /// <summary>次の同期でこの Project を取り直させる。</summary>
    public Task InvalidateProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = database.Open();
        Execute(connection, null, "DELETE FROM sync_state WHERE project_id = $p;", ("$p", projectId));
        return Task.CompletedTask;
    }

    public Task RetryFailedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = database.Open();
        Execute(connection, null, "UPDATE outbox SET failed = 0, attempts = 0, last_error = NULL WHERE failed = 1;");
        return Task.CompletedTask;
    }

    /// <summary>送信に失敗した変更を破棄し、影響する Project の同期状態を消して次回に取り直させる。</summary>
    public Task DiscardFailedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = database.Open();
        using var tx = connection.BeginTransaction();
        foreach (var projectId in QueryStrings(connection, tx, "SELECT DISTINCT project_id FROM outbox WHERE failed = 1;"))
        {
            Execute(connection, tx, "DELETE FROM sync_state WHERE project_id = $p;", ("$p", projectId));
        }

        Execute(connection, tx, "DELETE FROM tasks WHERE item_id IN (SELECT item_id FROM outbox WHERE failed = 1 AND kind = 0);");
        Execute(connection, tx, "DELETE FROM outbox WHERE failed = 1;");
        tx.Commit();
        return Task.CompletedTask;
    }

    /// <summary>GitHub で作成・移動した結果の ID を、キャッシュと送信キューへ反映する。</summary>
    public Task ReplaceItemIdAsync(string oldItemId, string newItemId, CreatedIssue? issue = null, CancellationToken cancellationToken = default) =>
        ReplaceItemIdAsync(oldItemId, newItemId, issue, null, cancellationToken);

    /// <param name="repositoryNameWithOwner">Issue を別のリポジトリへ転送した場合の転送先。</param>
    public Task ReplaceItemIdAsync(string oldItemId, string newItemId, CreatedIssue? issue, string? repositoryNameWithOwner, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = database.Open();
        using var tx = connection.BeginTransaction();

        var task = ReadTask(connection, tx, oldItemId);
        if (task is not null)
        {
            Execute(connection, tx, "DELETE FROM tasks WHERE item_id = $id;", ("$id", oldItemId));
            WriteTask(connection, tx, task with
            {
                ItemId = newItemId,
                IssueId = issue?.Id ?? task.IssueId,
                Number = issue?.Number ?? task.Number,
                Url = issue?.Url ?? task.Url,
                RepositoryNameWithOwner = repositoryNameWithOwner ?? task.RepositoryNameWithOwner,
            });
        }

        Execute(connection, tx, "UPDATE outbox SET item_id = $new WHERE item_id = $old;", ("$new", newItemId), ("$old", oldItemId));

        // Issue の ID が変わった場合は、子タスクが参照する親の ID も付け替える
        if (task is not null && issue is not null && task.IssueId != issue.Id)
        {
            Execute(connection, tx, "UPDATE tasks SET parent_issue_id = $new WHERE parent_issue_id = $old;", ("$new", issue.Id), ("$old", task.IssueId));
            Execute(connection, tx, "UPDATE outbox SET new_value = $new WHERE field = $f AND new_value = $old;",
                ("$new", issue.Id), ("$old", task.IssueId), ("$f", (int)TaskField.Parent));
            Execute(connection, tx, "UPDATE outbox SET old_value = $new WHERE field = $f AND old_value = $old;",
                ("$new", issue.Id), ("$old", task.IssueId), ("$f", (int)TaskField.Parent));
            ReplaceBlockingIssueId(connection, tx, task.IssueId, issue.Id);
        }
        if (issue is not null)
        {
            Execute(connection, tx, "UPDATE outbox SET issue_id = $issue WHERE item_id = $new;", ("$new", newItemId), ("$issue", issue.Id));
        }

        tx.Commit();
        ItemIdReplaced?.Invoke(this, (oldItemId, newItemId));
        return Task.CompletedTask;
    }

    /// <summary>先行タスクとして参照している Issue の ID を付け替える（タスクと送信キューの両方）。</summary>
    private static void ReplaceBlockingIssueId(SqliteConnection connection, SqliteTransaction tx, string oldIssueId, string newIssueId)
    {
        string? Replace(string? value) =>
            TaskValues.IssueIds(TaskValues.ParseIssueIds(value).Select(id => id == oldIssueId ? newIssueId : id));

        var pattern = $"%{oldIssueId}%";
        foreach (var (itemId, value) in QueryPairs(connection, tx, "SELECT item_id, blocked_by FROM tasks WHERE blocked_by LIKE $p;", pattern))
        {
            Execute(connection, tx, "UPDATE tasks SET blocked_by = $v WHERE item_id = $id;", ("$v", Replace(value)), ("$id", itemId));
        }

        foreach (var column in (string[])["old_value", "new_value"])
        {
            foreach (var (id, value) in QueryPairs(connection, tx,
                $"SELECT CAST(id AS TEXT), {column} FROM outbox WHERE field = {(int)TaskField.BlockedBy} AND {column} LIKE $p;", pattern))
            {
                Execute(connection, tx, $"UPDATE outbox SET {column} = $v WHERE id = $id;", ("$v", Replace(value)), ("$id", long.Parse(id, CultureInfo.InvariantCulture)));
            }
        }
    }

    private static List<(string Key, string? Value)> QueryPairs(SqliteConnection connection, SqliteTransaction tx, string sql, string pattern) =>
        Query(connection, tx, sql, r => (r.GetString(0), NullableString(r, 1)), ("$p", pattern));

    // ================================================================ Organization と担当者の候補

    public Task SaveOrganizationsAsync(IReadOnlyList<Organization> organizations, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = database.Open();
        using var tx = connection.BeginTransaction();
        Execute(connection, tx, "DELETE FROM organizations;");
        foreach (var o in organizations)
        {
            Execute(connection, tx, """
                INSERT INTO organizations (login, id, name, can_create_projects, can_create_repositories, problem, problem_detail)
                VALUES ($login, $id, $name, $projects, $repos, $problem, $detail);
                """,
                ("$login", o.Login), ("$id", o.Id), ("$name", o.Name), ("$projects", o.CanCreateProjects),
                ("$repos", o.CanCreateRepositories), ("$problem", (int)o.Problem), ("$detail", o.ProblemDetail));
        }

        tx.Commit();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Organization>> GetOrganizationsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = database.Open();
        return Task.FromResult<IReadOnlyList<Organization>>(Query(connection, null, """
            SELECT id, login, name, can_create_projects, can_create_repositories, problem, problem_detail
            FROM organizations ORDER BY login;
            """,
            r => new Organization(r.GetString(0), r.GetString(1), NullableString(r, 2), r.GetBoolean(3), r.GetBoolean(4),
                (OrganizationAccessProblem)r.GetInt32(5), NullableString(r, 6))));
    }

    public Task SaveAssignableUsersAsync(string repositoryNameWithOwner, IReadOnlyList<Assignee> users, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = database.Open();
        using var tx = connection.BeginTransaction();
        Execute(connection, tx, "DELETE FROM assignable_users WHERE repository_name = $r;", ("$r", repositoryNameWithOwner));
        foreach (var u in users)
        {
            Execute(connection, tx, "INSERT INTO assignable_users (repository_name, login, id, name) VALUES ($r, $login, $id, $name);",
                ("$r", repositoryNameWithOwner), ("$login", u.Login), ("$id", u.Id), ("$name", u.Name));
        }

        tx.Commit();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Assignee>> GetAssignableUsersAsync(string repositoryNameWithOwner, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = database.Open();
        return Task.FromResult<IReadOnlyList<Assignee>>(Query(connection, null,
            "SELECT id, login, name FROM assignable_users WHERE repository_name = $r ORDER BY login;",
            r => new Assignee(r.GetString(0), r.GetString(1), NullableString(r, 2)), ("$r", repositoryNameWithOwner)));
    }

    // ================================================================ 内部処理

    /// <summary>
    /// 未送信・送信失敗の変更を、キャッシュの内容に重ねて適用する。
    /// 移動のように複数の Project にまたがる変更があるため、キュー全体を順に適用する（適用は冪等）。
    /// </summary>
    private static void ReapplyPending(SqliteConnection connection, SqliteTransaction tx)
    {
        var entries = QueryOutbox(connection, tx, "ORDER BY id");
        foreach (var e in entries)
        {
            switch (e.Kind)
            {
                case OutboxKind.Create when ReadTask(connection, tx, e.ItemId) is null:
                    var payload = JsonSerializer.Deserialize(e.Payload ?? "{}", OutboxJsonContext.Default.NewTaskPayload);
                    if (payload is not null)
                    {
                        WriteTask(connection, tx, NewLocalTask(e.ItemId, e.ProjectId, payload, e.CreatedAt));
                    }

                    break;

                case OutboxKind.Field when e.Field is { } field && ReadTask(connection, tx, e.ItemId) is { } task:
                    WriteTask(connection, tx, Touched(TaskValues.Apply(task, new TaskChange(field, e.OldValue, e.NewValue),
                        QueryStatusOptions(connection, tx, task.ProjectId)), e.CreatedAt));
                    break;

                case OutboxKind.Delete:
                    Execute(connection, tx, "DELETE FROM tasks WHERE item_id = $id;", ("$id", e.ItemId));
                    break;

                case OutboxKind.Move when ReadTask(connection, tx, e.ItemId) is { } moved:
                    WriteTask(connection, tx, Touched(moved with { ProjectId = e.ProjectId }, e.CreatedAt));
                    break;
            }
        }
    }

    /// <summary>GitHub へ未作成のタスク。</summary>
    public static TaskItem NewLocalTask(string itemId, string projectId, NewTaskPayload payload, DateTimeOffset createdAt) => new()
    {
        ItemId = itemId,
        ProjectId = projectId,
        IssueId = itemId,
        RepositoryNameWithOwner = payload.RepositoryNameWithOwner,
        Number = 0,
        Title = payload.Title,
        Body = payload.Body,
        UpdatedAt = createdAt,
    };

    /// <summary>
    /// 変えた時刻を更新日時にする（一覧の「更新」と更新順の並び）。GitHub の更新日時は送り終えて取り込むまで変わらないため、
    /// 変えたときと、送信待ちの変更を取り込んだ内容に重ね直すときに進める。より新しい更新日時は戻さない。
    /// </summary>
    private static TaskItem Touched(TaskItem task, DateTimeOffset at) => at > task.UpdatedAt ? task with { UpdatedAt = at } : task;

    private static void Enqueue(SqliteConnection connection, SqliteTransaction tx, OutboxKind kind, string projectId, string itemId,
        string? issueId, TaskField? field, string? oldValue, string? newValue, string? payload)
    {
        Execute(connection, tx, """
            INSERT INTO outbox (kind, project_id, item_id, issue_id, field, old_value, new_value, payload, created_at)
            VALUES ($kind, $project, $item, $issue, $field, $old, $new, $payload, $at);
            """,
            ("$kind", (int)kind), ("$project", projectId), ("$item", itemId),
            ("$issue", issueId is not null && !issueId.StartsWith(TaskItem.LocalIdPrefix, StringComparison.Ordinal) ? issueId : null),
            ("$field", field is null ? null : (int)field), ("$old", oldValue), ("$new", newValue), ("$payload", payload),
            ("$at", FormatTimestamp(DateTimeOffset.UtcNow)));
    }

    private static List<OutboxEntry> QueryOutbox(SqliteConnection connection, SqliteTransaction? tx, string where, params (string Name, object? Value)[] parameters)
    {
        return Query(connection, tx, $"""
            SELECT id, kind, project_id, item_id, issue_id, field, old_value, new_value, payload, attempts, last_error, failed, created_at
            FROM outbox {where};
            """,
            r => new OutboxEntry(
                r.GetInt64(0), (OutboxKind)r.GetInt32(1), r.GetString(2), r.GetString(3), NullableString(r, 4),
                r.IsDBNull(5) ? null : (TaskField)r.GetInt32(5), NullableString(r, 6), NullableString(r, 7), NullableString(r, 8),
                r.GetInt32(9), NullableString(r, 10), r.GetBoolean(11), ParseTimestamp(r.GetString(12))),
            parameters);
    }

    private static List<Project> QueryProjects(SqliteConnection connection, SqliteTransaction? tx, string? projectId)
    {
        var options = Query(connection, tx, "SELECT project_id, id, name, color, category FROM status_options ORDER BY project_id, sort_order;",
                r => (Project: r.GetString(0), Option: new StatusOption(r.GetString(1), r.GetString(2), r.GetString(3), (StatusCategory)r.GetInt32(4))))
            .GroupBy(x => x.Project)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Option).ToList());

        var fields = Query(connection, tx, "SELECT project_id, kind, name, id FROM project_fields;",
                r => (Key: (r.GetString(0), r.GetInt32(1)), Name: r.GetString(2), Id: r.GetString(3)))
            .GroupBy(x => x.Key)
            .ToDictionary(g => g.Key, g => g.ToDictionary(x => x.Name, x => x.Id));

        var milestones = Query(connection, tx, "SELECT project_id, id, number, title, due, description, closed FROM milestones;",
                r => (Project: r.GetString(0), Milestone: new Milestone(r.GetString(1), r.GetInt32(2), r.GetString(3),
                    TaskValues.ParseDate(NullableString(r, 4)), NullableString(r, 5), r.GetBoolean(6))))
            .GroupBy(x => x.Project)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Milestone).ToList());

        return Query(connection, tx, $"""
            SELECT id, number, title, kind, owner_login, url, repository_id, repository_name, closed, updated_at, settings
            FROM projects {(projectId is null ? "" : "WHERE id = $id")} ORDER BY kind, title;
            """,
            r =>
            {
                var id = r.GetString(0);
                return new Project
                {
                    Id = id,
                    Number = r.GetInt32(1),
                    Title = r.GetString(2),
                    Kind = (ProjectKind)r.GetInt32(3),
                    OwnerLogin = r.GetString(4),
                    Url = NullableString(r, 5),
                    RepositoryId = NullableString(r, 6),
                    RepositoryNameWithOwner = NullableString(r, 7),
                    Closed = r.GetBoolean(8),
                    UpdatedAt = ParseTimestamp(r.GetString(9)),
                    Settings = ProjectSettings.Parse(NullableString(r, 10)),
                    StatusOptions = options.GetValueOrDefault(id) ?? [],
                    FieldIds = fields.GetValueOrDefault((id, 0)) ?? [],
                    KindOptionIds = fields.GetValueOrDefault((id, 1)) ?? [],
                    Milestones = milestones.GetValueOrDefault(id) ?? [],
                };
            },
            projectId is null ? [] : [("$id", projectId)]);
    }

    private static List<StatusOption> QueryStatusOptions(SqliteConnection connection, SqliteTransaction? tx, string projectId)
    {
        return Query(connection, tx, "SELECT id, name, color, category FROM status_options WHERE project_id = $p ORDER BY sort_order;",
            r => new StatusOption(r.GetString(0), r.GetString(1), r.GetString(2), (StatusCategory)r.GetInt32(3)), ("$p", projectId));
    }

    private static TaskItem? ReadTask(SqliteConnection connection, SqliteTransaction? tx, string itemId) =>
        QueryTasks(connection, tx, "WHERE item_id = $id", ("$id", itemId)).FirstOrDefault();

    private static IReadOnlyList<TaskItem> QueryTasks(SqliteConnection connection, SqliteTransaction? tx, string where, params (string Name, object? Value)[] parameters)
    {
        var assignees = Query(connection, tx, "SELECT item_id, login FROM task_assignees ORDER BY login;",
                r => (Item: r.GetString(0), Login: r.GetString(1)))
            .GroupBy(x => x.Item)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Login).ToList());

        return Query(connection, tx, $"SELECT {TaskColumns} FROM tasks {where};",
            r =>
            {
                var itemId = r.GetString(0);
                return new TaskItem
                {
                    ItemId = itemId,
                    ProjectId = r.GetString(1),
                    IssueId = r.GetString(2),
                    RepositoryNameWithOwner = r.GetString(3),
                    Number = r.GetInt32(4),
                    Title = r.GetString(5),
                    Url = NullableString(r, 6),
                    IsClosed = r.GetBoolean(7),
                    StatusOptionId = NullableString(r, 8),
                    StatusName = NullableString(r, 9),
                    Category = (StatusCategory)r.GetInt32(10),
                    Kind = (TaskKind)r.GetInt32(11),
                    Start = TaskValues.ParseDate(NullableString(r, 12)),
                    Target = TaskValues.ParseDate(NullableString(r, 13)),
                    ActualStart = TaskValues.ParseDate(NullableString(r, 14)),
                    ActualEnd = TaskValues.ParseDate(NullableString(r, 15)),
                    EstimateHours = r.IsDBNull(16) ? null : r.GetDouble(16),
                    ProgressPercent = r.GetDouble(17),
                    ParentIssueId = NullableString(r, 18),
                    UpdatedAt = ParseTimestamp(r.GetString(19)),
                    Body = NullableString(r, 20),
                    SortOrder = r.GetDouble(21),
                    BlockedBy = TaskValues.ParseIssueIds(NullableString(r, 22)),
                    NonBlocking = r.GetBoolean(23),
                    Repositories = TaskValues.ParseRepositories(NullableString(r, 24)),
                    Branches = TaskValues.ParseBranches(NullableString(r, 25)),
                    MilestoneId = NullableString(r, 26),
                    Assignees = assignees.GetValueOrDefault(itemId) ?? [],
                };
            },
            parameters);
    }

    private static void WriteTask(SqliteConnection connection, SqliteTransaction? tx, TaskItem t)
    {
        Execute(connection, tx, $"""
            INSERT OR REPLACE INTO tasks ({TaskColumns})
            VALUES ($item, $project, $issue, $repo, $number, $title, $url, $closed,
                $status, $statusName, $category, $kind, $start, $target, $actualStart, $actualEnd,
                $estimate, $progress, $parent, $updated, $body, $sortOrder, $blockedBy, $nonBlocking, $repositories, $branches, $milestone);
            """,
            ("$item", t.ItemId), ("$project", t.ProjectId), ("$issue", t.IssueId), ("$repo", t.RepositoryNameWithOwner),
            ("$number", t.Number), ("$title", t.Title), ("$url", t.Url), ("$closed", t.IsClosed),
            ("$status", t.StatusOptionId), ("$statusName", t.StatusName), ("$category", (int)t.Category), ("$kind", (int)t.Kind),
            ("$start", TaskValues.Date(t.Start)), ("$target", TaskValues.Date(t.Target)),
            ("$actualStart", TaskValues.Date(t.ActualStart)), ("$actualEnd", TaskValues.Date(t.ActualEnd)),
            ("$estimate", t.EstimateHours), ("$progress", t.ProgressPercent), ("$parent", t.ParentIssueId),
            ("$updated", FormatTimestamp(t.UpdatedAt)), ("$body", t.Body), ("$sortOrder", t.SortOrder),
            ("$blockedBy", TaskValues.IssueIds(t.BlockedBy)), ("$nonBlocking", t.NonBlocking),
            ("$repositories", TaskValues.Repositories(t.Repositories)), ("$branches", TaskValues.Branches(t.Branches)), ("$milestone", t.MilestoneId));

        Execute(connection, tx, "DELETE FROM task_assignees WHERE item_id = $item;", ("$item", t.ItemId));
        foreach (var login in t.Assignees.Distinct())
        {
            Execute(connection, tx, "INSERT INTO task_assignees (item_id, login) VALUES ($item, $login);", ("$item", t.ItemId), ("$login", login));
        }
    }

    private static List<string> QueryStrings(SqliteConnection connection, SqliteTransaction? tx, string sql) =>
        Query(connection, tx, sql, r => r.GetString(0));

    /// <summary>SQL を実行し、結果の行を 1 行ずつ <paramref name="read"/> で読んで返す。</summary>
    private static List<T> Query<T>(SqliteConnection connection, SqliteTransaction? tx, string sql, Func<SqliteDataReader, T> read,
        params (string Name, object? Value)[] parameters)
    {
        using var cmd = Command(connection, tx, sql, parameters);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read())
        {
            list.Add(read(r));
        }

        return list;
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = Command(connection, tx, sql, parameters);
        return cmd.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? tx, string sql, (string Name, object? Value)[] parameters)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return cmd;
    }

    private static string? NullableString(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static string FormatTimestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
}
