using Microsoft.Data.Sqlite;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Editing;
using Tasklabe.GitHub;

namespace Tasklabe.Data.Tests;

public sealed class OutboxTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _keepAlive;
    private readonly SqliteTaskStore _store;

    public OutboxTests()
    {
        var cs = $"Data Source=file:outbox-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(cs);
        _keepAlive.Open();
        var db = new TasklabeDatabase(cs);
        db.Migrate();
        _store = new SqliteTaskStore(db);
    }

    public void Dispose() => _keepAlive.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Project Project(string id) => new()
    {
        Id = id,
        Number = 1,
        Title = id,
        Kind = ProjectKind.Personal,
        OwnerLogin = "me",
        RepositoryId = "R_1",
        RepositoryNameWithOwner = "me/repo",
        UpdatedAt = Now,
        StatusOptions = [new($"{id}-todo", "Todo", "GRAY", StatusCategory.Todo), new($"{id}-done", "Done", "GREEN", StatusCategory.Done)],
        FieldIds = new Dictionary<string, string> { ["Progress"] = "F_progress" },
    };

    private static TaskItem Remote(string id, string projectId, double progress = 0) => new()
    {
        ItemId = id,
        ProjectId = projectId,
        IssueId = $"ISSUE_{id}",
        RepositoryNameWithOwner = "me/repo",
        Number = 1,
        Title = $"task {id}",
        StatusOptionId = $"{projectId}-todo",
        StatusName = "Todo",
        ProgressPercent = progress,
        UpdatedAt = Now,
    };

    [Fact]
    public async Task Pending_edit_survives_a_pull_of_older_remote_data()
    {
        await _store.ReplaceProjectAsync(new ProjectSnapshot(Project("P1"), [Remote("I1", "P1")]), Now, Ct);
        var task = (await _store.GetTaskAsync("I1", Ct))!;

        await _store.ApplyEditAsync("I1", TaskRules.Set(task, TaskField.Progress, "60"), Ct);
        await _store.ReplaceProjectAsync(new ProjectSnapshot(Project("P1"), [Remote("I1", "P1", progress: 10)]), Now, Ct);

        Assert.Equal(60, (await _store.GetTaskAsync("I1", Ct))!.ProgressPercent);
        Assert.Equal((1, 0), await _store.GetOutboxCountsAsync(Ct));
    }

    [Fact]
    public async Task Edit_moves_updated_at_and_a_pull_of_older_remote_data_keeps_it()
    {
        // 区分などを変えたら、取り込みを待たずに「更新」へ反映し、古い取り込みで戻さない
        await _store.ReplaceProjectAsync(new ProjectSnapshot(Project("P1"), [Remote("I1", "P1")]), Now, Ct);
        var task = (await _store.GetTaskAsync("I1", Ct))!;

        var before = DateTimeOffset.UtcNow;
        await _store.ApplyEditAsync("I1", TaskRules.Set(task, TaskField.Progress, "60"), Ct);
        var edited = (await _store.GetTaskAsync("I1", Ct))!.UpdatedAt;
        Assert.True(edited >= before.AddSeconds(-1));

        await _store.ReplaceProjectAsync(new ProjectSnapshot(Project("P1"), [Remote("I1", "P1", progress: 10)]), Now, Ct);
        Assert.True((await _store.GetTaskAsync("I1", Ct))!.UpdatedAt >= edited.AddSeconds(-1));
    }

    [Fact]
    public async Task Local_task_survives_a_pull_and_gets_real_ids_after_creation()
    {
        await _store.ReplaceProjectAsync(new ProjectSnapshot(Project("P1"), []), Now, Ct);
        var local = SqliteTaskStore.NewLocalTask("local:1", "P1", new NewTaskPayload("new task", null, "me/repo"), Now);
        await _store.AddLocalTaskAsync(local, [new TaskChange(TaskField.Status, null, "P1-todo")], Ct);

        await _store.ReplaceProjectAsync(new ProjectSnapshot(Project("P1"), []), Now, Ct);
        Assert.Equal("new task", (await _store.GetTaskAsync("local:1", Ct))!.Title);

        var create = (await _store.GetNextPendingAsync(Ct))!;
        Assert.Equal(OutboxKind.Create, create.Kind);
        await _store.ReplaceItemIdAsync("local:1", "I_real", new CreatedIssue("ISSUE_real", 42, "https://example/42"), Ct);
        await _store.CompleteOutboxAsync(create.Id, Ct);

        var created = (await _store.GetTaskAsync("I_real", Ct))!;
        Assert.Equal(42, created.Number);
        Assert.Equal("Todo", created.StatusName);

        var next = (await _store.GetNextPendingAsync(Ct))!;
        Assert.Equal(("I_real", "ISSUE_real", TaskField.Status), (next.ItemId, next.IssueId, next.Field!.Value));
    }

    [Fact]
    public async Task Deleting_an_unsent_task_removes_its_queue_entries()
    {
        await _store.ReplaceProjectAsync(new ProjectSnapshot(Project("P1"), []), Now, Ct);
        var local = SqliteTaskStore.NewLocalTask("local:1", "P1", new NewTaskPayload("new", null, "me/repo"), Now);
        var added = await _store.AddLocalTaskAsync(local, [new TaskChange(TaskField.Status, null, "P1-todo")], Ct);

        await _store.DeleteTaskAsync(added, Ct);

        Assert.Equal((0, 0), await _store.GetOutboxCountsAsync(Ct));
        Assert.Null(await _store.GetTaskAsync("local:1", Ct));
    }

    [Fact]
    public async Task Deleted_task_stays_deleted_after_pull()
    {
        await _store.ReplaceProjectAsync(new ProjectSnapshot(Project("P1"), [Remote("I1", "P1")]), Now, Ct);
        await _store.DeleteTaskAsync((await _store.GetTaskAsync("I1", Ct))!, Ct);

        await _store.ReplaceProjectAsync(new ProjectSnapshot(Project("P1"), [Remote("I1", "P1")]), Now, Ct);

        Assert.Null(await _store.GetTaskAsync("I1", Ct));
    }

    [Fact]
    public async Task Moved_task_stays_in_target_through_pulls_of_both_projects()
    {
        await _store.ReplaceProjectAsync(new ProjectSnapshot(Project("P1"), [Remote("I1", "P1")]), Now, Ct);
        await _store.ReplaceProjectAsync(new ProjectSnapshot(Project("P2"), []), Now, Ct);
        var task = (await _store.GetTaskAsync("I1", Ct))!;

        await _store.MoveTaskAsync(task, "P2", [new TaskChange(TaskField.Status, null, "P2-todo")], Ct);
        await _store.ReplaceProjectAsync(new ProjectSnapshot(Project("P2"), []), Now, Ct);
        await _store.ReplaceProjectAsync(new ProjectSnapshot(Project("P1"), [Remote("I1", "P1")]), Now, Ct);

        var moved = (await _store.GetTaskAsync("I1", Ct))!;
        Assert.Equal("P2", moved.ProjectId);
        Assert.Equal("P2-todo", moved.StatusOptionId);
    }

    [Fact]
    public async Task Failed_creation_fails_its_follow_up_changes()
    {
        await _store.ReplaceProjectAsync(new ProjectSnapshot(Project("P1"), []), Now, Ct);
        var local = SqliteTaskStore.NewLocalTask("local:1", "P1", new NewTaskPayload("new", null, "me/repo"), Now);
        await _store.AddLocalTaskAsync(local, [new TaskChange(TaskField.Status, null, "P1-todo")], Ct);

        await _store.FailOutboxAsync((await _store.GetNextPendingAsync(Ct))!, "boom", Ct);

        Assert.Equal((0, 2), await _store.GetOutboxCountsAsync(Ct));
        Assert.Null(await _store.GetNextPendingAsync(Ct));

        await _store.DiscardFailedAsync(Ct);
        Assert.Equal((0, 0), await _store.GetOutboxCountsAsync(Ct));
        Assert.Null(await _store.GetTaskAsync("local:1", Ct));
    }

    [Fact]
    public async Task Child_of_unsent_parent_points_to_real_issue_after_creation()
    {
        await _store.ReplaceProjectAsync(new ProjectSnapshot(Project("P1"), []), Now, Ct);
        var parent = SqliteTaskStore.NewLocalTask("local:p", "P1", new NewTaskPayload("parent", null, "me/repo"), Now);
        await _store.AddLocalTaskAsync(parent, [], Ct);
        var child = SqliteTaskStore.NewLocalTask("local:c", "P1", new NewTaskPayload("child", null, "me/repo"), Now);
        await _store.AddLocalTaskAsync(child, [new TaskChange(TaskField.Parent, null, "local:p")], Ct);
        Assert.Equal("local:p", (await _store.GetTaskAsync("local:c", Ct))!.ParentIssueId);

        await _store.ReplaceItemIdAsync("local:p", "I_parent", new CreatedIssue("ISSUE_parent", 1, null), Ct);

        Assert.Equal("ISSUE_parent", (await _store.GetTaskAsync("local:c", Ct))!.ParentIssueId);
        using var connection = new SqliteConnection(_keepAlive.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT new_value FROM outbox WHERE field = {(int)TaskField.Parent};";
        Assert.Equal("ISSUE_parent", cmd.ExecuteScalar());
    }

    [Fact]
    public async Task Dependency_on_unsent_task_points_to_real_issue_after_creation()
    {
        await _store.ReplaceProjectAsync(new ProjectSnapshot(Project("P1"), []), Now, Ct);
        var first = SqliteTaskStore.NewLocalTask("local:a", "P1", new NewTaskPayload("a", null, "me/repo"), Now);
        await _store.AddLocalTaskAsync(first, [], Ct);
        var next = SqliteTaskStore.NewLocalTask("local:b", "P1", new NewTaskPayload("b", null, "me/repo"), Now);
        await _store.AddLocalTaskAsync(next, [new TaskChange(TaskField.BlockedBy, null, "ISSUE_x,local:a")], Ct);

        await _store.ReplaceItemIdAsync("local:a", "I_a", new CreatedIssue("ISSUE_a", 1, null), Ct);

        Assert.Equal(["ISSUE_a", "ISSUE_x"], (await _store.GetTaskAsync("local:b", Ct))!.BlockedBy);
        using var connection = new SqliteConnection(_keepAlive.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT new_value FROM outbox WHERE field = {(int)TaskField.BlockedBy};";
        Assert.Equal("ISSUE_a,ISSUE_x", cmd.ExecuteScalar());
    }
}
