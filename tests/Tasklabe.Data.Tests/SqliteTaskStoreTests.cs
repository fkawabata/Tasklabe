using Microsoft.Data.Sqlite;
using Tasklabe.Core.Domain;
using Tasklabe.GitHub;

namespace Tasklabe.Data.Tests;

public sealed class SqliteTaskStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    // 共有インメモリ DB は接続が 1 つでも開いている間だけ存在する
    private readonly SqliteConnection _keepAlive;
    private readonly SqliteTaskStore _store;

    public SqliteTaskStoreTests()
    {
        var cs = $"Data Source=file:test-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(cs);
        _keepAlive.Open();
        var db = new TasklabeDatabase(cs);
        db.Migrate();
        _store = new SqliteTaskStore(db);
    }

    public void Dispose() => _keepAlive.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Project Project(string id, ProjectKind kind = ProjectKind.Personal) => new()
    {
        Id = id,
        Number = 1,
        Title = $"project {id}",
        Kind = kind,
        OwnerLogin = "me",
        RepositoryId = "R_1",
        RepositoryNameWithOwner = "me/tasklabe-personal",
        UpdatedAt = Now,
        StatusOptions = [new("o1", "Todo", "GRAY", StatusCategory.Todo), new("o2", "Done", "GREEN", StatusCategory.Done)],
    };

    private static TaskItem Task(string id, string projectId) => new()
    {
        ItemId = id,
        ProjectId = projectId,
        IssueId = $"ISSUE_{id}",
        RepositoryNameWithOwner = "me/tasklabe-personal",
        Number = 7,
        Title = $"task {id}",
        StatusOptionId = "o1",
        StatusName = "Todo",
        Start = new DateOnly(2026, 9, 14),
        Target = new DateOnly(2026, 9, 25),
        EstimateHours = 12.5,
        ProgressPercent = 40,
        Assignees = ["me", "you"],
        UpdatedAt = Now,
    };

    [Fact]
    public async Task Snapshot_round_trips()
    {
        await _store.ReplaceProjectAsync(new ProjectSnapshot(Project("P1"), [Task("I1", "P1")]), Now, Ct);

        var project = Assert.Single(await _store.GetProjectsAsync(Ct));
        Assert.Equal("project P1", project.Title);
        Assert.Equal(["Todo", "Done"], project.StatusOptions.Select(o => o.Name));

        var task = Assert.Single(await _store.GetTasksAsync("P1", Ct));
        Assert.Equal(Task("I1", "P1") with { Assignees = task.Assignees }, task);
        Assert.Equal(["me", "you"], task.Assignees);

        var state = (await _store.GetSyncStatesAsync(Ct))["P1"];
        Assert.Equal(Now, state.LastSyncedAt);
        Assert.Equal(Now, state.RemoteUpdatedAt);
    }

    [Fact]
    public async Task Replacing_a_project_removes_tasks_that_disappeared()
    {
        await _store.ReplaceProjectAsync(new ProjectSnapshot(Project("P1"), [Task("I1", "P1"), Task("I2", "P1")]), Now, Ct);
        await _store.ReplaceProjectAsync(new ProjectSnapshot(Project("P1"), [Task("I2", "P1")]), Now.AddMinutes(1), Ct);

        Assert.Equal(["I2"], (await _store.GetTasksAsync(Ct)).Select(t => t.ItemId));
    }

    [Fact]
    public async Task Projects_not_on_remote_are_removed_with_their_tasks()
    {
        await _store.ReplaceProjectAsync(new ProjectSnapshot(Project("P1"), [Task("I1", "P1")]), Now, Ct);
        await _store.ReplaceProjectAsync(new ProjectSnapshot(Project("P2"), [Task("I2", "P2")]), Now, Ct);

        var removed = await _store.RemoveProjectsExceptAsync(["P2"], Ct);

        Assert.Equal(1, removed);
        Assert.Equal(["P2"], (await _store.GetProjectsAsync(Ct)).Select(p => p.Id));
        Assert.Equal(["I2"], (await _store.GetTasksAsync(Ct)).Select(t => t.ItemId));
        Assert.False((await _store.GetSyncStatesAsync(Ct)).ContainsKey("P1"));
    }

    [Fact]
    public async Task Organizations_and_assignable_users_round_trip()
    {
        await _store.SaveOrganizationsAsync(
        [
            new Organization("O_1", "acme", "Acme", true, true),
            new Organization("O_2", "lab", null, false, false, OrganizationAccessProblem.OAuthAppNotApproved, "restricted"),
        ], Ct);
        await _store.SaveAssignableUsersAsync("acme/repo", [new Assignee("U_1", "me", "Me"), new Assignee("U_2", "you", null)], Ct);

        var orgs = await _store.GetOrganizationsAsync(Ct);
        Assert.Equal(["acme", "lab"], orgs.Select(o => o.Login));
        Assert.Equal(OrganizationAccessProblem.OAuthAppNotApproved, orgs[1].Problem);
        Assert.False(orgs[1].IsAccessible);

        Assert.Equal(["me", "you"], (await _store.GetAssignableUsersAsync("acme/repo", Ct)).Select(u => u.Login));
        Assert.Empty(await _store.GetAssignableUsersAsync("other/repo", Ct));
    }
}
