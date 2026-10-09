using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Tasklabe.Animation;
using Tasklabe.App.Services;
using Windows.Foundation;

namespace Tasklabe.App.Controls;

/// <summary>
/// 横に並べたボタンの列を、スクロールバーなしで左右に動かす入れ物（タスクの追加の属性ボタン。UI デザイン設計書 3.5.1 節）。
/// はみ出している側の端をフェードさせて続きがあることを示し、動かせるときだけポインターを載せた範囲に色を付けて、つかめる範囲を示す。
/// ホイールは縦に回しても横に動かし、Tab で見えていないボタンへ移ると、そのボタンが見える分だけずらす。
/// ボタンの上からでも、つかんで左右に動かせる（動かしたときは、そのボタンを押したことにしない）。
/// ScrollViewer はホイールを入力のイベントより下で受けて動かし、処理済みにしても止められないため、手で動かせないようにして、ここで動かす。
/// </summary>
public static class HorizontalStrip
{
    /// <summary>ずらしたときに、ボタンの外に残す余白。</summary>
    private const double RevealMargin = 4;

    /// <summary>端のフェードの長さ。端の外に残っている分がこれより短ければ、その分だけにする。</summary>
    private const double FadeLength = 28;

    /// <summary>列のまわりに広げる、つかめる範囲と色を付ける範囲。</summary>
    private static readonly Thickness Reach = new(12, 4, 12, 4);

    private static readonly TimeSpan HoverFade = TimeSpan.FromMilliseconds(160);

