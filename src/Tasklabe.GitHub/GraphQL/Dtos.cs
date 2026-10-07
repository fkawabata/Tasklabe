using System.Text.Json.Serialization;

namespace Tasklabe.GitHub.GraphQL;

// GraphQL 応答の受け皿。フィールド名は camelCase で対応させる（GitHubJsonContext の設定）。

internal sealed class GraphQLResponse<T>
{
    public T? Data { get; set; }
    public List<GraphQLError>? Errors { get; set; }
}

internal sealed class GraphQLError
{
    public string? Message { get; set; }
    public string? Type { get; set; }
}

internal sealed class RateLimitDto
{
    public int Remaining { get; set; }
    public int Limit { get; set; }
    public DateTimeOffset ResetAt { get; set; }
}

internal sealed class Connection<T>
{
    public List<T>? Nodes { get; set; }
    public PageInfo? PageInfo { get; set; }
    public int TotalCount { get; set; }
}

internal sealed class PageInfo
{
    public bool HasNextPage { get; set; }
    public string? EndCursor { get; set; }
}

internal sealed class NodeRef
{
    public string? Id { get; set; }
}

internal sealed class LoginRef
{
    public string? Login { get; set; }
}

internal sealed class IssueProjectItemsDto
{
    public Connection<ProjectItemRef>? ProjectItems { get; set; }
}

internal sealed class ProjectItemRef
{
    public string? Id { get; set; }
    public NodeRef? Project { get; set; }
}

// --- viewer ---

internal sealed class ViewerData
{
    public UserDto? Viewer { get; set; }
    public RateLimitDto? RateLimit { get; set; }
}

internal sealed class UserDto
{
    public string Id { get; set; } = "";
    public string Login { get; set; } = "";
    public string? Name { get; set; }
    public string? AvatarUrl { get; set; }
}

// --- repository ---

internal sealed class RepositoryData
{
    public RepositoryDto? Repository { get; set; }
}

internal sealed class RepositoryDto
{
    public string Id { get; set; } = "";
    public string NameWithOwner { get; set; } = "";
    public Connection<NodeRef>? Issues { get; set; }

    /// <summary>Issue と、それが入っている Project のアイテム（RepositoryIssueItemsSince の別名 issueItems）。</summary>
    public Connection<IssueProjectItemsDto>? IssueItems { get; set; }

    /// <summary>自分のリポジトリへの権限（ADMIN / MAINTAIN / WRITE / TRIAGE / READ）。</summary>
    public string? ViewerPermission { get; set; }
}

internal sealed class CreateRepositoryData
{
    public CreateRepositoryPayload? CreateRepository { get; set; }
}

internal sealed class CreateRepositoryPayload
{
    public RepositoryDto? Repository { get; set; }
}

// --- projects ---

internal sealed class ViewerProjectsData
{
    public ViewerProjectsDto? Viewer { get; set; }
    public RateLimitDto? RateLimit { get; set; }
}

internal sealed class ViewerProjectsDto
{
    public Connection<ProjectDto>? ProjectsV2 { get; set; }
}

internal sealed class ProjectNodeData
{
    public ProjectDto? Node { get; set; }
    public RateLimitDto? RateLimit { get; set; }
}

internal sealed class ProjectDto
{
    public string Id { get; set; } = "";
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string? ShortDescription { get; set; }

    /// <summary>README（プロジェクトの設定の注記を含む）。</summary>
    public string? Readme { get; set; }
    public string? Url { get; set; }
    public bool Closed { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>自分がこの Project を編集できるか。</summary>
    public bool? ViewerCanUpdate { get; set; }
    public OwnerDto? Owner { get; set; }
    public Connection<RepositoryDto>? Repositories { get; set; }
    public Connection<FieldDto>? Fields { get; set; }
    public Connection<ItemDto>? Items { get; set; }
}

internal sealed class OwnerDto
{
    [JsonPropertyName("__typename")] public string? TypeName { get; set; }
    public string? Id { get; set; }
    public string? Login { get; set; }
}

internal sealed class FieldDto
{
    [JsonPropertyName("__typename")] public string? TypeName { get; set; }
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? DataType { get; set; }
    public List<OptionDto>? Options { get; set; }
}

internal sealed class OptionDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Color { get; set; }
    public string? Description { get; set; }
}

