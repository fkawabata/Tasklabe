using System.Text;
using System.Text.Json.Nodes;
using Tasklabe.Core.Domain;

namespace Tasklabe.GitHub;

/// <summary>Issue の Development に紐づくブランチと PR。</summary>
public sealed record IssueDevelopment(IReadOnlyList<LinkedBranch> Branches, IReadOnlyList<PullRequestInfo> PullRequests);

public sealed partial class GitHubApi
{
    private const string PullRequestFields = """
        id number title url state isDraft reviewDecision
        repository { nameWithOwner }
        reviewThreads(first: 100) { nodes { isResolved } }
        """;

    private const string IssueDevelopmentQuery = $$"""
        query($ids: [ID!]!) {
          nodes(ids: $ids) {
            ... on Issue {
              id
              linkedBranches(first: 20) { nodes { ref { name repository { nameWithOwner url } } } }
              closedByPullRequestsReferences(first: 10, includeClosedPrs: true) { nodes { {{PullRequestFields}} } }
            }
          }
        }
        """;

    /// <summary>1 回の問い合わせでまとめて取る Issue の数（`nodes` の上限は 100。PR とレビューの数を掛けた量を抑える）。</summary>
    private const int DevelopmentBatchSize = 50;

    /// <summary>
    /// Issue の Development に紐づくブランチと PR（要件 F-UI-GT-12、技術設計書 3.2.3.2 節）。50 件ずつまとめて問い合わせる。
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IssueDevelopment>> GetIssueDevelopmentAsync(IReadOnlyList<string> issueIds, CancellationToken ct = default)
    {
        var result = new Dictionary<string, IssueDevelopment>(StringComparer.Ordinal);
        foreach (var chunk in issueIds.Distinct(StringComparer.Ordinal).Chunk(DevelopmentBatchSize))
        {
            var ids = new JsonArray([.. chunk.Select(id => (JsonNode?)JsonValue.Create(id))]);
            var data = await QueryAsync(IssueDevelopmentQuery, new JsonObject { ["ids"] = ids }, ct).ConfigureAwait(false);
            foreach (var node in data["nodes"]?.AsArray() ?? [])
            {
                if (node?["id"]?.GetValue<string>() is not { } id)
                {
                    continue;
                }

                var branches = new List<LinkedBranch>();
                foreach (var b in node["linkedBranches"]?["nodes"]?.AsArray() ?? [])
                {
                    // 消したブランチは ref が null になる
                    if (b?["ref"] is { } r && r["name"]?.GetValue<string>() is { } name && r["repository"]?["nameWithOwner"]?.GetValue<string>() is { } repository)
                    {
                        branches.Add(new LinkedBranch(repository, name, BranchUrl(repository, name)));
                    }
                }

                result[id] = new IssueDevelopment(branches, ReadPullRequests(node["closedByPullRequestsReferences"]));
            }
        }

        return result;
    }

    /// <summary>
    /// 選んだブランチから出した PR（要件 F-UI-GT-12）。ブランチを消した後もマージ済みを取れるよう、ブランチの参照ではなく
    /// `pullRequests(headRefName:)` で引く。リポジトリごとに 1 回問い合わせ、見られないリポジトリは結果に含めない。
    /// </summary>
    /// <returns>(owner/name, ブランチ名) ごとの PR。</returns>
    public async Task<IReadOnlyDictionary<(string Repository, string Branch), IReadOnlyList<PullRequestInfo>>> GetBranchPullRequestsAsync(
        IEnumerable<(string Repository, string Branch)> branches, CancellationToken ct = default)
    {
        var result = new Dictionary<(string, string), IReadOnlyList<PullRequestInfo>>();
        foreach (var group in branches.Distinct().GroupBy(b => b.Repository, StringComparer.OrdinalIgnoreCase))
        {
            var names = group.Select(b => b.Branch).Distinct(StringComparer.Ordinal).ToList();
            var (owner, name) = Split(group.Key);
            var query = new StringBuilder("query($owner: String!, $name: String!");
            var variables = new JsonObject { ["owner"] = owner, ["name"] = name };
            for (int i = 0; i < names.Count; i++)
            {
                query.Append($", $h{i}: String!");
                variables[$"h{i}"] = names[i];
            }

            query.Append(") { repository(owner: $owner, name: $name) {");
            for (int i = 0; i < names.Count; i++)
            {
                query.Append($" b{i}: pullRequests(headRefName: $h{i}, first: 10, orderBy: {{ field: CREATED_AT, direction: DESC }}) {{ nodes {{ {PullRequestFields} }} }}");
            }

            query.Append(" } }");

            JsonObject data;
            try
            {
                data = await QueryAsync(query.ToString(), variables, ct).ConfigureAwait(false);
            }
            catch (GitHubGraphQLException)
            {
                // 消えた・見られないリポジトリは、そのブランチの PR を示さないだけにする
                continue;
            }

            for (int i = 0; i < names.Count; i++)
            {
                result[(group.Key, names[i])] = ReadPullRequests(data["repository"]?[$"b{i}"]);
            }
        }

        return result;
    }

    private static List<PullRequestInfo> ReadPullRequests(JsonNode? connection)
    {
        var list = new List<PullRequestInfo>();
        foreach (var p in connection?["nodes"]?.AsArray() ?? [])
        {
            if (p?["id"]?.GetValue<string>() is not { } id || p["url"]?.GetValue<string>() is not { } url)
            {
                continue;
            }

            var state = p["state"]?.GetValue<string>() switch
            {
                "MERGED" => PullRequestState.Merged,
                "CLOSED" => PullRequestState.Closed,
                _ => PullRequestState.Open,
            };
            int unresolved = (p["reviewThreads"]?["nodes"]?.AsArray() ?? []).Count(t => t?["isResolved"]?.GetValue<bool>() == false);
            list.Add(new PullRequestInfo(id, p["repository"]?["nameWithOwner"]?.GetValue<string>() ?? "", p["number"]?.GetValue<int>() ?? 0,
                p["title"]?.GetValue<string>() ?? "", url, state, p["isDraft"]?.GetValue<bool>() == true,
                p["reviewDecision"]?.GetValue<string>() == "APPROVED", unresolved));
        }

        return list;
    }

    /// <summary>ブランチを GitHub で開く URL。ブランチ名の `/` は区切りのまま残す。</summary>
    public string BranchUrl(string repositoryNameWithOwner, string branch) =>
        $"https://{_host.Name}/{repositoryNameWithOwner}/tree/{Uri.EscapeDataString(branch).Replace("%2F", "/", StringComparison.Ordinal)}";
}
