using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Tasklabe.Animation;
using Tasklabe.App.Services;
using Windows.Foundation;
using Windows.System;

namespace Tasklabe.App.Controls;

/// <summary>起点とのあいだを運ぶ要素の、出入りのしかた（<see cref="MorphPopup.Carry"/>）。</summary>
internal enum CarryFade
{
    /// <summary>見えたまま運ぶ（上端の欄の値、アイコン、矢印）。</summary>
    None,

    /// <summary>起点の写し。起点の位置では見えていて、パネルへ運ばれるあいだに薄れ、閉じて戻るあいだに現れる。</summary>
    Out,

    /// <summary>起点と姿の違う値（日付のピッカーの下端の日付）。起点の位置では見えず、パネルへ運ばれるあいだに現れ、戻るあいだに薄れる。</summary>
    In,
}

/// <summary>
/// 起点（押したボタン、値のセル、カード、右クリックした位置）から面が育って開き、閉じると起点へ縮むポップアップ
/// （UI デザイン設計書 2.7 節、4.1.2 節）。候補から選ぶピッカー、日付のピッカー、メニューが使う。
/// 面・影・切り抜き・外側を押したときの扱いと、起点がボタンの形のときに起点を隠して値（<see cref="Carry"/> で結んだもの）を
/// 起点とのあいだで運ぶ動きを受け持つ。中身の作りは使う側が決める。
/// </summary>
internal sealed class MorphPopup
{
    private const double WindowEdge = 8;

    /// <summary>右クリックした位置から開くときの、起点の大きさ。</summary>
    private static readonly Size PointOrigin = new(40, 32);

    private static readonly TimeSpan FaceInDuration = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan FaceInDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan FaceOutDuration = TimeSpan.FromMilliseconds(120);

    /// <summary>ボタンの色と面の色のあいだを移る時間。</summary>
    private static readonly TimeSpan SurfaceFade = TimeSpan.FromMilliseconds(160);

    /// <summary>影が濃くなる・薄くなる時間。起点（影のないボタン）から浮いた面へ、少しずつ持ち上がって見せる。</summary>
    private static readonly TimeSpan ShadowDuration = TimeSpan.FromMilliseconds(260);

    private readonly FrameworkElement _anchor;
    private readonly Point? _position;
    private readonly Popup _popup;
    private readonly Grid _root;
    private readonly Grid _panel;
    private readonly Grid _surfaceHost = new();

    /// <summary>
    /// 面の塗りの色を、パネルのテーマで引くための見えない要素。ほかのピッカーと同じ背景（アクリル）を解決し、
    /// その代替色を図形に渡す。コードからの色の引き方（ThemeResources）では、ダークでもライトの値が返る色があるため。
    /// </summary>
    private readonly Border _surfaceColor = new() { Width = 0, Height = 0, IsHitTestVisible = false };

    private readonly FrameworkElement _content;
    private readonly DependencyObject? _restoreFocus;
    private CompositionRoundedRectangleGeometry? _geometry;
    private CompositionColorBrush? _surfaceFill;
    private CompositionColorBrush? _surfaceStroke;
    private DropShadow? _shadow;
    private ContainerVisual? _surface;
    private ShapeVisual? _surfaceShapes;
    private SpriteVisual? _surfaceShadow;
    private CompositionVisualSurface? _surfaceImage;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _finishTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _meltTimer;
    private (Windows.UI.Color Fill, Windows.UI.Color Stroke) _openLook;
    private Rect _origin;
    private bool _opened;
    private bool _closing;
    private bool _focusRestored;

    /// <summary>起点とパネルのあいだを行き来する要素と、起点の中で同じ役目の要素。</summary>
    private readonly List<(UIElement Element, FrameworkElement Target, CarryFade Fade)> _carried = [];

