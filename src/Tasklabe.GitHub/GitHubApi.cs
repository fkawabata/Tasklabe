using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Milestones;
using Tasklabe.Core.Settings;
using Tasklabe.GitHub.GraphQL;

namespace Tasklabe.GitHub;

public sealed record GitHubUser(string Id, string Login, string? Name, string? AvatarUrl);

public sealed record RepositoryInfo(string Id, string NameWithOwner);

/// <summary>変更検出用の Project の要約。</summary>
public sealed record ProjectSummary(string Id, string Title, DateTimeOffset UpdatedAt, bool IsInbox, string? RepositoryNameWithOwner);

/// <summary>自分の Project とリポジトリへの権限。</summary>
/// <param name="RepositoryNameWithOwner">Project にリンクしたリポジトリ（なければ null）。</param>
/// <param name="CanWriteRepository">そのリポジトリに Issue を作れるか（Write 以上）。</param>
public sealed record ProjectAccess(bool CanUpdateProject, string? RepositoryNameWithOwner, bool CanWriteRepository);

/// <summary>取得した Project とタスク。</summary>
/// <param name="StatusRepairs">GitHub の Status を、読み取った値へ書き戻すアイテム（アイテムの ID → 選択肢の ID）。</param>
public sealed record ProjectSnapshot(Project Project, IReadOnlyList<TaskItem> Tasks, IReadOnlyDictionary<string, string>? StatusRepairs = null);

public sealed record RateLimit(int Remaining, int Limit, DateTimeOffset ResetAt);

/// <summary>Tasklabe が用いる GitHub の操作。</summary>
public sealed partial class GitHubApi
{
    private readonly GraphQLClient _client;
    private readonly HttpClient _http;
    private readonly GitHubHost _host;
    private readonly Func<string?> _tokenProvider;

    public GitHubApi(HttpClient http, GitHubHost host, Func<string?> tokenProvider)
    {
        _client = new GraphQLClient(http, host, tokenProvider);
        _http = http;
        _host = host;
        _tokenProvider = tokenProvider;
    }

    public RateLimit? LastRateLimit =>
        _client.LastRateLimit is { } r ? new RateLimit(r.Remaining, r.Limit, r.ResetAt) : null;

    public async Task<GitHubUser> GetViewerAsync(CancellationToken ct = default)
    {
        var data = await _client.SendAsync(Queries.Viewer, null, GitHubJsonContext.Default.GraphQLResponseViewerData, ct).ConfigureAwait(false);
        var v = data.Viewer ?? throw new GitHubException("ユーザー情報を取得できませんでした。");
        return new GitHubUser(v.Id, v.Login, v.Name, v.AvatarUrl);
    }

    // ---------------------------------------------------------------- repositories

    public async Task<RepositoryInfo?> FindRepositoryAsync(string owner, string name, CancellationToken ct = default)
    {
        try
        {
            var data = await _client.SendAsync(Queries.Repository, new JsonObject { ["owner"] = owner, ["name"] = name },
                GitHubJsonContext.Default.GraphQLResponseRepositoryData, ct).ConfigureAwait(false);
            return data.Repository is { } r ? new RepositoryInfo(r.Id, r.NameWithOwner) : null;
        }
        catch (GitHubGraphQLException ex) when (ex.ErrorTypes.Contains("NOT_FOUND"))
        {
            return null;
        }
    }

    public async Task<RepositoryInfo> CreatePrivateRepositoryAsync(string name, string description, CancellationToken ct = default)
    {
        var data = await _client.SendAsync(Queries.CreateRepository, new JsonObject { ["name"] = name, ["description"] = description },
            GitHubJsonContext.Default.GraphQLResponseCreateRepositoryData, ct).ConfigureAwait(false);
        var r = data.CreateRepository?.Repository ?? throw new GitHubException("リポジトリを作成できませんでした。");
        return new RepositoryInfo(r.Id, r.NameWithOwner);
    }