internal sealed class ItemDto
{
    public string Id { get; set; } = "";
    public bool IsArchived { get; set; }

    /// <summary>アイテムが属する Project（ID で個別に取得したときだけ入る）。</summary>
    public NodeRef? Project { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Connection<FieldValueDto>? FieldValues { get; set; }
    public ContentDto? Content { get; set; }
}

internal sealed class FieldValueDto
{
    [JsonPropertyName("__typename")] public string? TypeName { get; set; }
    public string? Date { get; set; }
    public double? Number { get; set; }
    public string? Text { get; set; }
    public string? OptionId { get; set; }
    public string? Name { get; set; }
    public FieldDto? Field { get; set; }
}

internal sealed class ContentDto
{
    [JsonPropertyName("__typename")] public string? TypeName { get; set; }
    public string? Id { get; set; }
    public int Number { get; set; }
    public string? Title { get; set; }
    public string? Body { get; set; }
    public string? State { get; set; }

    /// <summary>Close の理由（COMPLETED / NOT_PLANNED など）。</summary>
    public string? StateReason { get; set; }
    public string? Url { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public RepositoryDto? Repository { get; set; }
    public Connection<LoginRef>? Assignees { get; set; }
    public NodeRef? Parent { get; set; }
    public Connection<NodeRef>? BlockedBy { get; set; }
    public NodeRef? Milestone { get; set; }
}

// --- mutations ---

internal sealed class CreateProjectData
{
    public ProjectPayload? CreateProjectV2 { get; set; }
}

internal sealed class ProjectPayload
{
    public ProjectDto? ProjectV2 { get; set; }
}

internal sealed class CreateIssueData
{
    public CreateIssuePayload? CreateIssue { get; set; }
}

internal sealed class CreateIssuePayload
{
    public ContentDto? Issue { get; set; }
}

internal sealed class AddProjectItemData
{
    public AddProjectItemPayload? AddProjectV2ItemById { get; set; }
}

internal sealed class AddProjectItemPayload
{
    public NodeRef? Item { get; set; }
}

internal sealed class ItemNodeData
{
    public ItemDto? Node { get; set; }
}

internal sealed class ItemValueData
{
    public ItemValueDto? Node { get; set; }
}

internal sealed class ItemValueDto
{
    public FieldValueDto? FieldValueByName { get; set; }
    public ContentDto? Content { get; set; }
}

internal sealed class ViewerOrganizationsData
{
    public ViewerOrganizationsDto? Viewer { get; set; }
}

internal sealed class ViewerOrganizationsDto
{
    public Connection<OrganizationDto>? Organizations { get; set; }
}

internal sealed class OrganizationDto
{
    public string Id { get; set; } = "";
    public string Login { get; set; } = "";
    public string? Name { get; set; }
    public bool ViewerCanCreateProjects { get; set; }
    public bool ViewerCanCreateRepositories { get; set; }
    public Connection<ProjectDto>? ProjectsV2 { get; set; }
    public Connection<OrganizationRepositoryDto>? Repositories { get; set; }
}

internal sealed class OrganizationRepositoryDto
{
    public string Id { get; set; } = "";
    public string NameWithOwner { get; set; } = "";
    public string? ViewerPermission { get; set; }
}

internal sealed class OrganizationData
{
    public OrganizationDto? Organization { get; set; }
    public RateLimitDto? RateLimit { get; set; }
}

internal sealed class AssignableUsersData
{
    public AssignableRepositoryDto? Repository { get; set; }
}

internal sealed class AssignableRepositoryDto
{
    public Connection<UserDto>? AssignableUsers { get; set; }
}

internal sealed class TransferIssueData
{
    public CreateIssuePayload? TransferIssue { get; set; }
}

internal sealed class MutationData
{
    // 結果を使わない mutation 用
    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Rest { get; set; }
}
