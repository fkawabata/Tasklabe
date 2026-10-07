namespace Tasklabe.GitHub.GraphQL;

internal static class Queries
{
    private const string RateLimit = "rateLimit { remaining limit resetAt }";

    public const string Viewer = $$"""
        query { viewer { id login name avatarUrl } {{RateLimit}} }
        """;

    public const string Repository = """
        query($owner: String!, $name: String!) {
          repository(owner: $owner, name: $name) { id nameWithOwner }
        }
        """;

    public const string RepositoryIssuesSince = """
        query($owner: String!, $name: String!, $since: DateTime!) {
          repository(owner: $owner, name: $name) {
            id nameWithOwner
            issues(first: 1, filterBy: { since: $since }) { totalCount }
          }
        }
        """;

    public const string RepositoryIssueItemsSince = """
        query($owner: String!, $name: String!, $since: DateTime!, $cursor: String) {
          repository(owner: $owner, name: $name) {
            id nameWithOwner
            issueItems: issues(first: 100, after: $cursor, filterBy: { since: $since }) {
              pageInfo { hasNextPage endCursor }
              nodes { projectItems(first: 20) { nodes { id project { id } } } }
            }
          }
        }
        """;

    public const string CreateRepository = """
        mutation($name: String!, $description: String!) {
          createRepository(input: { name: $name, visibility: PRIVATE, description: $description, hasIssuesEnabled: true }) {
            repository { id nameWithOwner }
          }
        }
        """;

    public const string ViewerProjects = $$"""
        query($cursor: String) {
          viewer {
            projectsV2(first: 100, after: $cursor) {
              pageInfo { hasNextPage endCursor }
              nodes {
                id number title shortDescription updatedAt closed
                repositories(first: 1) { nodes { id nameWithOwner } }
              }
            }
          }
          {{RateLimit}}
        }
        """;

    private const string ProjectHeader = """
        id number title shortDescription readme url closed updatedAt
        owner { __typename ... on User { id login } ... on Organization { id login } }
        repositories(first: 1) { nodes { id nameWithOwner } }
        fields(first: 50) {
          nodes {
            __typename
            ... on ProjectV2FieldCommon { id name dataType }
            ... on ProjectV2SingleSelectField { options { id name color description } }
          }
        }
        """;

    public const string ProjectSchema = $$"""
        query($id: ID!) {
          node(id: $id) { ... on ProjectV2 { {{ProjectHeader}} } }
          {{RateLimit}}
        }
        """;

    public const string ProjectWithItems = $$"""
        query($id: ID!, $cursor: String) {
          node(id: $id) {
            ... on ProjectV2 {
              {{ProjectHeader}}
              items(first: 100, after: $cursor) {
                pageInfo { hasNextPage endCursor }
                nodes { {{ItemFields}} }
              }
            }
          }
          {{RateLimit}}
        }
        """;

    /// <summary>Project のアイテムとして取り込む項目（一覧と個別の取得で共通）。</summary>
    private const string ItemFields = """
        id isArchived updatedAt
        fieldValues(first: 30) {
          nodes {
            __typename
            ... on ProjectV2ItemFieldDateValue { date field { ... on ProjectV2FieldCommon { name } } }
            ... on ProjectV2ItemFieldNumberValue { number field { ... on ProjectV2FieldCommon { name } } }
            ... on ProjectV2ItemFieldTextValue { text field { ... on ProjectV2FieldCommon { name } } }
            ... on ProjectV2ItemFieldSingleSelectValue { optionId name field { ... on ProjectV2FieldCommon { name } } }
          }
        }
        content {
          __typename
          ... on Issue {
            id number title body state stateReason url updatedAt
            repository { id nameWithOwner }
            assignees(first: 10) { nodes { login } }
            parent { id }
            blockedBy(first: 20) { nodes { id } }
            milestone { id }
          }
        }
        """;

    /// <summary>自分の Project とリポジトリへの権限。</summary>
    public const string ProjectAccess = """
        query($id: ID!) {
          node(id: $id) {
            ... on ProjectV2 { id url viewerCanUpdate repositories(first: 1) { nodes { id nameWithOwner viewerPermission } } }
          }
        }
        """;

