using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Tasklabe.GitHub.Tests;

public class ProjectFetchTests
{
    private const string ProjectResponse = """
        {
          "data": {
            "node": {
              "id": "PVT_1", "number": 3, "title": "Web", "shortDescription": "[tasklabe]",
              "url": "https://github.com/users/me/projects/3", "closed": false, "updatedAt": "2026-09-18T10:00:00Z",
              "owner": { "__typename": "User", "id": "U_1", "login": "me" },
              "repositories": { "nodes": [ { "id": "R_1", "nameWithOwner": "me/repo" } ] },
              "fields": { "nodes": [] },
              "items": {
                "pageInfo": { "hasNextPage": false, "endCursor": null },
                "nodes": [ ITEM_1 ]
              }
            }
          }
        }
        """;

    private static string Item(string id, int number, string? projectId = null) => $$"""
        { "id": "{{id}}", "isArchived": false, "updatedAt": "2026-09-18T09:00:00Z",
          {{(projectId is null ? "" : $"\"project\": {{ \"id\": \"{projectId}\" }},")}}
          "fieldValues": { "nodes": [] },
          "content": { "__typename": "Issue", "id": "ISSUE_{{number}}", "number": {{number}}, "title": "task {{number}}", "state": "OPEN",
            "updatedAt": "2026-09-18T09:00:00Z", "repository": { "id": "R_1", "nameWithOwner": "me/repo" },
            "assignees": { "nodes": [] }, "parent": null } }
        """;

