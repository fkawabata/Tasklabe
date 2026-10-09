using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Tasklabe.Animation;
using Tasklabe.App.Controls;
using Tasklabe.App.Services;
using Tasklabe.App.Views.Dialogs;
using Windows.Foundation;
using Windows.Graphics;
using VirtualKey = Windows.System.VirtualKey;

namespace Tasklabe.App.Views;

/// <summary>
/// Copilot キーからの追加（要件 F-UI-MY-08、UI デザイン設計書 3.5.2 節）。タイトルバーのない板を、透明なウィンドウに浮かべる。
/// ピッカーは板の外へはみ出して開く。ウィンドウはピッカーがはみ出す分の大きさをとり、押せる範囲（ウィンドウの領域）は
/// 板と影、開いているピッカーの分だけに絞る。
/// </summary>
public sealed partial class QuickAddWindow : Window
{
    /// <summary>板の幅。</summary>
    private const double PanelWidth = 560;

    /// <summary>ウィンドウの端から板までの余白（影を描く分）。下は影が下へずれる分だけ広くとる。</summary>
    private const double SideInset = 32;
    private const double TopInset = 24;
    private const double BottomInset = 48;

    /// <summary>下端の帯の高さ。</summary>
    private const double FooterHeight = 46;

    /// <summary>ウィンドウの高さの上限（板の下にピッカーがはみ出して開く分）。</summary>
    private const double MaxWindowHeight = 760;

    /// <summary>追加したことを見せてから消すまで（暫定。使って決める）。</summary>
    private static readonly TimeSpan AddedHold = TimeSpan.FromMilliseconds(600);

    private static readonly TimeSpan RevealDuration = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ConcealDuration = TimeSpan.FromMilliseconds(160);

    private readonly DispatcherQueueTimer _regionTimer;
    private readonly DispatcherQueueTimer _addedTimer;
    private CompositionRoundedRectangleGeometry? _geometry;
    private CompositionColorBrush? _fill;
    private CompositionColorBrush? _stroke;
    private ShapeVisual? _shapes;
    private CompositionVisualSurface? _image;
    private SpriteVisual? _shadowSprite;
    private DropShadow? _shadow;

    /// <summary>板の高さ（ばねの行き先）。中身の高さに合わせる。</summary>
    private double _height;

    /// <summary>開いているピッカーの下端（板の上辺から）。開いていなければ null。</summary>
    private double? _pickerBottom;

    /// <summary>ウィンドウの領域に入れている高さ（板の上辺から）。</summary>
    private double _regionHeight;

    private bool _closing;
    private bool _busy;
    private bool _confirming;
    private PointInt32? _dragStart;
    private PointInt32 _windowStart;

    public QuickAddWindow()
    {
        InitializeComponent();
        Title = "タスクを追加 - Tasklabe";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Tasklabe.ico"));
        Root.RequestedTheme = App.Current.Theme;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = false;
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(false, false);
        }

        TransparentBackdrop.Prepare(this);
        Panel.Margin = new Thickness(SideInset, TopInset, 0, 0);
        Composer.UseCompactLayout(PanelWidth - 40);
        PlaceOnPointerScreen();

        _regionTimer = DispatcherQueue.CreateTimer();
        _regionTimer.IsRepeating = false;
        _regionTimer.Tick += (_, _) => ApplyRegion(Math.Max(_height, _pickerBottom ?? 0));
        _addedTimer = DispatcherQueue.CreateTimer();
        _addedTimer.IsRepeating = false;
        _addedTimer.Interval = AddedHold;
        _addedTimer.Tick += (_, _) => Vanish();

        LastInput.Track(Root);
        Root.KeyDown += OnRootKeyDown;
        Panel.PointerPressed += OnPanelPointerPressed;
        Panel.PointerMoved += OnPanelPointerMoved;
        Panel.PointerReleased += (_, e) => EndDrag(e);
        Panel.PointerCaptureLost += (_, _) => _dragStart = null;
        Activated += OnActivated;
        AppWindow.Closing += OnClosing;
        Root.ActualThemeChanged += (_, _) => ApplyColors();

        Composer.CanCreateChanged += (_, _) => UpdateFooter();
        Composer.SubmitRequested += async (_, _) => await SubmitAsync();
        Composer.DetailRequested += async (_, _) => await ContinueInDetailAsync();
        Composer.SizeChanged += (_, _) => Resize();
        MorphPopup.Placed += OnPickerPlaced;
        MorphPopup.Closing += OnPickerClosing;
        Closed += (_, _) =>
        {
            MorphPopup.Placed -= OnPickerPlaced;
            MorphPopup.Closing -= OnPickerClosing;
        };
        Root.Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        BuildSurface();
        var projects = await NewTaskDialog.ProjectsAsync();
        if (projects.Count == 0)
        {
            // 追加先がない（セットアップの途中など）ときは、本体で続ける
            App.Current.ShowMain();
            CloseNow();
            return;
        }