    /// <summary>アイテムを ID で 1 件取得する。Project の一覧に反映される前のアイテムも取れる。</summary>
    public const string ItemById = $$"""
        query($id: ID!) {
          node(id: $id) { ... on ProjectV2Item { {{ItemFields}} project { id } } }
        }
        """;

    public const string CreateProject = """
        mutation($ownerId: ID!, $title: String!) {
          createProjectV2(input: { ownerId: $ownerId, title: $title }) { projectV2 { id number } }
        }
        """;

    public const string UpdateProjectDescription = """
        mutation($id: ID!, $description: String!) {
          updateProjectV2(input: { projectId: $id, shortDescription: $description }) { projectV2 { id } }
        }
        """;

    public const string ProjectReadme = """
        query($id: ID!) {
          node(id: $id) { ... on ProjectV2 { id readme } }
        }
        """;

    public const string UpdateProjectReadme = """
        mutation($id: ID!, $readme: String!) {
          updateProjectV2(input: { projectId: $id, readme: $readme }) { projectV2 { id } }
        }
        """;

    public const string UpdateProjectTitle = """
        mutation($id: ID!, $title: String!) {
          updateProjectV2(input: { projectId: $id, title: $title }) { projectV2 { id } }
        }
        """;

    public const string SetProjectClosed = """
        mutation($id: ID!, $closed: Boolean!) {
          updateProjectV2(input: { projectId: $id, closed: $closed }) { projectV2 { id } }
        }
        """;

    public const string LinkRepository = """
        mutation($projectId: ID!, $repositoryId: ID!) {
          linkProjectV2ToRepository(input: { projectId: $projectId, repositoryId: $repositoryId }) { repository { id } }
        }
        """;

    public const string CreateField = """
        mutation($projectId: ID!, $name: String!, $dataType: ProjectV2CustomFieldType!, $options: [ProjectV2SingleSelectFieldOptionInput!]) {
          createProjectV2Field(input: { projectId: $projectId, name: $name, dataType: $dataType, singleSelectOptions: $options }) {
            projectV2Field { ... on ProjectV2FieldCommon { id } }
          }
        }
        """;

    public const string UpdateSingleSelectOptions = """
        mutation($fieldId: ID!, $options: [ProjectV2SingleSelectFieldOptionInput!]) {
          updateProjectV2Field(input: { fieldId: $fieldId, singleSelectOptions: $options }) {
            projectV2Field { ... on ProjectV2FieldCommon { id } }
          }
        }
        """;

    // ---------------------------------------------------------------- タスクの編集

    public const string CreateIssue = """
        mutation($repositoryId: ID!, $title: String!, $body: String) {
          createIssue(input: { repositoryId: $repositoryId, title: $title, body: $body }) { issue { id number url } }
        }
        """;

    public const string AddProjectItem = """
        mutation($projectId: ID!, $contentId: ID!) {
          addProjectV2ItemById(input: { projectId: $projectId, contentId: $contentId }) { item { id } }
        }
        """;

    public const string DeleteProjectItem = """
        mutation($projectId: ID!, $itemId: ID!) {
          deleteProjectV2Item(input: { projectId: $projectId, itemId: $itemId }) { deletedItemId }
        }
        """;

    public const string UpdateItemField = """
        mutation($projectId: ID!, $itemId: ID!, $fieldId: ID!, $value: ProjectV2FieldValue!) {
          updateProjectV2ItemFieldValue(input: { projectId: $projectId, itemId: $itemId, fieldId: $fieldId, value: $value }) {
            projectV2Item { id }
          }
        }
        """;

    public const string ClearItemField = """
        mutation($projectId: ID!, $itemId: ID!, $fieldId: ID!) {
          clearProjectV2ItemFieldValue(input: { projectId: $projectId, itemId: $itemId, fieldId: $fieldId }) {
            projectV2Item { id }
          }
        }
        """;

    public const string UpdateIssueTitle = """
        mutation($id: ID!, $title: String!) { updateIssue(input: { id: $id, title: $title }) { issue { id } } }
        """;

    public const string UpdateIssueBody = """
        mutation($id: ID!, $body: String!) { updateIssue(input: { id: $id, body: $body }) { issue { id } } }
        """;

    public const string CloseIssue = """
        mutation($id: ID!, $reason: IssueClosedStateReason) { closeIssue(input: { issueId: $id, stateReason: $reason }) { issue { id } } }
        """;

