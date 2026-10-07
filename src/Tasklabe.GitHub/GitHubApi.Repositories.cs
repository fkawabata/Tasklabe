using System.Text.Json.Nodes;

namespace Tasklabe.GitHub;

/// <summary>作業するリポジトリの候補。</summary>
/// <param name="CanWrite">ブランチを作れるか（Write 以上）。</param>
public sealed record WorkRepository(string NameWithOwner, bool CanWrite);

/// <summary>リポジトリのブランチ。</summary>
/// <param name="DefaultBranch">既定のブランチ。まだコミットのない空のリポジトリでは null で、ブランチを作れない。</param>
/// <param name="Branches">既定のブランチを除くブランチ（最近コミットした順）。</param>
public sealed record RepositoryBranches(string? DefaultBranch, IReadOnlyList<string> Branches)
{
    /// <summary>まだコミットがないか（既定のブランチがなく、ブランチを作る元がない）。</summary>
    public bool IsEmpty => DefaultBranch is null && Branches.Count == 0;
}

/// <summary>Issue に紐づくブランチ（GitHub の Development）。</summary>
public sealed record LinkedBranch(string RepositoryNameWithOwner, string Name, string? Url);

public sealed partial class GitHubApi
{
    private const string ViewerRepositoriesQuery = """
        query($cursor: String) {
          viewer {
            repositories(first: 100, after: $cursor, affiliations: [OWNER, COLLABORATOR, ORGANIZATION_MEMBER],
                ownerAffiliations: [OWNER, COLLABORATOR, ORGANIZATION_MEMBER], isArchived: false,
                orderBy: { field: PUSHED_AT, direction: DESC }) {
              pageInfo { hasNextPage endCursor }
              nodes { nameWithOwner viewerPermission }
            }
          }
        }
        """;

    private const string LinkedBranchesQuery = """
        query($id: ID!) {
          node(id: $id) {
            ... on Issue {
              linkedBranches(first: 50) { nodes { ref { name repository { nameWithOwner url } } } }
            }
          }
        }
        """;

    private const string DefaultBranchQuery = """
        query($owner: String!, $name: String!) {
          repository(owner: $owner, name: $name) { id defaultBranchRef { name target { oid } } }
        }
        """;

    private const string BranchesQuery = """
        query($owner: String!, $name: String!) {
          repository(owner: $owner, name: $name) {
            defaultBranchRef { name }
            refs(refPrefix: "refs/heads/", first: 100, orderBy: { field: TAG_COMMIT_DATE, direction: DESC }) { nodes { name } }
          }
        }
        """;

    private const string CreateLinkedBranchMutation = """
        mutation($issueId: ID!, $repositoryId: ID!, $oid: GitObjectID!, $name: String!) {
          createLinkedBranch(input: { issueId: $issueId, repositoryId: $repositoryId, oid: $oid, name: $name }) {
            linkedBranch { ref { name } }
          }
        }
        """;

    /// <summary>候補を取り過ぎないよう、最近 push したものから数える上限。</summary>
    private const int MaxWorkRepositories = 300;

    /// <summary>
    /// 作業するリポジトリの候補（要件 F-TSK-18）。自分が所有・共同編集・所属する Organization のリポジトリを、最近 push した順に返す。
    /// </summary>
    public async Task<IReadOnlyList<WorkRepository>> ListWorkRepositoriesAsync(CancellationToken ct = default)
    {
        var result = new List<WorkRepository>();
        string? cursor = null;
        do
        {
            var data = await QueryAsync(ViewerRepositoriesQuery, new JsonObject { ["cursor"] = cursor }, ct).ConfigureAwait(false);
            var repositories = data["viewer"]?["repositories"];
            foreach (var node in repositories?["nodes"]?.AsArray() ?? [])
            {
                if (node?["nameWithOwner"]?.GetValue<string>() is { } name)
                {
                    var permission = node["viewerPermission"]?.GetValue<string>();
                    result.Add(new WorkRepository(name, permission is "WRITE" or "MAINTAIN" or "ADMIN"));
                }
            }

            cursor = repositories?["pageInfo"]?["hasNextPage"]?.GetValue<bool>() == true
                ? repositories["pageInfo"]?["endCursor"]?.GetValue<string>()
                : null;
        }
        while (cursor is not null && result.Count < MaxWorkRepositories);

        return result;
    }

