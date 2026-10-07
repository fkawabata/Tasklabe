using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Tasklabe.App.Services;
using Tasklabe.App.ViewModels;

namespace Tasklabe.App;

public partial class App : Application
{
    private MainWindow? _window;

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

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow(Services);
        ApplyTheme();
        _window.Activate();
        _window.Start();
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
        _window.AppWindow.TitleBar.PreferredTheme = theme switch
        {
            ElementTheme.Light => TitleBarTheme.Light,
            ElementTheme.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
    }
}
