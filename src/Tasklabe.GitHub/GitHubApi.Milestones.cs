using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Tasklabe.Core.Domain;

namespace Tasklabe.GitHub;

/// <summary>
/// マイルストーン（要件 F-MS-01〜03、技術設計書 3.2.4 節）。プロジェクトにつないだリポジトリの Milestone を使う。
/// GraphQL には Milestone を作る・変える・消す操作がないため、それらは REST API で行う。読むことと、Issue の Milestone の付け替えは GraphQL で行う。
/// </summary>
public sealed partial class GitHubApi
{
    private const string MilestonesQuery = """
        query($owner: String!, $name: String!, $cursor: String) {
          repository(owner: $owner, name: $name) {
            milestones(first: 100, after: $cursor, orderBy: { field: DUE_DATE, direction: ASC }) {
              pageInfo { hasNextPage endCursor }
              nodes { id number title description dueOn state }
            }
          }
        }
        """;

    private const string SetIssueMilestoneMutation = """
        mutation($id: ID!, $milestoneId: ID) { updateIssue(input: { id: $id, milestoneId: $milestoneId }) { issue { id } } }
        """;

    /// <summary>リポジトリの Milestone（開いているものと閉じたもの）。</summary>
    public async Task<IReadOnlyList<Milestone>> GetMilestonesAsync(string repositoryNameWithOwner, CancellationToken ct = default)
    {
        var (owner, name) = Split(repositoryNameWithOwner);
        var result = new List<Milestone>();
        string? cursor = null;
        do
        {
            var data = await QueryAsync(MilestonesQuery, new JsonObject { ["owner"] = owner, ["name"] = name, ["cursor"] = cursor }, ct).ConfigureAwait(false);
            var connection = data["repository"]?["milestones"];
            foreach (var node in connection?["nodes"]?.AsArray() ?? [])
            {
                if (node?["id"]?.GetValue<string>() is { } id && node["title"]?.GetValue<string>() is { } title)
                {
                    result.Add(new Milestone(id, node["number"]?.GetValue<int>() ?? 0, title,
                        ParseDue(node["dueOn"]?.GetValue<string>()), node["description"]?.GetValue<string>(),
                        node["state"]?.GetValue<string>() == "CLOSED"));
                }
            }

            cursor = connection?["pageInfo"]?["hasNextPage"]?.GetValue<bool>() == true ? connection["pageInfo"]?["endCursor"]?.GetValue<string>() : null;
        }
        while (cursor is not null);

        return result;
    }

    /// <summary>Issue の Milestone を付け替える。null なら外す。</summary>
    public Task SetIssueMilestoneAsync(string issueId, string? milestoneId, CancellationToken ct = default) =>
        MutateAsync(SetIssueMilestoneMutation, new JsonObject { ["id"] = issueId, ["milestoneId"] = milestoneId }, ct);

    /// <summary>Milestone を作る。</summary>
    public async Task<Milestone> CreateMilestoneAsync(string repositoryNameWithOwner, string title, DateOnly? due, string? description = null, CancellationToken ct = default)
    {
        var (owner, name) = Split(repositoryNameWithOwner);
        var body = new JsonObject { ["title"] = title, ["due_on"] = FormatDue(due) };
        if (description is { Length: > 0 })
        {
            body["description"] = description;
        }

        var created = await SendMilestoneAsync(HttpMethod.Post, $"repos/{owner}/{name}/milestones", body, ct).ConfigureAwait(false)
            ?? throw new GitHubException("マイルストーンを作れませんでした。");
        return ToMilestone(created);
    }

    /// <summary>Milestone の名前と期日を変える。説明欄は、指定したときだけ変える。</summary>
    public async Task<Milestone> UpdateMilestoneAsync(string repositoryNameWithOwner, int number, string title, DateOnly? due, string? description = null, CancellationToken ct = default)
    {
        var (owner, name) = Split(repositoryNameWithOwner);
        var body = new JsonObject { ["title"] = title, ["due_on"] = FormatDue(due) };
        if (description is not null)
        {
            body["description"] = description;
        }

        var updated = await SendMilestoneAsync(HttpMethod.Patch, $"repos/{owner}/{name}/milestones/{number}", body, ct).ConfigureAwait(false)
            ?? throw new GitHubException("マイルストーンを変えられませんでした。");
        return ToMilestone(updated);
    }

    /// <summary>Milestone を消す。Issue からは外れるが、Issue は残る。</summary>
    public async Task DeleteMilestoneAsync(string repositoryNameWithOwner, int number, CancellationToken ct = default)
    {
        var (owner, name) = Split(repositoryNameWithOwner);
        await SendMilestoneAsync(HttpMethod.Delete, $"repos/{owner}/{name}/milestones/{number}", null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 期日は日付だけを扱う。GitHub は受け取った時刻を太平洋時間などの UTC より西の時刻帯で日付にしてから、その日の UTC 0 時として保存する。
    /// UTC の 0 時で送ると前日になるため（2026-10-13T00:00:00Z が 2026-10-12 になった）、どの時刻帯でも同じ日付になる UTC の 12 時で送る。
    /// 読むときは、返ってきた値の UTC の日付を期日とする。
    /// </summary>
    private static DateOnly? ParseDue(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? DateOnly.FromDateTime(at.UtcDateTime) : null;

    private static string? FormatDue(DateOnly? due) => due is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T12:00:00Z" : null;

    private static Milestone ToMilestone(JsonObject node) => new(
        node["node_id"]?.GetValue<string>() ?? throw new GitHubException("マイルストーンの ID を読み取れません。"),
        node["number"]?.GetValue<int>() ?? 0,
        node["title"]?.GetValue<string>() ?? "",
        ParseDue(node["due_on"]?.GetValue<string>()),
        node["description"]?.GetValue<string>(),
        node["state"]?.GetValue<string>() == "closed");

    /// <summary>マイルストーンの REST API を呼ぶ。応答の本文があれば返す。</summary>
    private async Task<JsonObject?> SendMilestoneAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        using var response = await SendRestAsync(method, path, body, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new GitHubAuthenticationException("GitHub の認証が無効になりました（401）。");
        }

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            throw new GitHubHttpException(response.StatusCode, "リポジトリのマイルストーンを変える権限がありません（Write 以上が必要です）。");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new GitHubHttpException(response.StatusCode, $"GitHub がエラーを返しました（{(int)response.StatusCode}）。");
        }

        return response.StatusCode == HttpStatusCode.NoContent
            ? null
            : await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.JsonObject, ct).ConfigureAwait(false);
    }
}