    /// <summary>Issue に紐づくブランチ（要件 F-TSK-19）。</summary>
    public async Task<IReadOnlyList<LinkedBranch>> GetLinkedBranchesAsync(string issueId, CancellationToken ct = default)
    {
        var data = await QueryAsync(LinkedBranchesQuery, new JsonObject { ["id"] = issueId }, ct).ConfigureAwait(false);
        var branches = new List<LinkedBranch>();
        foreach (var node in data["node"]?["linkedBranches"]?["nodes"]?.AsArray() ?? [])
        {
            if (node?["ref"] is { } r && r["name"]?.GetValue<string>() is { } name && r["repository"]?["nameWithOwner"]?.GetValue<string>() is { } repository)
            {
                var url = r["repository"]?["url"]?.GetValue<string>() is { } repoUrl ? $"{repoUrl}/tree/{Uri.EscapeDataString(name).Replace("%2F", "/", StringComparison.Ordinal)}" : null;
                branches.Add(new LinkedBranch(repository, name, url));
            }
        }

        return branches;
    }

    /// <summary>
    /// リポジトリの既定のブランチから、Issue に紐づくブランチを作る（要件 F-TSK-19）。
    /// GitHub の Issue の Development に並び、そのブランチの PR は Issue に紐づく。
    /// </summary>
    public async Task<LinkedBranch> CreateLinkedBranchAsync(string issueId, string repositoryNameWithOwner, string branchName, CancellationToken ct = default)
    {
        var (owner, name) = Split(repositoryNameWithOwner);
        var data = await QueryAsync(DefaultBranchQuery, new JsonObject { ["owner"] = owner, ["name"] = name }, ct).ConfigureAwait(false);
        var repository = data["repository"] ?? throw new GitHubException($"リポジトリ {repositoryNameWithOwner} が見つかりません。");
        var repositoryId = repository["id"]?.GetValue<string>() ?? throw new GitHubException($"リポジトリ {repositoryNameWithOwner} が見つかりません。");
        var oid = repository["defaultBranchRef"]?["target"]?["oid"]?.GetValue<string>()
            ?? throw new GitHubException($"{repositoryNameWithOwner} にはまだコミットがないため、ブランチを作れません。");

        var created = await QueryAsync(CreateLinkedBranchMutation,
            new JsonObject { ["issueId"] = issueId, ["repositoryId"] = repositoryId, ["oid"] = oid, ["name"] = branchName }, ct).ConfigureAwait(false);
        var createdName = created["createLinkedBranch"]?["linkedBranch"]?["ref"]?["name"]?.GetValue<string>()
            ?? throw new GitHubException("ブランチを作れませんでした。同じ名前のブランチが既にないか確かめてください。");
        return new LinkedBranch(repositoryNameWithOwner, createdName, $"https://{_host.Name}/{repositoryNameWithOwner}/tree/{createdName}");
    }

    /// <summary>
    /// リポジトリのブランチを、最近コミットした順に返す（要件 F-TSK-19）。既定のブランチは作業用ではないため一覧から除き、
    /// 名前だけを添える（空のリポジトリでは既定のブランチがない）。
    /// </summary>
    public async Task<RepositoryBranches> ListBranchesAsync(string repositoryNameWithOwner, CancellationToken ct = default)
    {
        var (owner, name) = Split(repositoryNameWithOwner);
        var data = await QueryAsync(BranchesQuery, new JsonObject { ["owner"] = owner, ["name"] = name }, ct).ConfigureAwait(false);
        var repository = data["repository"] ?? throw new GitHubException($"リポジトリ {repositoryNameWithOwner} が見つかりません。");
        var defaultBranch = repository["defaultBranchRef"]?["name"]?.GetValue<string>();
        return new RepositoryBranches(defaultBranch, [.. (repository["refs"]?["nodes"]?.AsArray() ?? [])
            .Select(n => n?["name"]?.GetValue<string>())
            .OfType<string>()
            .Where(b => b != defaultBranch)]);
    }

    private async Task<JsonObject> QueryAsync(string query, JsonObject variables, CancellationToken ct) =>
        await _client.SendAsync(query, variables, GitHubJsonContext.Default.GraphQLResponseJsonObject, ct).ConfigureAwait(false);
}