    /// <summary>
    /// 開いている途中（面が育ち切るまでと、Windows のダブルクリックの時間の長いほう）だけ、起点の範囲に重ねる透明な受け。
    /// ここを押すと取り消して閉じる。元のボタンをもう一度押すと閉じるという分かりやすい基準になり、
    /// 素早く 2 回押しても、2 回目が起点の上に育った面の中のボタン（日付のピッカーのクリアなど）に当たらない。
    /// 起点の外の面はふつうに押せ（候補を選べる）、面の外を押せばいつでも閉じる。
    /// </summary>
    private readonly Border _shield = new()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
    };

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _guardTimer;

    /// <summary>
    /// 開いているポップアップが隠している起点と、隠す前の不透明度、隠しているポップアップの数。
    /// 同じ起点から続けて開くピッカー（絞り込みの条件 → 値）では、前のものが縮み終える前に次が起点を隠すため、数えて最後に戻す。
    /// </summary>
    private static readonly Dictionary<FrameworkElement, (double Opacity, int Count)> HiddenTriggers = [];

    /// <summary>このポップアップが起点を隠している。</summary>
    private bool _hidesTrigger;

    /// <summary>
    /// ボタンの形の起点ごとの、面が残っている（開き始めてから閉じ終えるまでの）ポップアップ。
    /// 閉じている面は押下を下へ通すため、連打の次の 1 回が隠れた起点に当たることがある。同じ起点から新しく開くときは、
    /// 前の面をその場で消してから育て、面が重ならないようにする（絞り込みの「条件 → 値」のように続けて開くときも同じ）。
    /// </summary>
    private static readonly Dictionary<FrameworkElement, MorphPopup> OpenOnTrigger = [];

    /// <summary>閉じ終えた（起点に戻した）。</summary>
    private bool _finished;

    /// <param name="anchor">起点の要素。開いた位置と閉じたあとのフォーカスの戻り先にも使う。</param>
    /// <param name="position">右クリックした位置（anchor の中の座標）。指定したときはその位置から開く。</param>
    /// <param name="content">中身。</param>
    public MorphPopup(FrameworkElement anchor, Point? position, FrameworkElement content)
    {
        _anchor = anchor;
        _position = position;
        _content = content;
        Trigger = position is null ? PickerTrigger.Of(anchor) : null;
        var xamlRoot = anchor.XamlRoot;
        _restoreFocus = FocusManager.GetFocusedElement(xamlRoot) as DependencyObject;

        _surfaceColor.Style = AppResources.Style("MorphPopup.SurfaceColor");
        _panel = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Children = { _surfaceColor, _surfaceHost, content },
        };

        // 木に属さない Popup はアプリ全体のテーマ（Windows の設定）で描かれるため、起点のテーマ（設定の「テーマ」）を渡す
        _root = new Grid
        {
            RequestedTheme = anchor.ActualTheme,
            Width = xamlRoot.Size.Width,
            Height = xamlRoot.Size.Height,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Children = { _panel },
        };
        _root.PointerPressed += (_, e) =>
        {
            // パネルの外を押したら閉じる（何を決めたかは使う側が決める）。開いている途中でも同じで、閉じる面は、いまの途中の大きさから起点へ縮む
            if (ReferenceEquals(e.OriginalSource, _root))
            {
                Dismissed?.Invoke(this, EventArgs.Empty);
            }
        };
        content.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key == VirtualKey.Escape && !_closing)
            {
                e.Handled = true;
                if (Back?.Invoke() != true)
                {
                    Dismissed?.Invoke(this, EventArgs.Empty);
                }
            }
        }), true);
        // Tab はパネルの中を巡らせる
        content.TabFocusNavigation = KeyboardNavigationMode.Cycle;
        LastInput.Track(_root);

        _popup = new Popup { XamlRoot = xamlRoot, Child = _root };
        xamlRoot.Changed += OnXamlRootChanged;
    }

    /// <summary>外を押した、Esc を押した、ウィンドウの大きさが変わった。使う側は <see cref="Close"/> を呼ぶ。</summary>
    public event EventHandler? Dismissed;

    /// <summary>面が広がり始めた。中身へのフォーカスはここで移す。</summary>
    public event EventHandler? Grown;

    /// <summary>
    /// 下に収まらず、起点の下端を留めて上へ開く（並べる前に知らせる）。上端の欄を持つ中身は、ここで並びを上下逆にし、
    /// 上端の欄が起点に重なったまま横に動くようにする。上端の欄を上に置いたままだと、起点の位置に候補が来る。
    /// </summary>
    public event EventHandler? OpensUpward;

    /// <summary>起点の下端を留めて上へ開いている。中身の大きさが変わっても、下端を起点に留めたまま伸び縮みさせる。</summary>
    public bool IsUpward { get; private set; }

    /// <summary>
    /// 中身のうち、面より少し遅れて現れ、閉じるときは面より先に消えるもの。
    /// 起点とのあいだを行き来させる値（下端の範囲など）は含めない。
    /// </summary>
    public List<UIElement> Faces { get; } = [];

    /// <summary>
    /// <see cref="Faces"/> を 1 つずつずらして現す間隔（メニューの行）。ずれは先頭から 4 つ分までとし、長い一覧でも待たせない。
    /// </summary>
    public TimeSpan FaceStagger { get; set; }

    /// <summary>Esc を中身が先に受け持つ（サブメニューから戻るなど）。true を返したら閉じない。</summary>
    public Func<bool>? Back { get; set; }

    /// <summary>
    /// 起点がボタンの形（ピッカーのボタン、計画の表のステータスのセル）なら、その形。値をボタンへ運ぶ動きの行き先と、
    /// 横に広げる向き（「⌄」の側を留める）に使う。
    /// </summary>
    internal IPickerTrigger? Trigger { get; }

    /// <summary>閉じ始めている。</summary>
    public bool IsClosing => _closing;

    /// <summary>面が育つばね（育つ距離に合わせたもの）。面と一緒に動かす値（下端の日付など）にも使う。</summary>
    public Spring GrowSpring { get; private set; } = Motion.Grow;

    /// <summary>
    /// 面が起点へ縮むばね。面と一緒に起点へ戻す値（選んだ候補の文字、上端の欄）にも使う。
    /// パネルの中のどこからでも（いちばん長いパネルの対角線の距離でも）、閉じ終える時刻（<see cref="Motion.Shrink"/> の見た目の時間）に
    /// 着き切る固さにする。着き切らないまま起点に切り替わると、残りの分だけ跳ぶ。
    /// </summary>
    public Spring ShrinkSpring => Motion.Shrink.Landing(new Vector2((float)_panel.ActualWidth, (float)_panel.ActualHeight).Length());

    public void Open()
    {
        // 面が育ち始めるときの見た目。ポップアップが重なると起点は載せた状態を外れるため、重ねる前に読む
        if (Trigger is { } look)
        {
            _openLook = LookOf(look.Element);
        }

        // 同じ起点の面がまだ残っていれば、その場で消してから育てる（まだ結果を返していなければ「選ばずに閉じた」にする）
        if (Trigger is { } own)
        {
            if (OpenOnTrigger.TryGetValue(own.Element, out var previous) && previous != this)
            {
                previous.Vanish();
            }

            OpenOnTrigger[own.Element] = this;
        }

        _content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _content.DesiredSize;
        _origin = Origin(size);
        Place(size);
        IsUpward = _panel.Margin.Top < _origin.Top && Math.Abs(_panel.Margin.Top + size.Height - _origin.Bottom) < 0.5;
        if (IsUpward)
        {
            OpensUpward?.Invoke(this, EventArgs.Empty);
        }

        BuildSurface(new Vector2((float)size.Width, (float)size.Height));
        SetToOrigin(_geometry!);
        _surface!.Opacity = 0;
        foreach (var face in Faces)
        {
            Motion.VisualOf(face).Opacity = 0;
        }

        foreach (var (element, _, fade) in _carried)
        {
            if (fade != CarryFade.None)
            {
                Motion.VisualOf(element).Opacity = 0;
            }
        }

        // 開いている途中は、起点の範囲に受けを重ね、元のボタンをもう一度押したら取り消して閉じる
        _shield.Margin = new Thickness(_origin.Left, _origin.Top, 0, 0);
        _shield.Width = _origin.Width;
        _shield.Height = _origin.Height;
        _shield.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            Dismissed?.Invoke(this, EventArgs.Empty);
        };
        _root.Children.Add(_shield);

        _panel.SizeChanged += (_, e) =>
        {
            // 入力の誤りの表示や絞り込みで大きさが変わったら、面も合わせる
            var resized = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
            if (_surface is not null && _surfaceShapes is not null && _surfaceShadow is not null && _surfaceImage is not null)
            {
                _surface.Size = Vector2.Max(_surface.Size, resized);
                _surfaceShapes.Size = _surface.Size;
                _surfaceShadow.Size = _surface.Size;
                _surfaceImage.SourceSize = _surface.Size;
            }

            if (_opened && !_closing && _geometry is not null)
            {
                // 入力欄が遅れて高さを持つ、メニューの中身が入れ替わるなどで大きさが変わったら、面を伸び縮みさせる。
                // いまの位置で収まるなら動かさない（中身が入れ替わるたびに留める角が変わり、面が飛ばないように）。
                // はみ出すときだけ置き直し、動いた分は図形をずらしておいて、そこから新しい位置へ伸び縮みさせる
                // 上へ開いたものは、下端を起点に留めたまま上端を動かす（上端の欄が起点から離れないように）
                var before = _panel.Margin;
                if (IsUpward && _origin.Bottom - e.NewSize.Height >= WindowEdge)
                {
                    _panel.Margin = new Thickness(before.Left, _origin.Bottom - e.NewSize.Height, 0, 0);
                }
                else if (!Fits(e.NewSize))
                {
                    Place(e.NewSize);
                }

                var moved = new Vector2((float)(before.Left - _panel.Margin.Left), (float)(before.Top - _panel.Margin.Top));
                if (moved != Vector2.Zero)
                {
                    _geometry.StopAnimation("Offset");
                    _geometry.Offset = moved;
                    Motion.SpringTo(_geometry, "Offset", Vector2.Zero, Motion.Grow.ForDistance(moved.Length()));
                }

                var previous = new Vector2((float)e.PreviousSize.Width, (float)e.PreviousSize.Height);
                Motion.SpringTo(_geometry, "Size", resized, Motion.Grow.ForDistance(Vector2.Abs(resized - previous).Length()));
            }
        };
        _root.Loaded += (_, _) => _root.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Grow);
        _popup.IsOpen = true;
    }

    /// <summary>閉じる。中身を先に消し、面を起点の大きさへ縮めてから消す。</summary>
    /// <param name="keyboard">閉じたあとのフォーカスを、キーボードの枠付きで戻す。</param>
    /// <param name="focusNow">
    /// 縮むのを待たずにフォーカスを戻す。メニューの項目がピッカーやダイアログを開くとき、
    /// その戻り先が閉じていくメニューにならず、縮み終えたときに開いたものからフォーカスを奪わないようにする。
    /// </param>
    public void Close(bool keyboard = false, bool focusNow = false)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        if (focusNow)
        {
            RestoreFocus(keyboard);
            _focusRestored = true;
        }

        // 縮んでいるあいだは何も受け付けない（切り抜いた中身も、当たり判定は残っているため）
        _root.IsHitTestVisible = false;
        if (!_opened || !Motion.IsEnabled || _geometry is null)
        {
            Finish(keyboard);
            return;
        }

        foreach (var face in Faces)
        {
            Motion.EaseTo(Motion.VisualOf(face), "Opacity", 0f, FaceOutDuration);
        }

        var origin = OriginInPanel();
        var shrink = ShrinkSpring;
        foreach (var (element, target, fade) in _carried)
        {
            var carried = Motion.VisualOf(element);
            Motion.SpringTo(carried, "Translation", OffsetTo(element, target), shrink);

            // 戻るあいだに、姿の違う値は薄れ、起点の写しが現れて、着いたときには起点と同じ姿になっている
            if (fade == CarryFade.Out)
            {
                Motion.EaseTo(carried, "Opacity", 1f, SurfaceFade);
            }
            else if (fade == CarryFade.In)
            {
                Motion.EaseTo(carried, "Opacity", 0f, SurfaceFade);
            }
        }

        // 閉じ終えるのは、面と運ぶ値が着き切る時刻（ShrinkSpring はこの時刻に着き切る固さにしてある）
        var landed = TimeSpan.FromSeconds(Motion.Shrink.VisualDuration);

        Motion.SpringTo(_geometry, "Offset", origin.Offset, shrink);
        Motion.SpringTo(_geometry, "Size", origin.Size, shrink);
        Motion.SpringTo(_geometry, "CornerRadius", new Vector2((float)RadiusOf("Radius.Control")), shrink);
        if (_shadow is not null)
        {
            Motion.EaseTo(_shadow, "Opacity", 0f, ShadowDuration);
        }

        // 面は縮み終えるまで面の色を保ち、着地する直前に起点の見た目へ溶かして、縮み終えたときに起点と見分けがつかないようにする。
        // ボタンの形の起点なら、起点の塗りと枠（表のセルなら透明）へ移す。そうでない起点（値のセル、カード、右クリックの位置）は薄くして消す
        var melt = landed - FaceOutDuration;
        if (Trigger is { } trigger && _surfaceFill is not null && _surfaceStroke is not null)
        {
            // 着地する見た目は、溶け始める時点で起点が実際にとっている見た目にする。隠している起点も、閉じ始めて根元が押下を通すと
            // ポインターを載せた状態になりうるが、ポインターを動かさなければ通常のままのこともある（撮影で確かめた）
            var surfaceFill = _surfaceFill;
            var surfaceStroke = _surfaceStroke;
            _meltTimer = _root.DispatcherQueue.After(melt, () =>
            {
                var (fill, stroke) = LookOf(trigger.Element);
                Motion.EaseTo(surfaceFill, Landing(fill, "Morph.Surface"), FaceOutDuration);
                Motion.EaseTo(surfaceStroke, Landing(stroke, "Morph.Stroke"), FaceOutDuration);
            });
        }
        else if (_surface is not null)
        {
            Motion.EaseTo(_surface, "Opacity", 0f, FaceOutDuration, delay: melt);
        }

        // ばねの完了は待たず（ごくわずかに動き続けるため）、着き切った時刻で起点に戻す
        _finishTimer = _root.DispatcherQueue.After(landed, () => Finish(keyboard));
    }

    /// <summary>
    /// 起点の見た目の写しを撮る。撮れなければ null（写しなしで開く）。起点が撮っている途中で画面から外れると
    /// （カンバンがデータの変更でボードを作り直したときなど）、RenderAsync は描画の中断（E_ABORT）で失敗する。
    /// 写しは見た目を滑らかにするためだけのものなので、撮れなくてもピッカーは開き、起点の状態をログに残す。
    /// </summary>
    public static async Task<Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap?> TrySnapshotAsync(FrameworkElement element)
    {
        try
        {
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
            await bitmap.RenderAsync(element);
            return bitmap;
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            AppLog.Info($"起点の写しを撮れませんでした（0x{ex.HResult:X8}）: {element.GetType().Name}、"
                + $"読み込み済み {element.IsLoaded}、XamlRoot {(element.XamlRoot is null ? "なし" : "あり")}、"
                + $"親 {(VisualTreeHelper.GetParent(element) is null ? "なし" : "あり")}、大きさ {element.ActualWidth:0}x{element.ActualHeight:0}");
            return null;
        }
    }

    /// <summary>
    /// パネルの中の要素（上端の欄の値の文字、アイコン、矢印、下端の日付）を、起点の中で同じ役目の要素と結ぶ。
    /// 開くときは起点の要素の位置からパネルの中の位置へ、閉じるときは起点の要素の位置へ、面と同じばねで運ぶ。
    /// 起点の要素と重なって見えるため、面が横に広がっても、起点にあった文字がそのまま新しい位置へ動いて見える。
    /// 起点と姿の違う値は、起点の写しと組にして <see cref="CarryFade"/> で入れ替えながら運ぶ。
    /// </summary>
    public void Carry(UIElement element, FrameworkElement target, CarryFade fade = CarryFade.None) => _carried.Add((element, target, fade));

    /// <summary>
    /// パネルの中の要素を、起点のボタンの値の文字（値の文字を持たない小さな起点では、そのアイコン）に重ねる Translation。
    /// 起点がピッカーのボタンでなければ null。
    /// </summary>
    public Vector3? OffsetToTriggerValue(UIElement element) =>
        Trigger is { } trigger && (trigger.ValueText ?? trigger.Icon) is { } target ? OffsetTo(element, target) : null;

    /// <summary>
    /// パネルの中の要素を、起点の中の要素に重ねる Translation（並べた位置からのずれ。横は左端、縦は中心を合わせる）。
    /// Translation の値を読むと動いている途中ではなく最後に置いた値が返るため、いまの Translation は足さない。
    /// </summary>
    private Vector3 OffsetTo(UIElement element, FrameworkElement targetElement)
    {
        // 横は左端、縦は中心で合わせる。値の文字は上端の欄のほうが箱が広く、表のセルの「⌄」は箱がセルの高さいっぱいに
        // 伸びている（形は箱の中央に描かれる）ため、左上で合わせると着地したあとに起点へ切り替わる瞬間に跳ぶ
        var target = LaidOut(targetElement, targetElement.XamlRoot.Content);
        var laidOut = LaidOut(element, _root);
        double dy = (target.Y + targetElement.ActualSize.Y / 2) - (laidOut.Y + element.ActualSize.Y / 2);
        return new Vector3((float)(target.X - laidOut.X), (float)dy, 0);
    }

    /// <summary>
    /// 要素を並べた位置。要素そのものの <c>TransformToVisual</c> は Composition で回した回転（上端の欄の矢印）を含むため、
    /// 親の位置に、親の中で並べた位置（<c>ActualOffset</c>）を足して求める。
    /// </summary>
    private static Point LaidOut(UIElement element, UIElement root)
    {
        if (VisualTreeHelper.GetParent(element) is not UIElement parent)
        {
            return element.TransformToVisual(root).TransformPoint(default);
        }

        var offset = element.ActualOffset;
        return parent.TransformToVisual(root).TransformPoint(new Point(offset.X, offset.Y));
    }

    /// <summary>
    /// 起点の矩形（ウィンドウの座標）。右クリックした位置からなら小さな矩形、要素からなら要素の矩形とする。
    /// 一覧の行のような横に長い要素は、パネルの幅と 1 行の高さに収める。
    /// </summary>
    private Rect Origin(Size panel)
    {
        var toRoot = _anchor.TransformToVisual(_anchor.XamlRoot.Content);
        if (_position is { } p)
        {
            var at = toRoot.TransformPoint(p);
            return new Rect(at.X, at.Y, PointOrigin.Width, PointOrigin.Height);
        }

        var rect = toRoot.TransformBounds(new Rect(0, 0, _anchor.ActualWidth, _anchor.ActualHeight));
        return new Rect(rect.X, rect.Y, Math.Min(rect.Width, panel.Width), Math.Min(rect.Height, 40));
    }

    /// <summary>
    /// 起点の左上に置く。「⌄」を持つボタンの形の起点より広いパネルは、右端（「⌄」の側）を留めて左へ広げ、上端の欄の矢印をボタンの矢印に重ねる。
    /// 「⌄」を持たない起点（カードのアイコンやチップ）は、値の側（左端）を留めて右へ広げる。
    /// ウィンドウからはみ出すときは、反対の側を留めるか、はみ出す分だけずらす。
    /// </summary>
    private void Place(Size size)
    {
        _panel.Margin = new Thickness(
            Pin(_origin.Left, _origin.Right, size.Width, _anchor.XamlRoot.Size.Width, preferEnd: Trigger?.Chevron is not null),
            Pin(_origin.Top, _origin.Bottom, size.Height, _anchor.XamlRoot.Size.Height, preferEnd: false), 0, 0);
    }

    /// <summary>いまの位置のままで、ウィンドウの端から離して収まる。</summary>
    private bool Fits(Size size) =>
        _panel.Margin.Left + size.Width <= _anchor.XamlRoot.Size.Width - WindowEdge
        && _panel.Margin.Top + size.Height <= _anchor.XamlRoot.Size.Height - WindowEdge;

    /// <summary>
    /// 起点の 1 つの角を留めて、残りの 2 辺だけを伸ばす位置を求める（1 次元ぶん）。
    /// 起点の始まりの側（<paramref name="preferEnd"/> なら終わりの側）に留めて収まればそこ、収まらなければ反対の側に留める。
    /// どちらでも収まらないときだけずらす。
    /// </summary>
    private static double Pin(double start, double end, double length, double bounds, bool preferEnd)
    {
        bool fitsFromStart = start + length <= bounds - WindowEdge;
        bool fitsFromEnd = end - length >= WindowEdge;
        if (preferEnd ? fitsFromEnd : fitsFromStart)
        {
            return preferEnd ? end - length : start;
        }

        if (preferEnd ? fitsFromStart : fitsFromEnd)
        {
            return preferEnd ? start : end - length;
        }

        return Math.Max(WindowEdge, Math.Min(start, bounds - WindowEdge - length));
    }

    /// <summary>起点の矩形とパネルの大きさのあいだで、いちばん長く動く辺の距離。</summary>
    private float Travel(Vector2 size)
    {
        var origin = OriginInPanel();
        var near = Vector2.Abs(origin.Offset);
        var far = Vector2.Abs(origin.Offset + origin.Size - size);
        return Math.Max(Math.Max(near.X, near.Y), Math.Max(far.X, far.Y));
    }

    private (Vector2 Offset, Vector2 Size) OriginInPanel() => (
        new Vector2((float)(_origin.X - _panel.Margin.Left), (float)(_origin.Y - _panel.Margin.Top)),
        new Vector2((float)_origin.Width, (float)_origin.Height));

    private void SetToOrigin(CompositionRoundedRectangleGeometry geometry)
    {
        var origin = OriginInPanel();
        geometry.Offset = origin.Offset;
        geometry.Size = origin.Size;
        geometry.CornerRadius = new Vector2((float)RadiusOf("Radius.Control"));
    }

    /// <summary>
    /// 面を作る。角丸の図形を描き、その下に図形の形を写した影を落とす。中身はその図形で切り抜く。
    /// 図形は大きさを変えながら動くため、影は図形を写した面から作り、図形と同じ形に付いてくるようにする。
    /// （ShapeVisual を LayerVisual の子にすると図形が描かれないため、LayerVisual の影は使わない）
    /// </summary>
    private void BuildSurface(Vector2 size)
    {
        var compositor = ElementCompositionPreview.GetElementVisual(_surfaceHost).Compositor;
        bool dark = _anchor.ActualTheme == ElementTheme.Dark;
        _geometry = compositor.CreateRoundedRectangleGeometry();
        var face = compositor.CreateSpriteShape(_geometry);
        _surfaceFill = compositor.CreateColorBrush();
        face.FillBrush = _surfaceFill;

        // 枠は、XAML の枠（BorderThickness）と同じく面の内側に 1 px で描く。図形の線は輪郭を挟んで内外に半分ずつ描かれるため、
        // 面の図形を 0.5 px 内側へ縮めた図形に引く。輪郭のまま引くと、上の外側半分が面の端で切れて薄くなり、下は 1 px 下の段に出て、
        // 起点のボタンに着地して切り替わる瞬間に枠が跳ぶ（白い面の上で、下の縁が一瞬暗く見える）
        var inset = compositor.CreateRoundedRectangleGeometry();
        foreach (var (property, expression) in new[]
        {
            ("Offset", "g.Offset + Vector2(0.5, 0.5)"),
            ("Size", "Max(g.Size - Vector2(1, 1), Vector2(0, 0))"),
            ("CornerRadius", "Max(g.CornerRadius - Vector2(0.5, 0.5), Vector2(0, 0))"),
        })
        {
            var follow = compositor.CreateExpressionAnimation(expression);
            follow.SetReferenceParameter("g", _geometry);
            inset.StartAnimation(property, follow);
        }

        var edge = compositor.CreateSpriteShape(inset);
        _surfaceStroke = compositor.CreateColorBrush(ColorOf("Morph.Stroke"));
        edge.StrokeBrush = _surfaceStroke;
        edge.StrokeThickness = 1;
        _surfaceShapes = compositor.CreateShapeVisual();
        _surfaceShapes.Size = size;
        _surfaceShapes.Shapes.Add(face);
        _surfaceShapes.Shapes.Add(edge);

        _surfaceImage = compositor.CreateVisualSurface();
        _surfaceImage.SourceVisual = _surfaceShapes;
        _surfaceImage.SourceSize = size;
        var shadow = compositor.CreateDropShadow();
        shadow.BlurRadius = 32;
        shadow.Offset = new Vector3(0, 8, 0);
        shadow.Color = Windows.UI.Color.FromArgb(dark ? (byte)0x80 : (byte)0x38, 0, 0, 0);
        shadow.SourcePolicy = CompositionDropShadowSourcePolicy.InheritFromVisualContent;
        shadow.Opacity = 0;
        _shadow = shadow;
        _surfaceShadow = compositor.CreateSpriteVisual();
        _surfaceShadow.Size = size;
        _surfaceShadow.Brush = compositor.CreateSurfaceBrush(_surfaceImage);
        _surfaceShadow.Shadow = shadow;

        _surface = compositor.CreateContainerVisual();
        _surface.Size = size;
        _surface.Children.InsertAtBottom(_surfaceShadow);
        _surface.Children.InsertAtTop(_surfaceShapes);
        ElementCompositionPreview.SetElementChildVisual(_surfaceHost, _surface);

        ElementCompositionPreview.GetElementVisual(_content).Clip = compositor.CreateGeometricClip(_geometry);
    }

    /// <summary>起点の大きさから、パネルの大きさへ広げる。</summary>
    private void Grow()
    {
        if (_closing || _geometry is null || _surface is null)
        {
            return;
        }

        _opened = true;

        // 開く前の測り方では、テンプレートが当たる前の入力欄などが小さく測られる。並べ終えた大きさで置き直す
        var laidOut = new Size(_panel.ActualWidth, _panel.ActualHeight);
        Place(laidOut);
        _panel.UpdateLayout();
        SetToOrigin(_geometry);
        if (_surfaceFill is not null)
        {
            var surface = _surfaceColor.Background switch
            {
                AcrylicBrush acrylic => acrylic.FallbackColor,
                SolidColorBrush solid => solid.Color,
                _ => _surfaceFill.Color,
            };

            // ボタンの形の起点からは、開く直前のボタンの見た目（ポインターなら載せた塗りと濃い枠）から面の塗りと枠へ移し、
            // ボタンがそのまま面になったように見せる
            if (Trigger is not null && _surfaceStroke is not null)
            {
                _surfaceFill.Color = Landing(_openLook.Fill, "Morph.Surface");
                _surfaceStroke.Color = Landing(_openLook.Stroke, "Morph.Stroke");
                Motion.EaseTo(_surfaceFill, surface, SurfaceFade);
                Motion.EaseTo(_surfaceStroke, ColorOf("Morph.Stroke"), SurfaceFade);
            }
            else
            {
                _surfaceFill.Color = surface;
            }
        }

        _surface.Opacity = 1;
        var size = new Vector2((float)_panel.ActualWidth, (float)_panel.ActualHeight);
        GrowSpring = Motion.Grow.ForDistance(Travel(size));

        // 受けは、面が育ち切るまでと、ダブルクリックの時間の長いほうで外す
        var guard = TimeSpan.FromMilliseconds(Math.Max(new Windows.UI.ViewManagement.UISettings().DoubleClickTime, GrowSpring.VisualDuration * 1000));
        _guardTimer = _root.DispatcherQueue.After(guard, () => _root.Children.Remove(_shield));

        // 起点がボタンの形なら、起点そのものが面に変わったように見せる。起点は隠し（後ろに枠や値が残らないように）、
        // 起点の中の文字などは、起点の位置からパネルの中の位置へ運ぶ
        if (Trigger is { } trigger)
        {
            HideTrigger(trigger.Element);
            _hidesTrigger = true;
        }

        foreach (var (element, target, fade) in _carried)
        {
            var carried = Motion.VisualOf(element);
            carried.Properties.InsertVector3("Translation", OffsetTo(element, target));
            Motion.SpringTo(carried, "Translation", Vector3.Zero, GrowSpring);

            // 起点の写しは薄れ、姿の違う値は現れて、運ばれるあいだに入れ替わる
            if (fade == CarryFade.Out)
            {
                carried.Opacity = 1;
                Motion.EaseTo(carried, "Opacity", 0f, SurfaceFade);
            }
            else if (fade == CarryFade.In)
            {
                Motion.EaseTo(carried, "Opacity", 1f, SurfaceFade, Motion.EnterEasing(carried.Compositor));
            }
        }

        Motion.SpringTo(_geometry, "Offset", Vector2.Zero, GrowSpring);
        Motion.SpringTo(_geometry, "Size", size, GrowSpring);
        Motion.SpringTo(_geometry, "CornerRadius", new Vector2((float)RadiusOf("Radius.Overlay")), GrowSpring);
        if (_shadow is not null)
        {
            Motion.EaseTo(_shadow, "Opacity", 1f, ShadowDuration, Motion.EnterEasing(_shadow.Compositor));
        }

        for (int i = 0; i < Faces.Count; i++)
        {
            Motion.Reveal(Faces[i], new Vector3(0, -4, 0), FaceInDuration, FaceInDelay + FaceStagger * Math.Min(i, 4));
        }

        Grown?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>動かさずにその場で消す（同じ起点から新しい面が開くとき）。</summary>
    private void Vanish()
    {
        if (!_closing)
        {
            // 使う側は Dismissed で Close を呼び、結果を「選ばずに閉じた」にする
            Dismissed?.Invoke(this, EventArgs.Empty);
        }

        if (!_finished)
        {
            Finish(keyboard: false);
        }
    }

    private void Finish(bool keyboard)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        if (Trigger is { } own && OpenOnTrigger.TryGetValue(own.Element, out var current) && current == this)
        {
            OpenOnTrigger.Remove(own.Element);
        }

        _guardTimer?.Stop();
        _finishTimer?.Stop();
        _meltTimer?.Stop();
        _anchor.XamlRoot.Changed -= OnXamlRootChanged;
        _popup.IsOpen = false;
        if (Trigger is { } trigger && _hidesTrigger)
        {
            ShowTrigger(trigger.Element);
            _hidesTrigger = false;
        }

        if (!_focusRestored)
        {
            RestoreFocus(keyboard);
        }
    }

    private void RestoreFocus(bool keyboard)
    {
        var state = keyboard ? FocusState.Keyboard : FocusState.Programmatic;
        if (_restoreFocus is Control { IsLoaded: true } control)
        {
            control.Focus(state);
        }
        else if (_anchor is Control { IsLoaded: true } anchor)
        {
            anchor.Focus(state);
        }
        else if (keyboard && FocusManager.GetFocusedElement(_anchor.XamlRoot) is Control current)
        {
            // 戻り先がなくなっていれば（値を変えて行が作り直されたなど）、閉じるあいだに呼び出し側が移したフォーカスに枠を付ける
            current.Focus(FocusState.Keyboard);
        }
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        // ウィンドウの大きさが変わったら、位置を合わせ直さずに閉じる
        if (sender.Size.Width != _root.Width || sender.Size.Height != _root.Height)
        {
            _opened = false;
            Dismissed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static void HideTrigger(FrameworkElement element)
    {
        HiddenTriggers[element] = HiddenTriggers.TryGetValue(element, out var hidden)
            ? (hidden.Opacity, hidden.Count + 1)
            : (element.Opacity, 1);
        element.Opacity = 0;
    }

    private static void ShowTrigger(FrameworkElement element)
    {
        if (!HiddenTriggers.TryGetValue(element, out var hidden))
        {
            return;
        }

        if (hidden.Count > 1)
        {
            HiddenTriggers[element] = (hidden.Opacity, hidden.Count - 1);
            return;
        }

        HiddenTriggers.Remove(element);
        element.Opacity = hidden.Opacity;
    }

    private static Windows.UI.Color ColorOf(string key) => ((SolidColorBrush)ThemeResources.Brush(key)).Color;

    /// <summary>
    /// 着地する色。透明（塗りや枠のない起点）へは、面の色のまま不透明度だけを 0 にする
    /// （透明の既定値は黒のため、そのまま移すと途中で濁る）。
    /// </summary>
    private static Windows.UI.Color Landing(Windows.UI.Color target, string surfaceKey)
    {
        if (target.A > 0)
        {
            return target;
        }

        var surface = ColorOf(surfaceKey);
        return Windows.UI.Color.FromArgb(0, surface.R, surface.G, surface.B);
    }

    /// <summary>
    /// 起点のいまの見た目（塗りと枠の色）。塗りや枠のないものは透明。
    /// 育つ面の起点のボタン（MorphSelect.Trigger とその派生）がポインターを載せた状態なら、その見た目
    /// （Morph.TriggerHover と Morph.StrokeHover）にする（Background・BorderBrush は状態で変わらないため）。
    /// </summary>
    private static (Windows.UI.Color Fill, Windows.UI.Color Stroke) LookOf(FrameworkElement element)
    {
        if (element is Button { Style: { } style } button && IsBasedOn(style, "MorphSelect.Trigger")
            && CommonState(button) is "PointerOver" or "Pressed")
        {
            return (ColorOf("Morph.TriggerHover"), ColorOf("Morph.StrokeHover"));
        }

        var (fill, stroke, thickness) = element switch
        {
            Control control => (control.Background, control.BorderBrush, control.BorderThickness.Left),
            Border border => (border.Background, border.BorderBrush, border.BorderThickness.Left),
            _ => (null, null, 0),
        };
        return (Solid(fill), thickness > 0 ? Solid(stroke) : default);

        static Windows.UI.Color Solid(Brush? brush) => brush is SolidColorBrush solid
            ? Windows.UI.Color.FromArgb((byte)(solid.Color.A * solid.Opacity), solid.Color.R, solid.Color.G, solid.Color.B)
            : default;
    }

    /// <summary>ボタンのいまの状態（Normal・PointerOver・Pressed・Disabled）。</summary>
    private static string? CommonState(Control control) =>
        VisualTreeHelper.GetChildrenCount(control) > 0 && VisualTreeHelper.GetChild(control, 0) is FrameworkElement root
            ? VisualStateManager.GetVisualStateGroups(root).FirstOrDefault(g => g.Name == "CommonStates")?.CurrentState?.Name
            : null;

    private static bool IsBasedOn(Style style, string key)
    {
        var target = AppResources.Style(key);
        for (var s = style; s is not null; s = s.BasedOn)
        {
            if (ReferenceEquals(s, target))
            {
                return true;
            }
        }

        return false;
    }

    private static double RadiusOf(string key) => (AppResources.CornerRadius(key)).TopLeft;
}
