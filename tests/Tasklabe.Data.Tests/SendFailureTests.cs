using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Tasklabe.Core.Abstractions;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Editing;
using Tasklabe.GitHub;

namespace Tasklabe.Data.Tests;

/// <summary>
/// 送信に失敗した変更が、いつまでも残ったり後ろの変更を止めたりしないこと（要件 F-SYNC-06）。
/// GitHub の代わりに、問い合わせの種類ごとに決めた応答を返す偽のサーバーを使う。
/// </summary>
public sealed class SendFailureTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _keepAlive;
    private readonly SqliteTaskStore _store;
    private readonly FakeGitHub _github = new();
    private readonly SyncEngine _sync;

    public SendFailureTests()
    {
        var cs = $"Data Source=file:send-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(cs);
        _keepAlive.Open();
        var db = new TasklabeDatabase(cs);
        db.Migrate();
        _store = new SqliteTaskStore(db);
        _sync = new SyncEngine(new GitHubApi(new HttpClient(_github), GitHubHost.Default, () => "token"), _store, TimeProvider.System);
    }

    public void Dispose() => _keepAlive.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>問い合わせの本文に含まれる語で応答を選ぶ偽の GitHub。</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        public List<(string Match, Func<HttpResponseMessage> Reply)> Rules { get; } = [];

        public List<string> Calls { get; } = [];

        /// <summary>つながらない（ネットワークがない）。</summary>
        public bool Offline { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Offline)
            {
                throw new HttpRequestException("ネットワークにつながっていません");
            }

            var query = (string)JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!["query"]!;
            foreach (var (match, reply) in Rules)
            {
                if (query.Contains(match, StringComparison.Ordinal))
                {
                    Calls.Add(match);
                    return reply();
                }
            }

            Calls.Add("?");
            return Json("""{ "data": {} }""");
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage NotFound() =>
        Json("""{ "data": null, "errors": [ { "type": "NOT_FOUND", "message": "Could not resolve to a node with the global id" } ] }""");

    private static Project Project() => new()
    {
        Id = "P1",
        Number = 1,
        Title = "P1",
        Kind = ProjectKind.Personal,
        OwnerLogin = "me",
        RepositoryId = "R_1",
        RepositoryNameWithOwner = "me/repo",
        UpdatedAt = Now,
        StatusOptions = [new("todo", "Todo", "GRAY", StatusCategory.Todo)],
        FieldIds = new Dictionary<string, string> { ["Progress"] = "F_progress", ["Estimate"] = "F_estimate" },
    };

    private static TaskItem Remote(string id) => new()
    {
        ItemId = id,
        ProjectId = "P1",
        IssueId = $"ISSUE_{id}",
        RepositoryNameWithOwner = "me/repo",
        Number = 1,
        Title = $"task {id}",
        UpdatedAt = Now,
    };

    private async Task EditProgressAsync(string itemId, double value)
    {
        var task = (await _store.GetTaskAsync(itemId, Ct))!;
        await _store.ApplyEditAsync(itemId, TaskRules.Set(task, TaskField.Progress, TaskValues.Number(value)), Ct);
    }

    [Fact]
    public async Task Edit_of_a_task_deleted_on_GitHub_is_dropped_instead_of_failing_forever()
    {
        await _store.ReplaceProjectAsync(new ProjectSnapshot(Project(), [Remote("I1")]), Now, Ct);
        await EditProgressAsync("I1", 50);
        _github.Rules.Add(("fieldValueByName", NotFound));
        _github.Rules.Add(("updateProjectV2ItemFieldValue", NotFound));

        await _sync.PushAsync(Ct);

        Assert.Equal((0, 0), await _store.GetOutboxCountsAsync(Ct));
    }

    [Fact]
    public async Task Unexpected_response_fails_the_entry_and_does_not_block_later_changes()
    {
        await _store.ReplaceProjectAsync(new ProjectSnapshot(Project(), [Remote("I1"), Remote("I2")]), Now, Ct);
        await EditProgressAsync("I1", 50);
        await EditProgressAsync("I2", 30);

        // 1 件目への応答は JSON ではない（プロキシのエラーページなど）。2 件目は正常に通る
        int calls = 0;
        _github.Rules.Add(("fieldValueByName", () => Json("""{ "data": { "node": { "fieldValueByName": { "number": 0 } } } }""")));
        _github.Rules.Add(("updateProjectV2ItemFieldValue", () => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>Bad gateway</html>", Encoding.UTF8, "application/json") }
            : Json("""{ "data": { "updateProjectV2ItemFieldValue": { "projectV2Item": { "id": "x" } } } }""")));

        await _sync.PushAsync(Ct);

        Assert.Equal((0, 1), await _store.GetOutboxCountsAsync(Ct));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Retrying_a_create_does_not_make_a_second_issue()
    {
        await _store.ReplaceProjectAsync(new ProjectSnapshot(Project(), []), Now, Ct);
        var local = SqliteTaskStore.NewLocalTask($"{TaskItem.LocalIdPrefix}1", "P1", new NewTaskPayload("new", null, "me/repo"), Now);
        await _store.AddLocalTaskAsync(local, [], Ct);

        // Issue は作れたが、Project への追加で一時的な障害が起きる
        int adds = 0;
        _github.Rules.Add(("createIssue", () => Json("""{ "data": { "createIssue": { "issue": { "id": "ISSUE_NEW", "number": 7, "url": "u" } } } }""")));
        _github.Rules.Add(("addProjectV2ItemById", () => ++adds == 1
            ? Json("{}", HttpStatusCode.BadGateway)
            : Json("""{ "data": { "addProjectV2ItemById": { "item": { "id": "ITEM_NEW" } } } }""")));

        await _sync.PushAsync(Ct);
        await _sync.PushAsync(Ct);

        Assert.Equal(1, _github.Calls.Count(c => c == "createIssue"));
        Assert.Equal((0, 0), await _store.GetOutboxCountsAsync(Ct));
        Assert.NotNull(await _store.GetTaskAsync("ITEM_NEW", Ct));
    }

    [Fact]
    public async Task Initial_values_of_a_new_task_are_not_conflicts_even_if_GitHub_filled_a_status()
    {
        var project = Project() with
        {
            StatusOptions = [new("todo", "Todo", "GRAY", StatusCategory.Todo), new("doing", "In Progress", "BLUE", StatusCategory.InProgress)],
            FieldIds = new Dictionary<string, string> { ["Status"] = "F_status" },
        };
        await _store.ReplaceProjectAsync(new ProjectSnapshot(project, []), Now, Ct);
        var local = SqliteTaskStore.NewLocalTask($"{TaskItem.LocalIdPrefix}1", "P1", new NewTaskPayload("new", null, "me/repo"), Now);
        await _store.AddLocalTaskAsync(local, [new TaskChange(TaskField.Status, null, "doing")], Ct);

        // 追加の直後に、GitHub のワークフローが Status を Todo にしている
        _github.Rules.Add(("createIssue", () => Json("""{ "data": { "createIssue": { "issue": { "id": "ISSUE_NEW", "number": 7, "url": "u" } } } }""")));
        _github.Rules.Add(("addProjectV2ItemById", () => Json("""{ "data": { "addProjectV2ItemById": { "item": { "id": "ITEM_NEW" } } } }""")));
        _github.Rules.Add(("fieldValueByName", () => Json("""{ "data": { "node": { "fieldValueByName": { "optionId": "todo", "name": "Todo" } } } }""")));
        _github.Rules.Add(("updateProjectV2ItemFieldValue", () => Json("""{ "data": { "updateProjectV2ItemFieldValue": { "projectV2Item": { "id": "ITEM_NEW" } } } }""")));
        var conflicts = new List<TaskConflict>();
        _sync.ConflictDetected += (_, c) => conflicts.Add(c);

        await _sync.PushAsync(Ct);

        Assert.Empty(conflicts);
        Assert.Equal((0, 0), await _store.GetOutboxCountsAsync(Ct));
    }

    [Fact]
    public async Task Changes_that_keep_failing_temporarily_become_visible_failures()
    {
        await _store.ReplaceProjectAsync(new ProjectSnapshot(Project(), [Remote("I1")]), Now, Ct);
        await EditProgressAsync("I1", 50);
        _github.Rules.Add(("fieldValueByName", () => Json("{}", HttpStatusCode.BadGateway)));

        for (int i = 0; i < SyncEngine.MaxTransientAttempts; i++)
        {
            await _sync.PushAsync(Ct);
        }

        Assert.Equal((0, 1), await _store.GetOutboxCountsAsync(Ct));
    }

    [Fact]
    public async Task Deleting_an_issue_that_is_already_gone_succeeds()
    {
        await _store.ReplaceProjectAsync(new ProjectSnapshot(Project(), [Remote("I1")]), Now, Ct);
        await _store.DeleteTaskAsync((await _store.GetTaskAsync("I1", Ct))!, Ct);
        _github.Rules.Add(("deleteIssue", NotFound));

        await _sync.PushAsync(Ct);

        Assert.Equal((0, 0), await _store.GetOutboxCountsAsync(Ct));
    }

    [Fact]
    public async Task Deleting_without_permission_removes_the_item_and_closes_the_issue_as_not_planned()
    {
        await _store.ReplaceProjectAsync(new ProjectSnapshot(Project(), [Remote("I1")]), Now, Ct);
        await _store.DeleteTaskAsync((await _store.GetTaskAsync("I1", Ct))!, Ct);
        _github.Rules.Add(("deleteIssue", () => Json("""{ "data": null, "errors": [ { "type": "FORBIDDEN", "message": "no permission" } ] }""")));
        _github.Rules.Add(("deleteProjectV2Item", () => Json("""{ "data": {} }""")));
        _github.Rules.Add(("closeIssue", () => Json("""{ "data": {} }""")));

        await _sync.PushAsync(Ct);

        Assert.Equal(["deleteIssue", "deleteProjectV2Item", "closeIssue"], _github.Calls);
        Assert.Equal((0, 0), await _store.GetOutboxCountsAsync(Ct));
    }

    [Fact]
    public async Task Reconnecting_sends_the_changes_made_while_offline_without_waiting_for_the_next_sync()
    {
        await _store.ReplaceProjectAsync(new ProjectSnapshot(Project(), [Remote("I1")]), Now, Ct);
        await EditProgressAsync("I1", 50);
        _github.Offline = true;
        await _sync.PushAsync(Ct);
        Assert.Equal(SyncState.Offline, _sync.Status.State);

        _github.Offline = false;
        _github.Rules.Add(("fieldValueByName", () => Json("""{ "data": { "node": { "fieldValueByName": { "number": 0 } } } }""")));
        _github.Rules.Add(("updateProjectV2ItemFieldValue", () => Json("""{ "data": { "updateProjectV2ItemFieldValue": { "projectV2Item": { "id": "I1" } } } }""")));
        await _sync.ReconnectedAsync(Ct);

        Assert.Equal((0, 0), await _store.GetOutboxCountsAsync(Ct));
        Assert.Equal(SyncState.Idle, _sync.Status.State);
    }

    [Fact]
    public async Task Reconnecting_with_nothing_to_send_does_not_call_GitHub()
    {
        await _store.ReplaceProjectAsync(new ProjectSnapshot(Project(), [Remote("I1")]), Now, Ct);

        await _sync.ReconnectedAsync(Ct);

        Assert.Empty(_github.Calls);
    }
}
