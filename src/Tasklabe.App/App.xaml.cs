using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Tasklabe.App.Controls;
using Tasklabe.App.Services;
using Tasklabe.App.ViewModels;
using Tasklabe.App.Views;
using Windows.ApplicationModel.Activation;

namespace Tasklabe.App;

public partial class App : Application
{
    /// <summary>Copilot キーから起動されるときの URI のスキーム（Package/AppxManifest.xml）。</summary>
    private const string CopilotScheme = "tasklabe-copilot";

    private MainWindow? _window;
    private QuickAddWindow? _quickAdd;
    private DispatcherQueue? _dispatcher;

    public App()
    {
        InitializeComponent();

        UnhandledException += (_, e) => AppLog.Error("UnhandledException", e.Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => AppLog.Error("UnobservedTaskException", e.Exception);
        Services.Sync.UnexpectedError += (_, ex) => AppLog.Error("Sync", ex);
        Services.Sync.Diagnostic += (_, message) => AppLog.Info($"Sync: {message}");
    }

    public static new App Current => (App)Application.Current;

    public AppServices Services { get; } = new();

    public MainWindow? MainWindow => _window;

    public ShellViewModel? Shell => _window?.ViewModel;

    /// <summary>テーマを読み取る根元の要素。本体がなければ、タスクの追加のウィンドウ。</summary>
    public FrameworkElement? ThemeRoot => (_window?.Content ?? _quickAdd?.Content) as FrameworkElement;

    /// <summary>アプリ全体のテーマ。Default は Windows の設定に従う。</summary>
    public ElementTheme Theme
    {
        get => Enum.TryParse<ElementTheme>(Services.CurrentSettings.Theme, out var t) ? t : ElementTheme.Default;
        set
        {
            Services.CurrentSettings.Theme = value.ToString();
            Services.SaveSettings();
            ApplyTheme();

            // コードで作った要素の色（ThemeResources）も新しいテーマで描き直す
            Shell?.NotifyDataChanged();
        }
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        // 動いているあいだの起動（Copilot キーなど）は、後から起動した Tasklabe から渡される（Program.cs）。
        // 渡された引数は渡し元のプロセスにあり、渡し元は渡し終えると終わるため、受けたその場で読み取る
        var instance = AppInstance.GetCurrent();
        instance.Activated += (_, e) =>
        {
            var (kind, state) = (e.Kind, CopilotKeyState(e));
            _dispatcher.TryEnqueue(() => Activate(kind, state));
        };
        var launched = instance.GetActivatedEventArgs();
        Activate(launched.Kind, CopilotKeyState(launched));
    }

    /// <summary>起動のされ方に応じて開く。Copilot キーからの起動でなければ本体を開く。</summary>
    /// <param name="copilotKey">Copilot キーからの起動なら、キーの状態。</param>
    private void Activate(ExtendedActivationKind kind, string? copilotKey)
    {
        AppLog.Info($"起動: {kind}{(copilotKey is null ? "" : $"（Copilot キー: {copilotKey}）")}");
        if (copilotKey is null)
        {
            ShowMain();
        }
        else
        {
            OnCopilotKey(copilotKey);
        }
    }

    /// <summary>
    /// Copilot キーに応える（要件 F-UI-MY-08）。押したときはタスクの追加だけを開き、長押ししたときは本体を開く。
    /// 長押しを離したときは何もしない（この起動でプロセスが始まったときは、そのまま終える）。
    /// </summary>
    private void OnCopilotKey(string state)
    {
        switch (state)
        {
            case CopilotKey.Tap:
                ShowQuickAdd();
                break;
            case CopilotKey.Down:
                ShowMain();
                break;
            default:
                if (_window is null && _quickAdd is null)
                {
                    Exit();
                }

                break;
        }
    }

    /// <summary>動いているあいだの Copilot キーを、ウィンドウへのメッセージで受け取る（技術設計書 2.3.3 節）。</summary>
    private void ListenCopilotKey(Window window) => CopilotKey.Listen(window, state =>
    {
        AppLog.Info($"Copilot キー: {state}");
        OnCopilotKey(state);
    });

    /// <summary>
    /// Copilot キーからの起動なら、キーの状態（Tap・Down・Up）を返す。Copilot キーは結果を受け取る形の URI の起動
    /// （ProtocolForResults）で起動する。
    /// </summary>
    private static string? CopilotKeyState(AppActivationArguments args)
    {
        if (args.Kind is not (ExtendedActivationKind.Protocol or ExtendedActivationKind.ProtocolForResults)
            || args.Data is not IProtocolActivatedEventArgs { Uri: { } uri }
            || !string.Equals(uri.Scheme, CopilotScheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new Windows.Foundation.WwwFormUrlDecoder(uri.Query).FirstOrDefault(e => e.Name == "state")?.Value;
    }

    /// <summary>本体を開く。開いていれば前面に出す。</summary>
    public MainWindow ShowMain()
    {
        if (_window is { } window)
        {
            if (window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            {
                presenter.Restore();
            }

            window.Activate();
            return window;
        }

        window = _window = new MainWindow(Services);
        window.Closed += (_, _) => _window = null;
        ListenCopilotKey(window);
        ApplyTheme();
        window.Activate();
        window.Start();
        return window;
    }

    /// <summary>
    /// タスクの追加だけを開く（要件 F-UI-MY-08）。サインインしていなければ、本体のサインインを開く。
    /// </summary>
    private void ShowQuickAdd()
    {
        if (_quickAdd is { } open)
        {
            open.Toggle();
            return;
        }

        if (_window is null ? !Services.TryRestoreSession() : !Services.HasToken)
        {
            ShowMain();
            return;
        }

        var window = _quickAdd = new QuickAddWindow();
        window.Closed += (_, _) => _quickAdd = null;
        ListenCopilotKey(window);
        window.Activate();
    }

    /// <summary>
    /// 本体を開いていないときに、送信キューを送り終えるまで待つ（タスクの追加だけを開いて終えるとき）。
    /// 送れなかった分は送信キューに残り、次に起動したときに送る。
    /// </summary>
    public async Task FlushIfAloneAsync()
    {
        if (_window is not null)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            // 作成の直後に送信が始まっていれば、PushAsync はすぐに戻り、その送信が続けて送る。終わるまで待つ
            await Services.Sync.PushAsync(timeout.Token);
            while (Services.Sync.Status.State == Tasklabe.Core.Abstractions.SyncState.Syncing)
            {
                await Task.Delay(100, timeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("タスクの追加のあと、送信を待ちきれずに終えます。残りは次に起動したときに送ります");
        }
    }

    private void ApplyTheme()
    {
        if (_window?.Content is not FrameworkElement root)
        {
            return;
        }

        var theme = Theme;
        root.RequestedTheme = theme;

        // 拡張タイトルバーのキャプションボタン（最小化・閉じる）の配色も揃える
        _window.AppWindow.TitleBar.PreferredTheme = TitleBarThemeOf(theme);
    }

    /// <summary>テーマに合うキャプションボタンの配色。</summary>
    internal static TitleBarTheme TitleBarThemeOf(ElementTheme theme) => theme switch
    {
        ElementTheme.Light => TitleBarTheme.Light,
        ElementTheme.Dark => TitleBarTheme.Dark,
        _ => TitleBarTheme.UseDefaultAppMode,
    };
}