    /// <summary>列を入れた入れ物を作る。列のまわりに <see cref="Reach"/> の分だけ広がる（外側の余白へはみ出して置く）。</summary>
    public static FrameworkElement Wrap(FrameworkElement row)
    {
        ArgumentNullException.ThrowIfNull(row);

        row.Margin = Reach;
        var scroller = new ScrollViewer
        {
            Content = row,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
            IsTabStop = false,
        };
        var hover = new Border
        {
            Background = ThemeResources.Brush("SubtleFillColorSecondaryBrush"),
            CornerRadius = AppResources.CornerRadius("Radius.Overlay"),
            IsHitTestVisible = false,
        };
        var start = Fade(HorizontalAlignment.Left);
        var end = Fade(HorizontalAlignment.Right);
        var host = new Grid
        {
            Margin = new Thickness(-Reach.Left, -Reach.Top, -Reach.Right, -Reach.Bottom),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Children = { scroller, start, end, hover },
        };
        Motion.VisualOf(hover).Opacity = 0;

        // はみ出している側の端だけをフェードさせる。長さは、端の外に残っている分に合わせる
        void UpdateFades()
        {
            start.Width = Math.Min(FadeLength, scroller.HorizontalOffset);
            end.Width = Math.Min(FadeLength, Math.Max(0, scroller.ScrollableWidth - scroller.HorizontalOffset));
        }

        scroller.ViewChanged += (_, _) => UpdateFades();
        scroller.SizeChanged += (_, _) => UpdateFades();
        row.SizeChanged += (_, _) => UpdateFades();
        host.ActualThemeChanged += (_, _) =>
        {
            hover.Background = ThemeResources.Brush("SubtleFillColorSecondaryBrush");
            start.Fill = FadeBrush(HorizontalAlignment.Left);
            end.Fill = FadeBrush(HorizontalAlignment.Right);
        };

        // 動かせるときだけ、ポインターを載せた範囲に色を付ける（どこをつかめば列が動くかを示す）
        bool over = false;
        void UpdateHover()
        {
            bool shown = over && scroller.ScrollableWidth > 0;
            Motion.EaseTo(Motion.VisualOf(hover), "Opacity", shown ? 1f : 0f, HoverFade);
        }

        host.PointerEntered += (_, _) =>
        {
            over = true;
            UpdateHover();
        };
        host.PointerExited += (_, _) =>
        {
            over = false;
            UpdateHover();
        };
        scroller.SizeChanged += (_, _) => UpdateHover();

        scroller.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, e) =>
        {
            var properties = e.GetCurrentPoint(scroller).Properties;
            int delta = properties.MouseWheelDelta;
            scroller.ChangeView(scroller.HorizontalOffset + (properties.IsHorizontalMouseWheel ? delta : -delta), null, null);
            e.Handled = true;
        }), true);

        // 中のボタンのフォーカスは、入れ物まで伝わってくる
        scroller.GotFocus += (_, e) =>
        {
            if (e.OriginalSource is FrameworkElement focused && focused != scroller)
            {
                Reveal(scroller, focused);
            }
        };

        new Drag(host, scroller).Attach();
        return host;
    }

    /// <summary>端のフェード。板の面の色から透明へ移る。</summary>
    private static Rectangle Fade(HorizontalAlignment side) => new()
    {
        Width = 0,
        HorizontalAlignment = side,
        Fill = FadeBrush(side),
        IsHitTestVisible = false,
    };

    private static LinearGradientBrush FadeBrush(HorizontalAlignment side)
    {
        var surface = ((SolidColorBrush)ThemeResources.Brush("Morph.Surface")).Color;
        var clear = Windows.UI.Color.FromArgb(0, surface.R, surface.G, surface.B);
        bool left = side == HorizontalAlignment.Left;
        return new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            GradientStops =
            {
                new GradientStop { Color = left ? surface : clear, Offset = 0 },
                new GradientStop { Color = left ? clear : surface, Offset = 1 },
            },
        };
    }

    /// <summary>
    /// 要素が見えていなければ、見える分だけずらす（右の端へ移ったら、1 つ分だけ左へ動く）。端のフェードに掛からないところまでずらす。
    /// Alt + 文字でフォーカスを移してすぐピッカーを開くときに、動いている途中の位置から開かないよう、その場でずらす。
    /// </summary>
    private static void Reveal(ScrollViewer scroller, FrameworkElement element)
    {
        double offset = scroller.HorizontalOffset;
        double left = element.TransformToVisual(scroller).TransformPoint(new Point(0, 0)).X + offset;
        double right = left + element.ActualWidth;
        if (left - Reach.Left < offset)
        {
            scroller.ChangeView(Math.Max(0, left - Reach.Left - RevealMargin), null, null, disableAnimation: true);
        }
        else if (right + Reach.Right > offset + scroller.ViewportWidth)
        {
            scroller.ChangeView(right + Reach.Right + RevealMargin - scroller.ViewportWidth, null, null, disableAnimation: true);
        }
    }

    /// <summary>つかんで左右に動かす。Windows のドラッグを始める距離を超えたら動かし始め、押していたボタンの押下を取り消す。</summary>
    private sealed class Drag(FrameworkElement host, ScrollViewer scroller)
    {
        private Pointer? _pointer;
        private double _startX;
        private double _startOffset;
        private bool _dragging;

        public void Attach()
        {
            host.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPressed), true);
            host.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnMoved), true);
            host.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnReleased), true);
            host.PointerCaptureLost += (_, _) => End();
        }

        private void OnPressed(object sender, PointerRoutedEventArgs e)
        {
            if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && !e.GetCurrentPoint(host).Properties.IsLeftButtonPressed)
            {
                return;
            }

            _pointer = e.Pointer;
            _startX = e.GetCurrentPoint(host).Position.X;
            _startOffset = scroller.HorizontalOffset;
            _dragging = false;

            // 列の範囲の余白（ボタンのないところ）を押したときも、外側（板を動かす操作など）へは伝えない
            e.Handled = true;
        }

        private void OnMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_pointer is null || e.Pointer.PointerId != _pointer.PointerId)
            {
                return;
            }

            double dx = e.GetCurrentPoint(host).Position.X - _startX;
            if (!_dragging)
            {
                // Windows のドラッグを始める距離（物理ピクセル）を、表示の拡大率で DIP に直して比べる
                double threshold = GetSystemMetrics(SmCxDrag) / (host.XamlRoot?.RasterizationScale ?? 1);
                if (Math.Abs(dx) <= threshold || scroller.ScrollableWidth <= 0)
                {
                    return;
                }

                // ボタンが持っている押下を取り上げ、離してもボタンを押したことにしない
                _dragging = host.CapturePointer(e.Pointer);
                if (!_dragging)
                {
                    return;
                }
            }

            scroller.ChangeView(_startOffset - dx, null, null, disableAnimation: true);
            e.Handled = true;
        }

        private void OnReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_dragging)
            {
                host.ReleasePointerCapture(e.Pointer);
                e.Handled = true;
            }

            End();
        }

        private void End()
        {
            _pointer = null;
            _dragging = false;
        }
    }

    private const int SmCxDrag = 68;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
