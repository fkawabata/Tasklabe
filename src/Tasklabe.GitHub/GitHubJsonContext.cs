using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Tasklabe.GitHub.GraphQL;

namespace Tasklabe.GitHub;

/// <summary>NativeAOT に対応するための JSON ソース生成コンテキスト。</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OAuthResponse))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(GraphQLResponse<ViewerData>))]
[JsonSerializable(typeof(GraphQLResponse<RepositoryData>))]
[JsonSerializable(typeof(GraphQLResponse<CreateRepositoryData>))]
[JsonSerializable(typeof(GraphQLResponse<ViewerProjectsData>))]
[JsonSerializable(typeof(GraphQLResponse<ProjectNodeData>))]
[JsonSerializable(typeof(GraphQLResponse<CreateProjectData>))]
[JsonSerializable(typeof(GraphQLResponse<MutationData>))]
[JsonSerializable(typeof(GraphQLResponse<CreateIssueData>))]
[JsonSerializable(typeof(GraphQLResponse<AddProjectItemData>))]
[JsonSerializable(typeof(GraphQLResponse<ItemValueData>))]
[JsonSerializable(typeof(GraphQLResponse<ItemNodeData>))]
[JsonSerializable(typeof(GraphQLResponse<ViewerOrganizationsData>))]
[JsonSerializable(typeof(GraphQLResponse<OrganizationData>))]
[JsonSerializable(typeof(GraphQLResponse<AssignableUsersData>))]
[JsonSerializable(typeof(GraphQLResponse<TransferIssueData>))]
[JsonSerializable(typeof(GraphQLResponse<JsonObject>))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;