    /// <summary>指定日時以降に更新された Issue がリポジトリにあるか。</summary>
    public async Task<bool> HasIssueChangesSinceAsync(string nameWithOwner, DateTimeOffset since, CancellationToken ct = default)
    {
        var (owner, name) = Split(nameWithOwner);
        var data = await _client.SendAsync(Queries.RepositoryIssuesSince,
            new JsonObject { ["owner"] = owner, ["name"] = name, ["since"] = since.UtcDateTime.ToString("O") },
            GitHubJsonContext.Default.GraphQLResponseRepositoryData, ct).ConfigureAwait(false);
        return data.Repository?.Issues?.TotalCount > 0;
    }

    /// <summary>
    /// since 以降に更新されたリポジトリの Issue のうち、指定した Project に入っているもののアイテムの ID。
    /// Project のアイテム一覧は、追加したばかりのアイテムを数分以上返さないことがある。ほかの端末やアプリの外で作られたタスクは
    /// 手元に ID がないため、Issue の側から ID を拾い、一覧に出ていなくても ID で読めるようにする（GetProjectAsync の knownItemIds）。
    /// </summary>
    public async Task<IReadOnlyList<string>> GetProjectItemIdsOfIssuesSinceAsync(string nameWithOwner, string projectId, DateTimeOffset since,
        CancellationToken ct = default)
    {
        // 変更が極端に多いときに問い合わせが続かないよう、読む量に上限を設ける（足りない分は一覧から取れる）
        const int MaxPages = 10;
        var (owner, name) = Split(nameWithOwner);
        var ids = new List<string>();
        string? cursor = null;
        for (int page = 0; page < MaxPages; page++)
        {
            var data = await _client.SendAsync(Queries.RepositoryIssueItemsSince,
                new JsonObject { ["owner"] = owner, ["name"] = name, ["since"] = since.UtcDateTime.ToString("O"), ["cursor"] = cursor },
                GitHubJsonContext.Default.GraphQLResponseRepositoryData, ct).ConfigureAwait(false);
            var issues = data.Repository?.IssueItems;
            foreach (var issue in issues?.Nodes ?? [])
            {
                ids.AddRange((issue.ProjectItems?.Nodes ?? [])
                    .Where(i => i.Id is not null && i.Project?.Id == projectId)
                    .Select(i => i.Id!));
            }

            cursor = issues?.PageInfo is { HasNextPage: true } pi ? pi.EndCursor : null;
            if (cursor is null)
            {
                break;
            }
        }

        return ids;
    }

    // ---------------------------------------------------------------- projects

    /// <summary>利用者が所有する、アプリの管理対象の Project。</summary>
    public async Task<IReadOnlyList<ProjectSummary>> ListViewerProjectsAsync(CancellationToken ct = default)
    {
        var result = new List<ProjectSummary>();
        string? cursor = null;
        do
        {
            var data = await _client.SendAsync(Queries.ViewerProjects, new JsonObject { ["cursor"] = cursor },
                GitHubJsonContext.Default.GraphQLResponseViewerProjectsData, ct).ConfigureAwait(false);
            var page = data.Viewer?.ProjectsV2;
            foreach (var p in page?.Nodes ?? [])
            {
                if (!p.Closed && ProjectConventions.IsManaged(p.ShortDescription))
                {
                    result.Add(new ProjectSummary(p.Id, p.Title, p.UpdatedAt,
                        ProjectConventions.IsInbox(p.ShortDescription),
                        p.Repositories?.Nodes?.FirstOrDefault()?.NameWithOwner));
                }
            }

            cursor = page?.PageInfo is { HasNextPage: true } pi ? pi.EndCursor : null;
        }
        while (cursor is not null);

        return result;
    }

