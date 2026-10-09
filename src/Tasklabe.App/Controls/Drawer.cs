using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Tasklabe.Animation;
using Tasklabe.App.Services;

namespace Tasklabe.App.Controls;

/// <summary>閉じるよう求めたときの、面が端へ向かっていた速さ（px/秒。払っていなければ 0）。</summary>
public sealed class DrawerDismissEventArgs(double velocity) : EventArgs
{
    public double Velocity { get; } = velocity;
}

/// <summary>
/// 画面の右端から出てくる引き出し（UI デザイン設計書 2.7 節・3.4.3 節、UX 規約 UX-10）。
/// 幕で下の画面を暗くして押下を受け止め、面は端からばねで出て、閉じるときは短い時間で端へ戻りながら終わり際に影ごと薄れる。
/// 見出しをつまんで端へ引くと、面の 3 分の 1 を越えるか払えば閉じ（離したときの速さを引き継ぐ）、反対へ引くとゴムのように抵抗する。
/// 閉じている途中は押下を下へ通し、そのあいだに開き直すと、いまの位置から出てくる。
/// 閉じるかどうかは持ち主が決める（<see cref="DismissRequested"/> を受けて <see cref="Close"/> か <see cref="Settle"/> を呼ぶ）。
/// </summary>
[ContentProperty(Name = nameof(Child))]
public sealed partial class Drawer : UserControl
{
    // Drawer の値（motion-tokens の duration.standard・fast、ease.standard・enter）
    private static readonly TimeSpan LeaveDuration = TimeSpan.FromMilliseconds(240);
    private static readonly TimeSpan ScrimInDuration = TimeSpan.FromMilliseconds(240);
    private static readonly TimeSpan ScrimOutDuration = TimeSpan.FromMilliseconds(160);

    /// <summary>払って閉じるときのばね。速さを引き継ぎ、ふつうに閉じるのと同じ時間で端へ着く。</summary>
    private static readonly Spring Fling = Motion.Smooth with { VisualDuration = 0.24 };

    /// <summary>閉じる動きのうち、面が不透明のまま動く割合。影が面と一緒に去るよう、終わり際だけで薄れる。</summary>
    private const float FadeFrom = 0.65f;

    /// <summary>端へ向けて払ったとみなす速さ（px/秒。Drawer の値）。</summary>
    private const double FlickVelocity = 500;

    /// <summary>つまんで動かし始めたとみなす距離（px。Motion の pan と同じ）。これより短ければ押しただけとする。</summary>
    private const double DragThreshold = 3;

    /// <summary>離したときの速さを求める区間。</summary>
    private static readonly TimeSpan VelocityWindow = TimeSpan.FromMilliseconds(100);

