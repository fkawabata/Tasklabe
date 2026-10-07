using System.Net.Http.Headers;
using System.Reflection;
using Tasklabe.Data;
using Tasklabe.GitHub;
using Tasklabe.Core.Keyboard;

namespace Tasklabe.App.Services;

/// <summary>アプリ全体で共有するサービスの組み立て。</summary>
public sealed class AppServices
{
    private string? _token;

    /// <summary>アプリのバージョン（Directory.Build.props の Version）。ビルド時に付くコミットの識別子は除く。</summary>
    public static string Version { get; } =
        typeof(AppServices).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public AppServices()
    {
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tasklabe");

        Host = GitHubHost.Default;
        Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        Http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Tasklabe", Version));

        Tokens = new TokenStore(Host.Name);
        Settings = new SettingsStore(Path.Combine(dataDir, "settings.json"));
        CurrentSettings = Settings.Load();

        Api = new GitHubApi(Http, Host, () => _token);
        Database = TasklabeDatabase.OpenFile(Path.Combine(dataDir, "cache.db"));
        Store = new SqliteTaskStore(Database);
        Sync = new SyncEngine(Api, Store, TimeProvider.System);
        Workspace = new WorkspaceService(Api, Store, TimeProvider.System);
        Edits = new TaskEditService(Store, Sync, TimeProvider.System, () => CurrentSettings.UserLogin, () => AppClock.Today);

        // GitHub 上で子のステータスが変わったときも、取り込んだあとに親タスクのステータスを子に合わせる
        Sync.DataChanged += async (_, _) => await Edits.ReconcileAfterSyncAsync();
        Keymap = new Keymap(CurrentSettings.Shortcuts);
    }

    /// <summary>キーの割り当て（要件 F-KEY-04）。</summary>
    public Keymap Keymap { get; private set; }

    /// <summary>キーの割り当てが変わった。</summary>
    public event EventHandler? KeymapChanged;

    /// <summary>キーの割り当てを変え、既定から変えた分を設定に保存する。</summary>
    public void SaveKeymap(Keymap keymap)
    {
        ArgumentNullException.ThrowIfNull(keymap);

        Keymap = keymap;
        CurrentSettings.Shortcuts = keymap.ToOverrides();
        SaveSettings();
        KeymapChanged?.Invoke(this, EventArgs.Empty);
    }

    public GitHubHost Host { get; }

    public HttpClient Http { get; }

    public TokenStore Tokens { get; }

    public SettingsStore Settings { get; }

    public AppSettings CurrentSettings { get; }

    public GitHubApi Api { get; }

    public TasklabeDatabase Database { get; }

    public SqliteTaskStore Store { get; }

    public SyncEngine Sync { get; }

    public WorkspaceService Workspace { get; }

    public TaskEditService Edits { get; }

    public bool HasToken => _token is not null;

    public GitHubUser? User => CurrentSettings.UserId is { } id && CurrentSettings.UserLogin is { } login
        ? new GitHubUser(id, login, CurrentSettings.UserName, null)
        : null;

    public RepositoryInfo? PersonalRepository =>
        CurrentSettings.PersonalRepositoryId is { } id && CurrentSettings.PersonalRepositoryName is { } name
            ? new RepositoryInfo(id, name)
            : null;

    /// <summary>保存済みのトークンを読み込む。セットアップが済んでいればサインイン済みとみなす。</summary>
    public bool TryRestoreSession()
    {
        _token = Tokens.Load();
        return _token is not null && User is not null && PersonalRepository is not null;
    }

    public void SetToken(string token) => _token = token;

    /// <summary>
    /// 無効になったトークンだけを破棄する。キャッシュ、未送信の変更、設定は残し、
    /// 同じアカウントで再サインインすればそのまま続きから使えるようにする。
    /// </summary>
    public void InvalidateToken()
    {
        _token = null;
        Tokens.Delete();
    }

    public void SaveSettings() => Settings.Save(CurrentSettings);

    public async Task SignOutAsync()
    {
        _token = null;
        Tokens.Delete();
        CurrentSettings.UserId = null;
        CurrentSettings.UserLogin = null;
        CurrentSettings.UserName = null;
        CurrentSettings.PersonalRepositoryId = null;
        CurrentSettings.PersonalRepositoryName = null;
        SaveSettings();
        await Store.ClearAsync();
    }
}