    /// <summary>Project とすべてのタスクを取得する。</summary>
    /// <param name="knownItemIds">
    /// 手元で把握しているアイテムの ID。GitHub の Project のアイテム一覧は、追加したばかりのアイテムを数分以上
    /// 返さないことがあるため、一覧になかったものは ID で 1 件ずつ確かめ、まだ Project にあれば含める。
    /// </param>
    public async Task<ProjectSnapshot> GetProjectAsync(string projectId, CancellationToken ct = default,
        IEnumerable<string>? knownItemIds = null)
    {
        Project? project = null;
        var tasks = new List<TaskItem>();
        var explicitIssues = new HashSet<string>(StringComparer.Ordinal);
        var repairs = new Dictionary<string, string>(StringComparer.Ordinal);
        string? cursor = null;
        int position = 0;
        do
        {
            var data = await _client.SendAsync(Queries.ProjectWithItems, new JsonObject { ["id"] = projectId, ["cursor"] = cursor },
                GitHubJsonContext.Default.GraphQLResponseProjectNodeData, ct).ConfigureAwait(false);
            var dto = data.Node ?? throw new GitHubException("プロジェクトが見つかりません。");
            project ??= ProjectMapper.ToProject(dto);

            foreach (var item in dto.Items?.Nodes ?? [])
            {
                if (ProjectMapper.ToTask(item, project, position++) is { } task)
                {
                    tasks.Add(task);
                    if (task.StatusOptionId is { } optionId && optionId != ProjectMapper.StatusOptionId(item))
                    {
                        repairs[task.ItemId] = optionId;
                    }

                    if (ProjectMapper.HasExplicitIssueKind(item))
                    {
                        explicitIssues.Add(task.IssueId);
                    }
                }
            }

            cursor = dto.Items?.PageInfo is { HasNextPage: true } pi ? pi.EndCursor : null;
        }
        while (cursor is not null);

        var listed = tasks.Select(t => t.ItemId).ToHashSet(StringComparer.Ordinal);
        foreach (var itemId in knownItemIds?.Where(id => !listed.Contains(id)).Distinct(StringComparer.Ordinal) ?? [])
        {
            if (await GetItemAsync(itemId, ct).ConfigureAwait(false) is { } item
                && item.Project?.Id == projectId
                && ProjectMapper.ToTask(item, project!, position++) is { } task)
            {
                tasks.Add(task);
                if (ProjectMapper.HasExplicitIssueKind(item))
                {
                    explicitIssues.Add(task.IssueId);
                }
            }
        }

        // マイルストーンは、つないだリポジトリの Milestone のうち、このプロジェクトのものを読む（要件 F-MS-01）。
        // リポジトリを読めない（権限がない、消えた）ときも、プロジェクトの取得は止めず、マイルストーンなしとする
        if (project!.RepositoryNameWithOwner is { } repository)
        {
            try
            {
                var milestones = await GetMilestonesAsync(repository, ct).ConfigureAwait(false);
                project = project with { Milestones = MilestonePlan.ForProject(project.Id, milestones, tasks) };
            }
            catch (GitHubGraphQLException)
            {
            }
        }

        return new ProjectSnapshot(project, ProjectConventions.KeepPlanHierarchy(tasks, explicitIssues), repairs);
    }

    /// <summary>
    /// 自分の Project とリポジトリへの権限を調べる（要件 F-PRJ-06）。リポジトリは Write 以上でないと Issue を作れず、
    /// Project は編集の権限がないとフィールドを変えられない。
    /// </summary>
    public async Task<ProjectAccess> GetProjectAccessAsync(string projectId, CancellationToken ct = default)
    {
        var data = await _client.SendAsync(Queries.ProjectAccess, new JsonObject { ["id"] = projectId },
            GitHubJsonContext.Default.GraphQLResponseProjectNodeData, ct).ConfigureAwait(false);
        var dto = data.Node ?? throw new GitHubException("プロジェクトが見つかりません。");
        var repository = dto.Repositories?.Nodes?.FirstOrDefault();
        return new ProjectAccess(
            dto.ViewerCanUpdate ?? false,
            repository?.NameWithOwner,
            repository?.ViewerPermission is "ADMIN" or "MAINTAIN" or "WRITE");
    }