    private readonly Border _scrim = new();
    private readonly Grid _sheet = new() { HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Border _shadowHost = new() { IsHitTestVisible = false };
    private readonly Grid _panel = new();
    private readonly Grid _header = new();
    private readonly RollingText _title = new();
    private readonly RollingText _description = new();
    private readonly ContentPresenter _actions = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _closeButton = new();
    private readonly Border _body = new();
    private DispatcherQueueTimer? _collapseTimer;

    private bool _open;

    /// <summary>この時刻までは面がばねで動いている（Composition は動いている途中の値を返さないため、つかませない）。</summary>
    private long _movingUntil;

    /// <summary>コードで最後に置いた、または行き先として渡した面のずれ（px）。</summary>
    private float _offset;

    private uint? _pointer;
    private double _pressX;
    private bool _dragging;
    private readonly List<(long Time, double X)> _samples = [];

    private SpriteVisual? _shadowSprite;
    private ShapeVisual? _shadowShapes;
    private CompositionVisualSurface? _shadowImage;
    private CompositionRoundedRectangleGeometry? _shadowGeometry;
    private CompositionColorBrush? _shadowFill;
    private DropShadow? _shadow;

    public Drawer()
    {
        Visibility = Visibility.Collapsed;
        IsTabStop = false;

        _scrim.Style = AppResources.Style("Drawer.Scrim");
        _scrim.PointerPressed += OnScrimPressed;

        _title.WordStyle = AppResources.Style("Text.BodyLarge");
        _description.WordStyle = AppResources.Style("Text.Caption");
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        titles.Children.Add(_title);
        titles.Children.Add(_description);

        _closeButton.Style = AppResources.Style("Drawer.Button");
        _closeButton.Content = new FontIcon { FontSize = 14, Glyph = "\uE711" };
        AutomationProperties.SetName(_closeButton, "閉じる");
        ToolTipService.SetToolTip(_closeButton, "閉じる (Esc)");
        _closeButton.Click += (_, _) => DismissRequested?.Invoke(this, new DrawerDismissEventArgs(0));

        // 見出しはつまんで引く持ち手を兼ねる
        _header.Style = AppResources.Style("Drawer.Header");
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _header.Children.Add(titles);
        Grid.SetColumn(_actions, 1);
        _header.Children.Add(_actions);
        Grid.SetColumn(_closeButton, 2);
        _header.Children.Add(_closeButton);
        _header.PointerPressed += OnHeaderPressed;
        _header.PointerMoved += OnHeaderMoved;
        _header.PointerReleased += OnHeaderReleased;
        _header.PointerCaptureLost += OnHeaderCaptureLost;

        _panel.Style = AppResources.Style("Drawer.Panel");
        _panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _panel.Children.Add(_header);
        Grid.SetRow(_body, 1);
        _panel.Children.Add(_body);
        _panel.SizeChanged += (_, _) => UpdateShadow();

        // 開いているあいだ、Tab は面の中を巡る
        _sheet.TabFocusNavigation = KeyboardNavigationMode.Cycle;
        _sheet.Children.Add(_shadowHost);
        _sheet.Children.Add(_panel);

        var root = new Grid();
        root.Children.Add(_scrim);
        root.Children.Add(_sheet);
        Content = root;

        Loaded += (_, _) => BuildShadow();
        ActualThemeChanged += (_, _) => UpdateShadow();
    }

    /// <summary>面の中身（見出しの下）。</summary>
    public UIElement? Child
    {
        get => _body.Child;
        set => _body.Child = value;
    }

    /// <summary>見出しの閉じるボタンの左に置く操作（GitHub で開くなど）。</summary>
    public UIElement? Actions
    {
        get => _actions.Content as UIElement;
        set => _actions.Content = value;
    }

    /// <summary>面の幅。</summary>
    public double SheetWidth
    {
        get => _sheet.Width;
        set => _sheet.Width = value;
    }

    /// <summary>面とウィンドウの端とのあいだ（タイトルバーの下から出すときの上端など）。幕はウィンドウ全体を覆う。</summary>
    public Thickness SheetInset
    {
        get => _sheet.Margin;
        set => _sheet.Margin = value;
    }

    /// <summary>開いているか（閉じる動きの途中は開いていないとみなす）。</summary>
    public bool IsOpen => _open;

    /// <summary>幕を押した、閉じるボタンを押した、見出しを引いて閉じる側で離した。</summary>
    public event EventHandler<DrawerDismissEventArgs>? DismissRequested;

    private Visual SheetVisual => Motion.VisualOf(_sheet);

    private Visual ScrimVisual => ElementCompositionPreview.GetElementVisual(_scrim);

    /// <summary>面がウィンドウの端の外に隠れるずれ（面の幅）。</summary>
    private Vector3 Edge => new((float)Math.Max(_sheet.ActualWidth, double.IsNaN(SheetWidth) ? 0 : SheetWidth), 0, 0);

    /// <summary>見出しの名前と補足を変える。</summary>
    /// <param name="direction">1 なら後のものへ（下から上がる）、-1 なら前のものへ（上から下りる）。</param>
    /// <param name="animate">
    /// 入れ替わりを見せるか。開いたまま中身を替えたときだけ見せ、開くのと同時に決まった見出しはその場に置く。
    /// </param>
    public void SetHeader(string title, string? description, int direction, bool animate)
    {
        animate = animate && _open;
        direction = direction < 0 ? -1 : 1;
        _title.SetText(title, direction, animate);
        _description.SetText(description ?? "", direction, animate);
        _description.Visibility = string.IsNullOrEmpty(description) ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(_panel, title);
    }

    // ---------------------------------------------------------------- 開く・閉じる

    /// <summary>端から出す。閉じている途中なら、いまの位置から出てくる。</summary>
    public void Open()
    {
        if (_open)
        {
            return;
        }

        bool collapsed = Visibility == Visibility.Collapsed;
        _open = true;
        _collapseTimer?.Stop();
        EndDrag();
        Visibility = Visibility.Visible;
        IsHitTestVisible = true;

        var sheet = SheetVisual;
        var scrim = ScrimVisual;
        if (collapsed)
        {
            // 面は不透明のまま、端の外から出す
            sheet.StopAnimation("Translation");
            sheet.StopAnimation("Opacity");
            sheet.Properties.InsertVector3("Translation", Edge);
            sheet.Opacity = 1;
            scrim.StopAnimation("Opacity");
            scrim.Opacity = 0;
        }
        else
        {
            // 閉じる途中で薄れ始めていれば、出ながら戻す
            Motion.EaseTo(sheet, "Opacity", 1f, ScrimOutDuration, Motion.EnterEasing(sheet.Compositor));
        }

        _offset = 0;
        Motion.SpringTo(sheet, "Translation", Vector3.Zero, Motion.Smooth);
        Motion.EaseTo(scrim, "Opacity", 1f, ScrimInDuration, Motion.EnterEasing(scrim.Compositor));
        _movingUntil = Stopwatch.GetTimestamp() + Ticks(Motion.Smooth.VisualDuration);
    }

    /// <summary>
    /// 端へ戻して閉じる。払ったときは、その速さを引き継いだばねで端へ向かう。
    /// 閉じる動きのあいだは押下を下の画面へ通し（もう一度開くための押下を妨げない）、終わったら隠す。
    /// </summary>
    public void Close(double velocity = 0)
    {
        if (!_open)
        {
            return;
        }

        _open = false;
        EndDrag();
        IsHitTestVisible = false;
        if (!Motion.IsEnabled)
        {
            Collapse();
            return;
        }

        var sheet = SheetVisual;
        var edge = Edge;
        if (velocity > 0)
        {
            Motion.SpringTo(sheet, "Translation", edge, Fling, new Vector3((float)velocity, 0, 0));
        }
        else
        {
            Motion.EaseTo(sheet, "Translation", edge, LeaveDuration, Motion.StandardEasing(sheet.Compositor));
        }

        _offset = edge.X;
        Motion.EaseTo(sheet, "Opacity", 0f, LeaveDuration * (1 - FadeFrom), sheet.Compositor.CreateLinearEasingFunction(),
            delay: LeaveDuration * FadeFrom);
        Motion.EaseTo(ScrimVisual, "Opacity", 0f, ScrimOutDuration, Motion.StandardEasing(sheet.Compositor));

        // ばねは見た目の時間を過ぎても小さく動き続けるため、後始末は時間で行う
        _collapseTimer = DispatcherQueue.After(LeaveDuration, Collapse);
    }

    /// <summary>引いた面を元の位置へ戻す（閉じる側で離したが、閉じないと決めたときも）。</summary>
    public void Settle()
    {
        if (!_open)
        {
            return;
        }

        EndDrag();
        _offset = 0;
        Motion.SpringTo(SheetVisual, "Translation", Vector3.Zero, Motion.Snappy);
        _movingUntil = Stopwatch.GetTimestamp() + Ticks(Motion.Snappy.VisualDuration);
    }

    private void Collapse()
    {
        _collapseTimer?.Stop();
        if (!_open)
        {
            Visibility = Visibility.Collapsed;
        }
    }

    private void OnScrimPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        if (_open)
        {
            DismissRequested?.Invoke(this, new DrawerDismissEventArgs(0));
        }
    }

