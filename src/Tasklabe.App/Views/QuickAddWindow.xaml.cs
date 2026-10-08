using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Tasklabe.App.Controls;
using Tasklabe.App.Services;
using Tasklabe.App.Views.Dialogs;
using Windows.Graphics;
using Windows.System;

namespace Tasklabe.App.Views;

/// <summary>
/// タスクの追加だけのウィンドウ（要件 F-UI-MY-08、UI デザイン設計書 3.5.2 節）。Copilot キーを押したときに、
/// 本体を開かずにマウスのある画面の中央へ開く。追加先は既定の個人プロジェクトとし、入力の形は追加のダイアログと同じにする。
/// 追加するか、破棄して閉じると消える。
/// </summary>
public sealed partial class QuickAddWindow : Window
{
    /// <summary>ウィンドウの中の大きさ（ピッカーが中に収まる高さをとる）。</summary>
    private const int ClientWidth = 540;
    private const int ClientHeight = 460;

    /// <summary>閉じてよいことを確かめ終えた（タイトルバーの ✕ で閉じるときに、もう確かめない）。</summary>
    private bool _closing;
    private bool _busy;

    public QuickAddWindow()
    {
        InitializeComponent();
        Title = "タスクを追加 - Tasklabe";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Tasklabe.ico"));
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleArea);
        Root.RequestedTheme = App.Current.Theme;
        AppWindow.TitleBar.PreferredTheme = App.TitleBarThemeOf(App.Current.Theme);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        PlaceOnPointerScreen();
        LastInput.Track(Root);
        Root.KeyDown += OnRootKeyDown;
        AppWindow.Closing += OnClosing;

        Composer.CanCreateChanged += (_, _) => AddButton.IsEnabled = Composer.CanCreate;
        Composer.SubmitRequested += async (_, _) => await SubmitAsync();
        Composer.DetailRequested += async (_, _) => await ContinueInDetailAsync();
        Root.Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var projects = await NewTaskDialog.ProjectsAsync();
        if (projects.Count == 0)
        {
            // 追加先がない（セットアップの途中など）ときは、本体で続ける
            App.Current.ShowMain();
            CloseNow();
            return;
        }

        Composer.Load(projects, new NewTaskContext());
        Composer.FocusTitle();
    }

    /// <summary>マウスのある画面の、中央のやや上に置く。</summary>
    private void PlaceOnPointerScreen()
    {
        GetCursorPos(out var cursor);
        var area = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Primary).WorkArea;
        GetDpiForMonitor(MonitorFromPoint(cursor, 2), 0, out uint dpi, out _);
        double scale = dpi / 96.0;
        int width = (int)(ClientWidth * scale);
        int height = (int)(ClientHeight * scale);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + Math.Max(0, (area.Height - height) / 3), width, height));
        AppWindow.ResizeClient(new SizeInt32(width, height));
    }

    private async void OnAdd(object sender, RoutedEventArgs e) => await SubmitAsync();

    private async void OnCancel(object sender, RoutedEventArgs e) => await CancelAsync();

    private async void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // ピッカーやダイアログが受けなかった Esc で閉じる
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            await CancelAsync();
        }
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_closing)
        {
            args.Cancel = true;
            _ = CancelAsync();
        }
    }

    /// <summary>追加して閉じる。本体を開いていなければ、GitHub へ送り終えるまで隠して待ってから終える。</summary>
    private async Task SubmitAsync()
    {
        if (_busy || !Composer.CanCreate)
        {
            return;
        }

        _busy = true;
        try
        {
            await Composer.Draft.CreateAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("QuickAdd", ex);
            await AppDialog.Info(Content.XamlRoot, "タスクを追加できませんでした", AppDialog.HintText(ex.Message)).ShowAsync();
            _busy = false;
            return;
        }

        _closing = true;
        AppWindow.Hide();
        await App.Current.FlushIfAloneAsync();
        Close();
    }

    /// <summary>入力があれば破棄してよいかを確かめてから閉じる（UX 規約 UX-11、UX-14）。</summary>
    private async Task CancelAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            if (Composer.Draft.HasContent
                && await NewTaskDialog.ConfirmDiscard(Content.XamlRoot, Composer.Draft).ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }
        finally
        {
            _busy = false;
        }

        CloseNow();
    }

    /// <summary>「詳細を入力…」: 本体を開き、入力を引き継いで詳細パネルで作成を続ける（UX-09）。</summary>
    private async Task ContinueInDetailAsync()
    {
        if (_busy)
        {
            return;
        }

        // 先に本体を開く（このウィンドウだけのときに閉じると、アプリが終わるため）
        var draft = Composer.Draft;
        var main = App.Current.ShowMain();
        CloseNow();
        await main.ShellShown;
        App.Current.Shell?.StartCreating(draft);
    }

    private void CloseNow()
    {
        _closing = true;
        Close();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(Point point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
}