    /// <summary>アイテムを ID で取得する。削除されたり Project から外されたりしたアイテムは null。</summary>
    private async Task<ItemDto?> GetItemAsync(string itemId, CancellationToken ct)
    {
        try
        {
            var data = await _client.SendAsync(Queries.ItemById, new JsonObject { ["id"] = itemId },
                GitHubJsonContext.Default.GraphQLResponseItemNodeData, ct).ConfigureAwait(false);
            return data.Node;
        }
        catch (GitHubGraphQLException ex) when (ex.ErrorTypes.Contains("NOT_FOUND"))
        {
            return null;
        }
    }

    /// <summary>プロジェクトの名前を変える（要件 F-PRJ-01）。</summary>
    public Task RenameProjectAsync(string projectId, string title, CancellationToken ct = default) =>
        MutateAsync(Queries.UpdateProjectTitle, new JsonObject { ["id"] = projectId, ["title"] = title }, ct);

    /// <summary>プロジェクトをアーカイブする、または戻す（要件 F-PRJ-01）。</summary>
    public Task SetProjectClosedAsync(string projectId, bool closed, CancellationToken ct = default) =>
        MutateAsync(Queries.SetProjectClosed, new JsonObject { ["id"] = projectId, ["closed"] = closed }, ct);

    /// <summary>
    /// Project を作成し、管理対象の記号、リポジトリのリンク、フィールド構成を設定する。
    /// </summary>
    /// <param name="statuses">Status の選択肢（null ならアプリの標準）。</param>
    /// <param name="settings">README に書き込むプロジェクトの設定（null または空なら書き込まない）。</param>
    public async Task<string> CreateProjectAsync(string ownerId, string title, ProjectKind kind, string? repositoryId,
        IReadOnlyList<StatusTemplate>? statuses = null, ProjectSettings? settings = null, CancellationToken ct = default)
    {
        var data = await _client.SendAsync(Queries.CreateProject, new JsonObject { ["ownerId"] = ownerId, ["title"] = title },
            GitHubJsonContext.Default.GraphQLResponseCreateProjectData, ct).ConfigureAwait(false);
        var id = data.CreateProjectV2?.ProjectV2?.Id ?? throw new GitHubException("プロジェクトを作成できませんでした。");

        var marker = kind == ProjectKind.Inbox ? ProjectConventions.InboxMarker : ProjectConventions.ProjectMarker;
        await MutateAsync(Queries.UpdateProjectDescription, new JsonObject { ["id"] = id, ["description"] = marker }, ct).ConfigureAwait(false);

        if (repositoryId is not null)
        {
            await MutateAsync(Queries.LinkRepository, new JsonObject { ["projectId"] = id, ["repositoryId"] = repositoryId }, ct).ConfigureAwait(false);
        }

        await EnsureSchemaAsync(id, ct, initialStatuses: statuses ?? DefaultSettings.StandardStatuses).ConfigureAwait(false);
        if (settings is { IsEmpty: false })
        {
            await SaveProjectSettingsAsync(id, settings, ct).ConfigureAwait(false);
        }

        return id;
    }