    // ---------------------------------------------------------------- 見出しを引いて閉じる

    private void OnHeaderPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!_open || !Motion.IsEnabled || _pointer is not null || IsOnButton(e.OriginalSource as DependencyObject)
            || (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && !point.Properties.IsLeftButtonPressed)
            || Stopwatch.GetTimestamp() < _movingUntil)
        {
            return;
        }

        _pointer = e.Pointer.PointerId;
        _pressX = point.Position.X;
        _dragging = false;
        _samples.Clear();
        _samples.Add((Stopwatch.GetTimestamp(), _pressX));
        _header.CapturePointer(e.Pointer);
    }

    private void OnHeaderMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_pointer != e.Pointer.PointerId)
        {
            return;
        }

        double x = e.GetCurrentPoint(this).Position.X;
        long now = Stopwatch.GetTimestamp();
        _samples.Add((now, x));
        while (_samples.Count > 2 && now - _samples[0].Time > Ticks(VelocityWindow.TotalSeconds))
        {
            _samples.RemoveAt(0);
        }

        double toward = x - _pressX;
        if (!_dragging && Math.Abs(toward) < DragThreshold)
        {
            return;
        }

        // 端へは指についていき、反対へはゴムのように抵抗する
        _dragging = true;
        _offset = (float)(toward >= 0 ? toward : -Math.Sqrt(-toward));
        var sheet = SheetVisual;
        sheet.StopAnimation("Translation");
        sheet.Properties.InsertVector3("Translation", new Vector3(_offset, 0, 0));
        e.Handled = true;
    }

    private void OnHeaderReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_pointer != e.Pointer.PointerId)
        {
            return;
        }

        bool dragged = _dragging;
        double velocity = Velocity();
        _header.ReleasePointerCapture(e.Pointer);
        EndDrag();
        if (!dragged)
        {
            return;
        }

        e.Handled = true;
        if (_offset > _panel.ActualWidth / 3 || (_offset > 0 && velocity > FlickVelocity))
        {
            DismissRequested?.Invoke(this, new DrawerDismissEventArgs(Math.Max(velocity, 0)));
        }
        else
        {
            Settle();
        }
    }

    private void OnHeaderCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_pointer != e.Pointer.PointerId)
        {
            return;
        }

        bool dragged = _dragging;
        EndDrag();
        if (dragged)
        {
            Settle();
        }
    }

    private void EndDrag()
    {
        _pointer = null;
        _dragging = false;
        _samples.Clear();
    }

    /// <summary>直近の区間で、端へ向かっていた速さ（px/秒）。</summary>
    private double Velocity()
    {
        if (_samples.Count < 2)
        {
            return 0;
        }

        var (t0, x0) = _samples[0];
        var (t1, x1) = _samples[^1];
        double seconds = (t1 - t0) / (double)Stopwatch.Frequency;
        return seconds <= 0 ? 0 : (x1 - x0) / seconds;
    }

    private bool IsOnButton(DependencyObject? source) =>
        VisualTree.AncestorsAndSelf(source).TakeWhile(e => !ReferenceEquals(e, _header)).Any(e => e is ButtonBase);

    private static long Ticks(double seconds) => (long)(seconds * Stopwatch.Frequency);

    // ---------------------------------------------------------------- 影

    /// <summary>
    /// 面の影。面と同じ形の図形を写した SpriteVisual に DropShadow を付け、面の後ろに敷く。
    /// 面の右側の角はウィンドウの端の外に出すため、図形は丸めの分だけ右へ長くする。
    /// </summary>
    private void BuildShadow()
    {
        if (_shadowSprite is not null)
        {
            return;
        }

        var compositor = ElementCompositionPreview.GetElementVisual(_shadowHost).Compositor;
        float radius = (float)_panel.CornerRadius.TopLeft;
        _shadowGeometry = compositor.CreateRoundedRectangleGeometry();
        _shadowGeometry.CornerRadius = new Vector2(radius);
        _shadowFill = compositor.CreateColorBrush();
        var shape = compositor.CreateSpriteShape(_shadowGeometry);
        shape.FillBrush = _shadowFill;
        _shadowShapes = compositor.CreateShapeVisual();
        _shadowShapes.Shapes.Add(shape);

        _shadowImage = compositor.CreateVisualSurface();
        _shadowImage.SourceVisual = _shadowShapes;
        _shadow = compositor.CreateDropShadow();
        _shadow.BlurRadius = 32;
        _shadow.Offset = new Vector3(0, 8, 0);
        _shadow.SourcePolicy = CompositionDropShadowSourcePolicy.InheritFromVisualContent;
        _shadowSprite = compositor.CreateSpriteVisual();
        _shadowSprite.Brush = compositor.CreateSurfaceBrush(_shadowImage);
        _shadowSprite.Shadow = _shadow;
        ElementCompositionPreview.SetElementChildVisual(_shadowHost, _shadowSprite);
        UpdateShadow();
    }

    private void UpdateShadow()
    {
        if (_shadowSprite is null || _shadowShapes is null || _shadowImage is null || _shadowGeometry is null || _shadowFill is null || _shadow is null)
        {
            return;
        }

        var size = new Vector2((float)(_panel.ActualWidth + _panel.CornerRadius.TopLeft), (float)_panel.ActualHeight);
        _shadowGeometry.Size = size;
        _shadowShapes.Size = size;
        _shadowImage.SourceSize = size;
        _shadowSprite.Size = size;

        // 面の色は、テーマで解決した面の塗りから読む（影の図形が面の縁からのぞかないように）
        if (_panel.Background is SolidColorBrush fill)
        {
            _shadowFill.Color = fill.Color;
        }

        bool dark = ActualTheme == ElementTheme.Dark;
        _shadow.Color = Windows.UI.Color.FromArgb(dark ? (byte)0x80 : (byte)0x38, 0, 0, 0);
    }
}