        Composer.Load(projects, new NewTaskContext());
        UpdateFooter();
        Composer.FocusTitle();
        Motion.Reveal(Panel, new Vector3(0, -8, 0), RevealDuration);
    }

    /// <summary>
    /// もう一度 Copilot キーを押した（UI デザイン設計書 3.5.2 節）。入力がなければ閉じ、あればタスク名へ戻る。
    /// </summary>
    public void Toggle()
    {
        if (_busy)
        {
            return;
        }

        if (!Composer.Draft.HasContent && !_confirming)
        {
            Vanish();
            return;
        }

        Activate();
        ShowConfirm(false);
        Composer.FocusTitle();
    }

    // ---------------------------------------------------------------- 置き場所と大きさ

    /// <summary>マウスのある画面の、左右の中央、上から 1/4 の高さに板を置く。ウィンドウは板の下へピッカーの分だけ伸ばす。</summary>
    private void PlaceOnPointerScreen()
    {
        GetCursorPos(out var cursor);
        var area = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Primary).WorkArea;
        GetDpiForMonitor(MonitorFromPoint(cursor, 2), 0, out uint dpi, out _);
        double scale = dpi / 96.0;
        int width = (int)Math.Ceiling((PanelWidth + SideInset * 2) * scale);
        int top = area.Y + area.Height / 4 - (int)(TopInset * scale);
        int height = Math.Min((int)(MaxWindowHeight * scale), area.Y + area.Height - top);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, top, width, height));
    }

    /// <summary>板の高さを中身（説明の行数）に合わせる。ピッカーは板の外へはみ出して開くため、板は伸ばさない。</summary>
    private void Resize()
    {
        double target = Composer.Margin.Top + Composer.ActualHeight + 18 + FooterHeight;
        if (Math.Abs(target - _height) < 0.5)
        {
            return;
        }

        bool first = _height == 0;
        double distance = Math.Abs(target - _height);
        var spring = target > _height ? Motion.Grow.ForDistance(distance) : Motion.Shrink.Landing(distance);
        _height = target;
        Panel.Height = target;
        if (_geometry is not null)
        {
            Motion.SpringTo(_geometry, "Size", new Vector2((float)PanelWidth, (float)target), spring, animate: !first);
        }

        Motion.SpringTo(Motion.VisualOf(Footer), "Translation", new Vector3(0, (float)(target - FooterHeight), 0), spring,
            animate: !first);
        UpdateRegion(spring);
    }

    /// <summary>
    /// ウィンドウの領域（押せる範囲と描く範囲）を、板と影、開いているピッカーの分に絞る。透明なところは後ろのウィンドウを押せる。
    /// 広げるときはすぐ広げ、狭めるときは縮み終えてから狭める（縮んでいる途中の板やピッカーが切れないように）。
    /// </summary>
    private void UpdateRegion(Spring shrink)
    {
        double needed = Math.Max(_height, _pickerBottom ?? 0);
        if (needed >= _regionHeight)
        {
            _regionTimer.Stop();
            ApplyRegion(needed);
        }
        else
        {
            _regionTimer.Interval = TimeSpan.FromSeconds(shrink.VisualDuration);
            _regionTimer.Start();
        }
    }

    private void ApplyRegion(double height)
    {
        _regionHeight = height;
        double scale = Root.XamlRoot?.RasterizationScale ?? 1;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        int width = (int)Math.Ceiling((PanelWidth + SideInset * 2) * scale);
        int bottom = (int)Math.Ceiling((TopInset + height + BottomInset) * scale);
        SetWindowRgn(hwnd, CreateRectRgn(0, 0, width, bottom), true);
    }

    private void OnPickerPlaced(object? sender, Rect rect)
    {
        if (!ReferenceEquals(sender, Root.XamlRoot))
        {
            return;
        }

        _pickerBottom = rect.Bottom - TopInset;
        UpdateRegion(Motion.Shrink);
    }

    private void OnPickerClosing(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, Root.XamlRoot))
        {
            return;
        }

        _pickerBottom = null;
        UpdateRegion(Motion.Shrink);
    }

    // ---------------------------------------------------------------- 板の面と影

    /// <summary>板の面と影を作る。説明の行数で板の高さが変わるときは、面の図形の大きさをばねで動かす。</summary>
    private void BuildSurface()
    {
        var compositor = ElementCompositionPreview.GetElementVisual(SurfaceHost).Compositor;
        _geometry = compositor.CreateRoundedRectangleGeometry();
        _geometry.CornerRadius = new Vector2(12);

        // 中身の大きさが先に決まっていれば（作る前に Resize が走ったとき）、その高さを当てる
        _geometry.Size = new Vector2((float)PanelWidth, (float)_height);
        _fill = compositor.CreateColorBrush();
        var face = compositor.CreateSpriteShape(_geometry);
        face.FillBrush = _fill;

        // 枠は面の内側に 1 px で描く。図形の線は輪郭を挟んで内外に半分ずつ描かれるため、0.5 px 内側へ縮めた図形に引く
        var inset = compositor.CreateRoundedRectangleGeometry();
        foreach (var (property, expression) in new[]
        {
            ("Offset", "Vector2(0.5, 0.5)"),
            ("Size", "Max(g.Size - Vector2(1, 1), Vector2(0, 0))"),
            ("CornerRadius", "Max(g.CornerRadius - Vector2(0.5, 0.5), Vector2(0, 0))"),
        })
        {
            var follow = compositor.CreateExpressionAnimation(expression);
            follow.SetReferenceParameter("g", _geometry);
            inset.StartAnimation(property, follow);
        }

        var edge = compositor.CreateSpriteShape(inset);
        _stroke = compositor.CreateColorBrush();
        edge.StrokeBrush = _stroke;
        edge.StrokeThickness = 1;

        var size = new Vector2((float)PanelWidth, (float)MaxWindowHeight);
        _shapes = compositor.CreateShapeVisual();
        _shapes.Size = size;
        _shapes.Shapes.Add(face);
        _shapes.Shapes.Add(edge);
        _image = compositor.CreateVisualSurface();
        _image.SourceVisual = _shapes;
        _image.SourceSize = size;
        _shadow = compositor.CreateDropShadow();
        _shadow.BlurRadius = 32;
        _shadow.Offset = new Vector3(0, 8, 0);
        _shadow.SourcePolicy = CompositionDropShadowSourcePolicy.InheritFromVisualContent;
        _shadowSprite = compositor.CreateSpriteVisual();
        _shadowSprite.Size = size;
        _shadowSprite.Brush = compositor.CreateSurfaceBrush(_image);
        _shadowSprite.Shadow = _shadow;

        var container = compositor.CreateContainerVisual();
        container.Size = size;
        container.Children.InsertAtBottom(_shadowSprite);
        container.Children.InsertAtTop(_shapes);
        ElementCompositionPreview.SetElementChildVisual(SurfaceHost, container);
        ApplyColors();
    }

    /// <summary>面・枠・影の色をテーマに合わせる。面と枠はピッカーと同じ色にする。</summary>
    private void ApplyColors()
    {
        if (_fill is null || _stroke is null || _shadow is null)
        {
            return;
        }

        bool dark = Root.ActualTheme == ElementTheme.Dark;
        _fill.Color = ((SolidColorBrush)ThemeResources.Brush("Morph.Surface")).Color;
        _stroke.Color = ((SolidColorBrush)ThemeResources.Brush("Morph.Stroke")).Color;
        _shadow.Color = Windows.UI.Color.FromArgb(dark ? (byte)0x80 : (byte)0x38, 0, 0, 0);
    }

    // ---------------------------------------------------------------- 下端の帯

    /// <summary>下端の案内と追加のボタンを、いまの入力に合わせる。</summary>
    private void UpdateFooter()
    {
        if (_busy)
        {
            return;
        }

        AddButton.IsEnabled = Composer.CanCreate;
        var keymap = App.Current.Services.Keymap;
        AddLabel.Content ??= KeyCaps.Content("追加", "composer.submit");
        HintText.Foreground = ThemeResources.Brush("TextFillColorTertiaryBrush");
        HintText.Text = !Hints.Shown ? ""
            : (Composer.CanCreate ? $"{keymap.Display("composer.submit")} で追加" : "タスク名を入力すると追加できます")
              + $"　{keymap.Display("composer.detail")} で詳細　Esc で閉じる";
        ToolTipService.SetToolTip(AddButton, KeyHints.Tip("composer.submit", "追加する"));
        ToolTipService.SetToolTip(DetailButton, KeyHints.Tip("composer.detail", "詳細パネルで入力を続ける"));
    }

    private void ShowConfirm(bool show)
    {
        _confirming = show;
        FooterConfirm.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        FooterNormal.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (show)
        {
            ContinueButton.Focus(FocusState.Keyboard);
        }
    }

    // ---------------------------------------------------------------- 操作

    private async void OnAdd(object sender, RoutedEventArgs e) => await SubmitAsync();

    private async void OnDetail(object sender, RoutedEventArgs e) => await ContinueInDetailAsync();

    private void OnDiscard(object sender, RoutedEventArgs e) => Vanish();

    private void OnContinue(object sender, RoutedEventArgs e)
    {
        ShowConfirm(false);
        Composer.FocusTitle();
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // ピッカーが受けなかった Esc で閉じる。確かめているあいだの Esc は、何も失わない側（続ける）にする。
        // 破棄は「破棄」のボタンを押したときだけにし、Esc を続けて押しても入力が消えないようにする（UX 規約 UX-11）
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            if (_confirming)
            {
                OnContinue(this, new RoutedEventArgs());
            }
            else
            {
                RequestClose();
            }
        }
    }

    /// <summary>入力がなければ閉じ、あれば下端で破棄してよいかを確かめる（UX 規約 UX-11、UX-14）。</summary>
    private void RequestClose()
    {
        if (_busy)
        {
            return;
        }

        if (Composer.Draft.HasContent)
        {
            ShowConfirm(true);
        }
        else
        {
            Vanish();
        }
    }

    /// <summary>ほかのウィンドウへ移ったとき、入力がなければ閉じる。入力があれば、手前に浮かせたまま残す。</summary>
    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated && !_busy && !_confirming && !_closing
            && !Composer.Draft.HasContent)
        {
            Vanish();
        }
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // Alt + F4 など。入力があれば確かめる
        if (!_closing)
        {
            args.Cancel = true;
            RequestClose();
        }
    }

    /// <summary>
    /// 追加する。追加のボタンを ✓ に変えて追加先を知らせ、少しおいてから消す。本体を開いていなければ、GitHub へ送り終えてから終える。
    /// </summary>
    private async Task SubmitAsync()
    {
        if (_busy || _confirming || !Composer.CanCreate)
        {
            return;
        }

        _busy = true;
        var draft = Composer.Draft;
        ShowAdded(draft.Title.Trim(), draft.Project is { } p ? ProjectDisplay.Name(p) : "");
        try
        {
            await draft.CreateAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("QuickAdd", ex);
            _busy = false;
            AddLabel.Opacity = 1;
            AddedMark.Visibility = Visibility.Collapsed;
            HintText.Foreground = ThemeResources.Brush("SystemFillColorCriticalBrush");
            HintText.Text = "追加できませんでした。入力はそのまま残しています";
            return;
        }

        App.Current.Shell?.ShowToast($"「{draft.Title.Trim()}」を追加しました");
        _addedTimer.Start();
    }

    /// <summary>追加した手応え: 追加のボタンを ✓ に変え、下端に追加先を添えて知らせる。</summary>
    private void ShowAdded(string title, string project)
    {
        // ラベルは幅を残したまま隠し、✓ に替えてもボタンの幅を変えない
        AddLabel.Opacity = 0;
        AddedMark.Visibility = Visibility.Visible;
        HintText.Foreground = ThemeResources.Brush("TextFillColorSecondaryBrush");
        HintText.Text = project.Length > 0 ? $"「{title}」を{project}に追加しました" : $"「{title}」を追加しました";
    }

    /// <summary>「詳細」: 本体を開き、入力を引き継いで詳細パネルで作成を続ける（UX-09）。</summary>
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

    /// <summary>板を薄れさせて消す。本体を開いていなければ、送信を待ってから終える。</summary>
    private void Vanish()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        _busy = true;
        Root.IsHitTestVisible = false;
        Motion.Conceal(Panel, new Vector3(0, -6, 0), ConcealDuration, async () =>
        {
            AppWindow.Hide();
            await App.Current.FlushIfAloneAsync();
            Close();
        });
    }

    private void CloseNow()
    {
        _closing = true;
        Close();
    }

    // ---------------------------------------------------------------- 板を動かす

    /// <summary>板の余白（押せる部品のないところ）をつかむと、板を動かせる。</summary>
    private void OnPanelPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (VisualTree.AncestorsAndSelf(e.OriginalSource as DependencyObject).TakeWhile(d => !ReferenceEquals(d, Panel))
            .Any(d => d is Control))
        {
            return;
        }

        GetCursorPos(out var cursor);
        _dragStart = new PointInt32(cursor.X, cursor.Y);
        _windowStart = AppWindow.Position;
        Panel.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPanelPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStart is not { } start)
        {
            return;
        }

        GetCursorPos(out var cursor);
        AppWindow.Move(new PointInt32(_windowStart.X + cursor.X - start.X, _windowStart.Y + cursor.Y - start.Y));
    }

    private void EndDrag(PointerRoutedEventArgs e)
    {
        if (_dragStart is not null)
        {
            _dragStart = null;
            Panel.ReleasePointerCapture(e.Pointer);
        }
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

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
}
