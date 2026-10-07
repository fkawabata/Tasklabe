using System.Text.Json.Nodes;
using Tasklabe.Core.Domain;
using Tasklabe.GitHub.GraphQL;

namespace Tasklabe.GitHub;

/// <summary>Organization のリソースにアクセスできない（技術設計書 6.1 節）。</summary>
public sealed class GitHubAccessException(OrganizationAccessProblem problem, string message)
    : GitHubException(message)
{
    public OrganizationAccessProblem Problem { get; } = problem;
}

/// <summary>Organization のリポジトリ。</summary>
public sealed record OrganizationRepository(string Id, string NameWithOwner, bool CanWrite);

public sealed partial class GitHubApi
{
    /// <summary>
    /// GraphQL のエラーから、Organization にアクセスできない理由を判定する。該当しない場合は None。
    /// </summary>
    public static OrganizationAccessProblem ClassifyAccessProblem(GitHubGraphQLException ex)
    {
        var message = ex.Message;
        if (message.Contains("OAuth App access restrictions", StringComparison.OrdinalIgnoreCase))
        {
            return OrganizationAccessProblem.OAuthAppNotApproved;
        }

        if (message.Contains("SAML", StringComparison.OrdinalIgnoreCase))
        {
            return OrganizationAccessProblem.SamlSsoRequired;
        }

        return ex.ErrorTypes.Contains("FORBIDDEN") ? OrganizationAccessProblem.Forbidden : OrganizationAccessProblem.None;
    }

    public async Task<IReadOnlyList<Organization>> ListOrganizationsAsync(CancellationToken ct = default)
    {
        var data = await _client.SendAsync(Queries.ViewerOrganizations, null,
            GitHubJsonContext.Default.GraphQLResponseViewerOrganizationsData, ct).ConfigureAwait(false);
        return data.Viewer?.Organizations?.Nodes?
            .Select(o => new Organization(o.Id, o.Login, o.Name, o.ViewerCanCreateProjects, o.ViewerCanCreateRepositories))
            .ToList() ?? [];
    }

    /// <summary>
    /// Organization が所有する、アプリの管理対象の Project。アクセスできない場合は
    /// <see cref="GitHubAccessException"/> を投げる。
    /// </summary>
    public async Task<IReadOnlyList<ProjectSummary>> ListOrganizationProjectsAsync(string login, CancellationToken ct = default)
    {
        var result = new List<ProjectSummary>();
        string? cursor = null;
        do
        {
            var data = await SendOrganizationQueryAsync(Queries.OrganizationProjects,
                new JsonObject { ["login"] = login, ["cursor"] = cursor }, ct).ConfigureAwait(false);
            var page = data.Organization?.ProjectsV2;
            foreach (var p in page?.Nodes ?? [])
            {
                if (!p.Closed && ProjectConventions.IsManaged(p.ShortDescription))
                {
                    result.Add(new ProjectSummary(p.Id, p.Title, p.UpdatedAt, false,
                        p.Repositories?.Nodes?.FirstOrDefault()?.NameWithOwner));
                }
            }

            cursor = page?.PageInfo is { HasNextPage: true } pi ? pi.EndCursor : null;
        }
        while (cursor is not null);

        return result;
    }

    /// <summary>Organization のリポジトリ（更新日時の新しい順）。</summary>
    public async Task<IReadOnlyList<OrganizationRepository>> ListOrganizationRepositoriesAsync(string login, CancellationToken ct = default)
    {
        var data = await SendOrganizationQueryAsync(Queries.OrganizationRepositories, new JsonObject { ["login"] = login }, ct).ConfigureAwait(false);
        return data.Organization?.Repositories?.Nodes?
            .Select(r => new OrganizationRepository(r.Id, r.NameWithOwner, r.ViewerPermission is "ADMIN" or "MAINTAIN" or "WRITE"))
            .ToList() ?? [];
    }

    public async Task<RepositoryInfo> CreateOrganizationRepositoryAsync(string organizationId, string name, string description, CancellationToken ct = default)
    {
        var data = await _client.SendAsync(Queries.CreateOwnedRepository,
            new JsonObject { ["ownerId"] = organizationId, ["name"] = name, ["description"] = description },
            GitHubJsonContext.Default.GraphQLResponseCreateRepositoryData, ct).ConfigureAwait(false);
        var r = data.CreateRepository?.Repository ?? throw new GitHubException("リポジトリを作成できませんでした。");
        return new RepositoryInfo(r.Id, r.NameWithOwner);
    }

    /// <summary>リポジトリの Issue に割り当てられる利用者。</summary>
    public async Task<IReadOnlyList<Assignee>> GetAssignableUsersAsync(string nameWithOwner, CancellationToken ct = default)
    {
        var (owner, name) = Split(nameWithOwner);
        var data = await _client.SendAsync(Queries.AssignableUsers, new JsonObject { ["owner"] = owner, ["name"] = name },
            GitHubJsonContext.Default.GraphQLResponseAssignableUsersData, ct).ConfigureAwait(false);
        return data.Repository?.AssignableUsers?.Nodes?.Select(u => new Assignee(u.Id, u.Login, u.Name)).ToList() ?? [];
    }

    /// <summary>担当者を置き換える。</summary>
    public Task SetAssigneesAsync(string issueId, IReadOnlyList<string> userIds, CancellationToken ct = default)
    {
        var ids = new JsonArray();
        foreach (var id in userIds)
        {
            ids.Add((JsonNode?)JsonValue.Create(id));
        }

        return MutateAsync(Queries.UpdateIssueAssignees, new JsonObject { ["id"] = issueId, ["assigneeIds"] = ids }, ct);
    }

    /// <summary>Issue を別のリポジトリへ転送し、転送後の Issue を返す。</summary>
    public async Task<CreatedIssue> TransferIssueAsync(string issueId, string repositoryId, CancellationToken ct = default)
    {
        var data = await _client.SendAsync(Queries.TransferIssue, new JsonObject { ["issueId"] = issueId, ["repositoryId"] = repositoryId },
            GitHubJsonContext.Default.GraphQLResponseTransferIssueData, ct).ConfigureAwait(false);
        var issue = data.TransferIssue?.Issue ?? throw new GitHubException("Issue を移動できませんでした。");
        return new CreatedIssue(issue.Id!, issue.Number, issue.Url);
    }

    private async Task<OrganizationData> SendOrganizationQueryAsync(string query, JsonObject variables, CancellationToken ct)
    {
        try
        {
            return await _client.SendAsync(query, variables, GitHubJsonContext.Default.GraphQLResponseOrganizationData, ct).ConfigureAwait(false);
        }
        catch (GitHubGraphQLException ex) when (ClassifyAccessProblem(ex) is not OrganizationAccessProblem.None and var problem)
        {
            throw new GitHubAccessException(problem, ex.Message);
        }
    }
}