    /// <summary>一覧には I_1 だけを返し、I_2（反映待ち）、I_3（削除済み）、I_4（別の Project）は ID で答える GitHub。</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        public List<string> LookedUp { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            var query = (string)body["query"]!;
            string json;
            if (query.Contains("items(first", StringComparison.Ordinal))
            {
                json = ProjectResponse.Replace("ITEM_1", Item("I_1", 1), StringComparison.Ordinal);
            }
            else if (query.Contains("milestones(", StringComparison.Ordinal))
            {
                // リポジトリのマイルストーン（この試験では持たない）
                json = """{ "data": { "repository": { "milestones": { "pageInfo": { "hasNextPage": false }, "nodes": [] } } } }""";
            }
            else
            {
                var id = (string)body["variables"]!["id"]!;
                LookedUp.Add(id);
                json = id switch
                {
                    "I_2" => $$"""{ "data": { "node": {{Item("I_2", 2, "PVT_1")}} } }""",
                    "I_4" => $$"""{ "data": { "node": {{Item("I_4", 4, "PVT_OTHER")}} } }""",
                    _ => """{ "data": { "node": null }, "errors": [ { "type": "NOT_FOUND", "message": "Could not resolve to a node" } ] }""",
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Known_items_missing_from_the_listing_are_kept_if_they_still_exist()
    {
        var github = new FakeGitHub();
        var api = new GitHubApi(new HttpClient(github), GitHubHost.Default, () => "token");

        var snapshot = await api.GetProjectAsync("PVT_1", TestContext.Current.CancellationToken, ["I_1", "I_2", "I_3", "I_4"]);

        Assert.Equal(["I_1", "I_2"], snapshot.Tasks.Select(t => t.ItemId));
        // 一覧に載っていたものは、改めて問い合わせない
        Assert.Equal(["I_2", "I_3", "I_4"], github.LookedUp);
    }
}

/// <summary>ほかの端末やアプリの外で作られたタスクを、Issue の側から拾う（一覧への反映の遅れで取り込みから漏れないようにする）。</summary>
public class IssueItemLookupTests
{
    /// <summary>2 ページに分けて、更新された Issue と、それが入っている Project のアイテムを返す GitHub。</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        public List<JsonNode> Variables { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var variables = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!["variables"]!;
            Variables.Add(variables.DeepClone());
            string json = variables["cursor"] is null
                ? """
                  { "data": { "repository": { "id": "R_1", "nameWithOwner": "me/repo", "issueItems": {
                    "pageInfo": { "hasNextPage": true, "endCursor": "C1" },
                    "nodes": [
                      { "projectItems": { "nodes": [ { "id": "I_5", "project": { "id": "PVT_1" } }, { "id": "I_6", "project": { "id": "PVT_OTHER" } } ] } },
                      { "projectItems": { "nodes": [] } } ] } } } }
                  """
                : """
                  { "data": { "repository": { "id": "R_1", "nameWithOwner": "me/repo", "issueItems": {
                    "pageInfo": { "hasNextPage": false, "endCursor": null },
                    "nodes": [ { "projectItems": { "nodes": [ { "id": "I_7", "project": { "id": "PVT_1" } } ] } } ] } } } }
                  """;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Items_of_recently_updated_issues_in_the_project_are_collected_across_pages()
    {
        var github = new FakeGitHub();
        var api = new GitHubApi(new HttpClient(github), GitHubHost.Default, () => "token");
        var since = new DateTimeOffset(2026, 10, 6, 15, 10, 0, TimeSpan.Zero);

        var ids = await api.GetProjectItemIdsOfIssuesSinceAsync("me/repo", "PVT_1", since, TestContext.Current.CancellationToken);

        // ほかの Project のアイテムは含めない
        Assert.Equal(["I_5", "I_7"], ids);
        Assert.Equal("2026-10-06T15:10:00.0000000Z", (string)github.Variables[0]["since"]!);
        Assert.Equal("C1", (string)github.Variables[1]["cursor"]!);
    }
}

/// <summary>リポジトリのマイルストーンの読み取り（要件 F-MS-01）。</summary>
public class MilestoneFetchTests
{
    private sealed class FakeGitHub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // GitHub は期日を時刻付き（太平洋時間の 0 時 = UTC の 7 時など）で返す
            const string json = """
                { "data": { "repository": { "milestones": { "pageInfo": { "hasNextPage": false }, "nodes": [
                  { "id": "MI_1", "number": 3, "title": "要件確定", "description": null, "dueOn": "2026-10-16T07:00:00Z", "state": "OPEN" },
                  { "id": "MI_2", "number": 4, "title": "未定", "description": "説明", "dueOn": null, "state": "CLOSED" } ] } } } }
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task Milestones_are_read_with_the_utc_date_as_the_due_date()
    {
        var api = new GitHubApi(new HttpClient(new FakeGitHub()), GitHubHost.Default, () => "token");

        var milestones = await api.GetMilestonesAsync("me/repo", TestContext.Current.CancellationToken);

        Assert.Equal(2, milestones.Count);
        Assert.Equal(new DateOnly(2026, 10, 16), milestones[0].Due);
        Assert.Equal(3, milestones[0].Number);
        Assert.Null(milestones[1].Due);
        Assert.True(milestones[1].IsClosed);
    }
}

/// <summary>マイルストーンを作る REST API の呼び出し（要件 F-MS-01）。</summary>
public class MilestoneCreateTests
{
    private sealed class FakeGitHub : HttpMessageHandler
    {
        public JsonNode? Body { get; private set; }

        public string? Path { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.AbsolutePath;
            Body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            const string json = """{ "node_id": "MI_9", "number": 9, "title": "リリース", "due_on": "2026-11-13T07:00:00Z", "state": "open" }""";
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Milestone_is_created_with_the_due_date_at_utc_noon_so_github_keeps_the_same_date()
    {
        var github = new FakeGitHub();
        var api = new GitHubApi(new HttpClient(github), GitHubHost.Default, () => "token");

        var created = await api.CreateMilestoneAsync("me/repo", "リリース", new DateOnly(2026, 11, 13), ct: TestContext.Current.CancellationToken);

        Assert.Equal("/repos/me/repo/milestones", github.Path);
        Assert.Equal("2026-11-13T12:00:00Z", (string?)github.Body!["due_on"]);
        Assert.Equal("MI_9", created.Id);
        Assert.Equal(new DateOnly(2026, 11, 13), created.Due);
    }
}