    public const string ReopenIssue = """
        mutation($id: ID!) { reopenIssue(input: { issueId: $id }) { issue { id } } }
        """;

    public const string DeleteIssue = """
        mutation($id: ID!) { deleteIssue(input: { issueId: $id }) { clientMutationId } }
        """;

    /// <summary>競合の検出用に、項目の現在値を取得する。</summary>
    public const string ItemCurrentValue = """
        query($itemId: ID!, $field: String!) {
          node(id: $itemId) {
            ... on ProjectV2Item {
              fieldValueByName(name: $field) {
                __typename
                ... on ProjectV2ItemFieldDateValue { date }
                ... on ProjectV2ItemFieldNumberValue { number }
                ... on ProjectV2ItemFieldTextValue { text }
                ... on ProjectV2ItemFieldSingleSelectValue { optionId name }
              }
              content { __typename ... on Issue { id number title body state url updatedAt assignees(first: 20) { nodes { login } } parent { id } blockedBy(first: 50) { nodes { id } } milestone { id } } }
            }
          }
        }
        """;

    // ---------------------------------------------------------------- チーム（Organization）

    public const string ViewerOrganizations = """
        query {
          viewer {
            organizations(first: 100) {
              nodes { id login name viewerCanCreateProjects viewerCanCreateRepositories }
            }
          }
        }
        """;

    public const string OrganizationProjects = $$"""
        query($login: String!, $cursor: String) {
          organization(login: $login) {
            projectsV2(first: 100, after: $cursor) {
              pageInfo { hasNextPage endCursor }
              nodes {
                id number title shortDescription updatedAt closed
                repositories(first: 1) { nodes { id nameWithOwner } }
              }
            }
          }
          {{RateLimit}}
        }
        """;

    public const string OrganizationRepositories = """
        query($login: String!) {
          organization(login: $login) {
            repositories(first: 100, orderBy: { field: UPDATED_AT, direction: DESC }, isArchived: false) {
              nodes { id nameWithOwner viewerPermission }
            }
          }
        }
        """;

    public const string CreateOwnedRepository = """
        mutation($ownerId: ID!, $name: String!, $description: String!) {
          createRepository(input: { ownerId: $ownerId, name: $name, visibility: PRIVATE, description: $description, hasIssuesEnabled: true }) {
            repository { id nameWithOwner }
          }
        }
        """;

    public const string AssignableUsers = """
        query($owner: String!, $name: String!) {
          repository(owner: $owner, name: $name) {
            assignableUsers(first: 100) { nodes { id login name } }
          }
        }
        """;

    public const string UpdateIssueAssignees = """
        mutation($id: ID!, $assigneeIds: [ID!]!) { updateIssue(input: { id: $id, assigneeIds: $assigneeIds }) { issue { id } } }
        """;

    public const string AddSubIssue = """
        mutation($parentId: ID!, $childId: ID!) {
          addSubIssue(input: { issueId: $parentId, subIssueId: $childId, replaceParent: true }) { issue { id } }
        }
        """;

    public const string RemoveSubIssue = """
        mutation($parentId: ID!, $childId: ID!) {
          removeSubIssue(input: { issueId: $parentId, subIssueId: $childId }) { issue { id } }
        }
        """;

    public const string AddBlockedBy = """
        mutation($issueId: ID!, $blockingId: ID!) {
          addBlockedBy(input: { issueId: $issueId, blockingIssueId: $blockingId }) { issue { id } }
        }
        """;

    public const string RemoveBlockedBy = """
        mutation($issueId: ID!, $blockingId: ID!) {
          removeBlockedBy(input: { issueId: $issueId, blockingIssueId: $blockingId }) { issue { id } }
        }
        """;

    public const string UpdateItemPosition = """
        mutation($projectId: ID!, $itemId: ID!, $afterId: ID) {
          updateProjectV2ItemPosition(input: { projectId: $projectId, itemId: $itemId, afterId: $afterId }) { clientMutationId }
        }
        """;

    public const string TransferIssue = """
        mutation($issueId: ID!, $repositoryId: ID!) {
          transferIssue(input: { issueId: $issueId, repositoryId: $repositoryId, createLabelsIfMissing: true }) {
            issue { id number url }
          }
        }
        """;
}
