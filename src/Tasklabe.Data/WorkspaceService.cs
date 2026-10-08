using Tasklabe.Core.Domain;
using Tasklabe.Core.Milestones;
using Tasklabe.Core.Settings;
using Tasklabe.GitHub;

namespace Tasklabe.Data;

/// <summary>
/// 新しいプロジェクトに最初から持たせるもの（要件 F-SET-04）。既定、または引き継ぎ元のプロジェクトから作る。
/// </summary>
/// <param name="Statuses">Status の選択肢。</param>
/// <param name="Settings">README に書き込む設定（空なら既定に従う）。</param>
public sealed record ProjectSetup(IReadOnlyList<StatusTemplate> Statuses, ProjectSettings Settings);

/// <summary>個人の作業環境（個人用リポジトリ、Inbox）と、プロジェクトの作成・設定。</summary>
public sealed class WorkspaceService(GitHubApi api, SqliteTaskStore store, TimeProvider time)
{
    /// <summary>
    /// 初回セットアップ（要件 F-SETUP-01、02）。既にある場合はそれを使う。
    /// </summary>
    public async Task<RepositoryInfo> EnsurePersonalWorkspaceAsync(
        GitHubUser user, string repositoryName, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report("個人用リポジトリを準備しています");
        var repository = await api.FindRepositoryAsync(user.Login, repositoryName, ct).ConfigureAwait(false)
            ?? await api.CreatePrivateRepositoryAsync(repositoryName, "Tasklabe の個人タスク", ct).ConfigureAwait(false);

        progress?.Report("Inbox を準備しています");
        var projects = await api.ListViewerProjectsAsync(ct).ConfigureAwait(false);
        if (!projects.Any(p => p.IsInbox))
        {
            await api.CreateProjectAsync(user.Id, ProjectConventions.InboxTitle, ProjectKind.Inbox, repository.Id, ct: ct).ConfigureAwait(false);
        }

        return repository;
    }