    /// <summary>
    /// プロジェクトの設定を README の注記に保存する（要件 F-SET-02）。README のほかの本文は保つため、保存の直前に読み直す。
    /// </summary>
    public async Task SaveProjectSettingsAsync(string projectId, ProjectSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var data = await _client.SendAsync(Queries.ProjectReadme, new JsonObject { ["id"] = projectId },
            GitHubJsonContext.Default.GraphQLResponseProjectNodeData, ct).ConfigureAwait(false);
        var readme = ProjectReadme.Write(data.Node?.Readme, settings);
        await MutateAsync(Queries.UpdateProjectReadme, new JsonObject { ["id"] = projectId, ["readme"] = readme }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 番号のキーがまだ保存されていなければ保存する（要件 F-TSK-15）。保存の直前に README を読み直し、
    /// ほかのメンバーが先に保存していればそのキーに従う。保存してあるキー（自分が保存したものを含む）を返す。
    /// </summary>
    public async Task<string> SaveKeyIfMissingAsync(string projectId, string key, CancellationToken ct = default)
    {
        var data = await _client.SendAsync(Queries.ProjectReadme, new JsonObject { ["id"] = projectId },
            GitHubJsonContext.Default.GraphQLResponseProjectNodeData, ct).ConfigureAwait(false);
        var current = ProjectReadme.Read(data.Node?.Readme);
        if (current.Key is { } saved && saved != ProjectKey.InboxKey)
        {
            return saved;
        }

        var readme = ProjectReadme.Write(data.Node?.Readme, current with { Key = key });
        await MutateAsync(Queries.UpdateProjectReadme, new JsonObject { ["id"] = projectId, ["readme"] = readme }, ct).ConfigureAwait(false);
        return key;
    }

    /// <summary>不足しているフィールドを追加し、Status にカテゴリの記号を、Kind に「課題」の選択肢を補う。</summary>
    /// <param name="initialStatuses">Status の選択肢をこの並びに置き換える。作ったばかりの Project にだけ使う。</param>
    public async Task EnsureSchemaAsync(string projectId, CancellationToken ct = default, IReadOnlyList<StatusTemplate>? initialStatuses = null)
    {
        var data = await _client.SendAsync(Queries.ProjectSchema, new JsonObject { ["id"] = projectId },
            GitHubJsonContext.Default.GraphQLResponseProjectNodeData, ct).ConfigureAwait(false);
        var dto = data.Node ?? throw new GitHubException("プロジェクトが見つかりません。");

        foreach (var field in ProjectSchema.MissingFields(dto))
        {
            await MutateAsync(Queries.CreateField, new JsonObject
            {
                ["projectId"] = projectId,
                ["name"] = field.Name,
                ["dataType"] = field.DataType,
                ["options"] = field.Options?.DeepClone(),
            }, ct).ConfigureAwait(false);
        }

        var statusOptions = initialStatuses is not null
            ? ProjectSchema.InitialStatusOptions(dto, initialStatuses)
            : ProjectSchema.StatusOptionsNeedingMarkers(dto);
        if (statusOptions is { } options && ProjectMapper.StatusField(dto)?.Id is { } fieldId)
        {
            await MutateAsync(Queries.UpdateSingleSelectOptions, new JsonObject { ["fieldId"] = fieldId, ["options"] = options }, ct).ConfigureAwait(false);
        }

        if (ProjectSchema.KindOptionsNeedingIssue(dto) is { } kinds && ProjectSchema.KindField(dto)?.Id is { } kindFieldId)
        {
            await MutateAsync(Queries.UpdateSingleSelectOptions, new JsonObject { ["fieldId"] = kindFieldId, ["options"] = kinds }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// REST API に送る（GraphQL にない操作）。トークンと REST の形式を付けて送り、つながらなければ <see cref="GitHubUnavailableException"/> にする。
    /// 応答の状態の読み方は操作ごとに違うため呼ぶ側が決め、応答は呼ぶ側が破棄する。
    /// </summary>
    private async Task<HttpResponseMessage> SendRestAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        var token = _tokenProvider() ?? throw new GitHubAuthenticationException("サインインしていません。");
        using var request = new HttpRequestMessage(method, new Uri(_host.RestEndpoint, path));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, GitHubJsonContext.Default.JsonObject);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        try
        {
            return await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new GitHubUnavailableException("GitHub に接続できません。", ex);
        }
    }

    private Task MutateAsync(string query, JsonObject variables, CancellationToken ct) =>
        _client.SendAsync(query, variables, GitHubJsonContext.Default.GraphQLResponseMutationData, ct);

    private static (string Owner, string Name) Split(string nameWithOwner)
    {
        var i = nameWithOwner.IndexOf('/');
        return i > 0 ? (nameWithOwner[..i], nameWithOwner[(i + 1)..]) : throw new ArgumentException("owner/name の形式で指定してください。", nameof(nameWithOwner));
    }
}