    /// <summary>
    /// チームプロジェクトを作成し、キャッシュへ取り込む（要件 F-PRJ-02）。
    /// リポジトリ名を指定した場合は、Organization にリポジトリを新規作成して使う。
    /// </summary>
    public async Task<Project> CreateTeamProjectAsync(
        Organization organization, OrganizationRepository? repository, string? newRepositoryName, string title, ProjectSetup? setup = null,
        CancellationToken ct = default)
    {
        var repositoryId = repository?.Id
            ?? (await api.CreateOrganizationRepositoryAsync(organization.Id, newRepositoryName!, $"Tasklabe のチームプロジェクト「{title}」", ct).ConfigureAwait(false)).Id;

        var id = await api.CreateProjectAsync(organization.Id, title, ProjectKind.Team, repositoryId, setup?.Statuses, setup?.Settings, ct).ConfigureAwait(false);
        var snapshot = await api.GetProjectAsync(id, ct).ConfigureAwait(false);
        await store.ReplaceProjectAsync(snapshot, time.GetUtcNow(), ct).ConfigureAwait(false);
        await RegisterNewKeyAsync(snapshot.Project, setup, ct).ConfigureAwait(false);

        if (snapshot.Project.RepositoryNameWithOwner is { } repo)
        {
            await store.SaveAssignableUsersAsync(repo, await api.GetAssignableUsersAsync(repo, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        }

        return snapshot.Project;
    }

    /// <summary>プロジェクトの名前を変え、キャッシュへ取り込む（要件 F-PRJ-01）。</summary>
    public async Task RenameProjectAsync(Project project, string title, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        await api.RenameProjectAsync(project.Id, title, ct).ConfigureAwait(false);
        await RefreshAsync(project.Id, ct).ConfigureAwait(false);
    }

    /// <summary>プロジェクトをアーカイブする、または戻す（要件 F-PRJ-01）。</summary>
    public async Task SetProjectClosedAsync(Project project, bool closed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        await api.SetProjectClosedAsync(project.Id, closed, ct).ConfigureAwait(false);
        await RefreshAsync(project.Id, ct).ConfigureAwait(false);
    }

    /// <summary>プロジェクトの設定（既定の上書きと番号のキー）を GitHub の Project に保存し、キャッシュへ取り込む（要件 F-SET-02、F-TSK-14）。</summary>
    public async Task SaveProjectSettingsAsync(Project project, ProjectSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        await api.SaveProjectSettingsAsync(project.Id, settings, ct).ConfigureAwait(false);
        await RefreshAsync(project.Id, ct).ConfigureAwait(false);
    }

    /// <summary>番号のキーをリポジトリの自動リンクに登録する（要件 F-TSK-16）。リポジトリがなければ何もせず null を返す。</summary>
    public async Task<AutolinkResult?> RegisterKeyAsync(string? repositoryNameWithOwner, string key, CancellationToken ct = default) =>
        repositoryNameWithOwner is null ? null : await api.EnsureAutolinkAsync(repositoryNameWithOwner, key, ct).ConfigureAwait(false);

    /// <summary>
    /// リポジトリの自動リンクで、このリポジトリの Issue 以外へつなぐキー（番号のキーにすると重なるもの）。
    /// リポジトリがない、または管理者でなく一覧を読めないときは空とする。
    /// </summary>
    public async Task<IReadOnlyList<string>> ForeignAutolinkKeysAsync(string? repositoryNameWithOwner, CancellationToken ct = default) =>
        repositoryNameWithOwner is null ? [] : await api.GetForeignAutolinkKeysAsync(repositoryNameWithOwner, ct).ConfigureAwait(false) ?? [];

    private readonly HashSet<string> _keyAttempts = new(StringComparer.Ordinal);

    /// <summary>
    /// キーをまだ保存していないプロジェクトに、提案したキーを保存して固定する（要件 F-TSK-15）。
    /// 保存できたプロジェクトでは自動リンクにも登録する。Project を編集する権限がないなど保存できないプロジェクトは、
    /// このアプリの起動中は試し直さない。保存したプロジェクトがあれば true を返す。
    /// </summary>
    public async Task<bool> FixMissingKeysAsync(CancellationToken ct = default)
    {
        var projects = await store.GetProjectsAsync(ct).ConfigureAwait(false);
        var keys = ProjectKey.Resolve(projects).ToDictionary(StringComparer.Ordinal);
        bool saved = false;
        foreach (var p in projects.Where(p => p.Kind != ProjectKind.Inbox && ProjectKey.SavedKey(p) is null).OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            lock (_keyAttempts)
            {
                if (!_keyAttempts.Add(p.Id))
                {
                    continue;
                }
            }

            try
            {
                var key = keys[p.Id];
                var linked = await ForeignAutolinkKeysAsync(p.RepositoryNameWithOwner, ct).ConfigureAwait(false);
                if (linked.Contains(key, StringComparer.Ordinal))
                {
                    key = ProjectKey.Suggest(p.Title, p.RepositoryNameWithOwner, keys.Where(k => k.Key != p.Id).Select(k => k.Value).Concat(linked));
                }

                var fixedKey = await api.SaveKeyIfMissingAsync(p.Id, key, ct).ConfigureAwait(false);
                keys[p.Id] = fixedKey;
                await RefreshAsync(p.Id, ct).ConfigureAwait(false);
                saved = true;
                if (fixedKey == key)
                {
                    await RegisterKeyAsync(p.RepositoryNameWithOwner, key, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is GitHubAuthenticationException or GitHubUnavailableException || ct.IsCancellationRequested)
            {
                // つながらないときは、次に読み直したときに試し直す
                lock (_keyAttempts)
                {
                    _keyAttempts.Remove(p.Id);
                }

                break;
            }
            catch (GitHubException)
            {
                // 編集する権限がないプロジェクトは、編集できる人が開いたときに固定する
            }
        }

        return saved;
    }

    /// <summary>
    /// 作ったプロジェクトのキーを自動リンクに登録する。登録できなくてもプロジェクトは使えるため、作成は失敗させない
    /// （プロジェクトの設定から登録し直せる）。
    /// </summary>
    private async Task RegisterNewKeyAsync(Project project, ProjectSetup? setup, CancellationToken ct)
    {
        if (setup?.Settings.Key is not { } key)
        {
            return;
        }

        try
        {
            await RegisterKeyAsync(project.RepositoryNameWithOwner, key, ct).ConfigureAwait(false);
        }
        catch (GitHubException) when (!ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Status の選択肢の名前・カテゴリ・並びを変え、キャッシュへ取り込む（要件 F-SET-03）。
    /// タスクが使っている選択肢は外せない（外すと、そのタスクのステータスが GitHub 上で消えるため）。
    /// </summary>
    public async Task UpdateStatusOptionsAsync(Project project, IReadOnlyList<StatusOptionEdit> options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(options);

        var templates = options.Select(o => new StatusTemplate(o.Name, o.Color, o.Category)).ToList();
        if (StatusTemplates.Problem(templates) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        var kept = options.Where(o => o.Id is not null).Select(o => o.Id!).ToHashSet(StringComparer.Ordinal);
        var used = await StatusUsageAsync(project.Id, ct).ConfigureAwait(false);
        if (project.StatusOptions.FirstOrDefault(o => !kept.Contains(o.Id) && used.GetValueOrDefault(o.Id) > 0) is { } removed)
        {
            throw new InvalidOperationException($"「{removed.Name}」のタスクが {used[removed.Id]} 件あるため、外せません。先に別のステータスへ移してください。");
        }

        await api.UpdateStatusOptionsAsync(project.Id, options, ct).ConfigureAwait(false);
        await RefreshAsync(project.Id, ct).ConfigureAwait(false);
    }

    /// <summary>ステータスの選択肢ごとの、タスクの件数。</summary>
    public async Task<IReadOnlyDictionary<string, int>> StatusUsageAsync(string projectId, CancellationToken ct = default)
    {
        var tasks = await store.GetTasksAsync(projectId, ct).ConfigureAwait(false);
        return tasks.Where(t => t.StatusOptionId is not null)
            .GroupBy(t => t.StatusOptionId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
    }

    // ---------------------------------------------------------------- マイルストーン（要件 F-MS-01〜03）

    /// <summary>
    /// マイルストーンを作り、キャッシュへ取り込む。同じリポジトリを使う他のプロジェクトに出ないよう、説明欄にこのプロジェクトの記号を付ける。
    /// タスクの所属の付け替えは呼び出し側が行う。
    /// </summary>
    public async Task<Milestone> CreateMilestoneAsync(Project project, string title, DateOnly? due, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var created = await api.CreateMilestoneAsync(RepositoryOf(project), title.Trim(), due, MilestonePlan.WithOwner(null, project.Id), ct).ConfigureAwait(false);
        await RefreshAsync(project.Id, ct).ConfigureAwait(false);
        return created;
    }

    /// <summary>マイルストーンの名前と期日を変え、キャッシュへ取り込む。記号のないもの（GitHub 上で作ったもの）には、このプロジェクトの記号を付ける。</summary>
    public async Task UpdateMilestoneAsync(Project project, Milestone milestone, string title, DateOnly? due, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(milestone);
        var description = MilestonePlan.NeedsOwner(milestone, project.Id) ? MilestonePlan.WithOwner(milestone.Description, project.Id) : null;
        await api.UpdateMilestoneAsync(RepositoryOf(project), milestone.Number, title.Trim(), due, description, ct).ConfigureAwait(false);
        await RefreshAsync(project.Id, ct).ConfigureAwait(false);
    }

    /// <summary>マイルストーンを消し、キャッシュへ取り込む。タスクは残る。</summary>
    public async Task DeleteMilestoneAsync(Project project, Milestone milestone, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(milestone);
        await api.DeleteMilestoneAsync(RepositoryOf(project), milestone.Number, ct).ConfigureAwait(false);
        await RefreshAsync(project.Id, ct).ConfigureAwait(false);
    }

    private static string RepositoryOf(Project project) =>
        project?.RepositoryNameWithOwner ?? throw new InvalidOperationException("プロジェクトにリポジトリがつながっていないため、マイルストーンを扱えません。");

    private async Task RefreshAsync(string projectId, CancellationToken ct)
    {
        var snapshot = await api.GetProjectAsync(projectId, ct).ConfigureAwait(false);
        await store.ReplaceProjectAsync(snapshot, time.GetUtcNow(), ct).ConfigureAwait(false);
    }

    /// <summary>個人プロジェクトを作成し、キャッシュへ取り込む（要件 F-PRJ-02）。</summary>
    public async Task<Project> CreatePersonalProjectAsync(GitHubUser user, RepositoryInfo repository, string title, ProjectSetup? setup = null,
        CancellationToken ct = default)
    {
        var id = await api.CreateProjectAsync(user.Id, title, ProjectKind.Personal, repository.Id, setup?.Statuses, setup?.Settings, ct).ConfigureAwait(false);
        var snapshot = await api.GetProjectAsync(id, ct).ConfigureAwait(false);
        await store.ReplaceProjectAsync(snapshot, time.GetUtcNow(), ct).ConfigureAwait(false);
        await RegisterNewKeyAsync(snapshot.Project, setup, ct).ConfigureAwait(false);
        return snapshot.Project;
    }
}
