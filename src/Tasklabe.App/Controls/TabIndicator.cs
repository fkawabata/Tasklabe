using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Tasklabe.Animation;
using Tasklabe.App.Services;

namespace Tasklabe.App.Controls;

/// <summary>
/// タブ（<see cref="SelectorBar"/>）の下線を、選び直したときに前のタブの位置から滑らせる（UI デザイン設計書 2.7 節）。
/// XAML で <c>controls:TabIndicator.Slides="True"</c> を付ける。
/// 標準の下線はタブごとにあり、選んだタブの下でその場に伸びるだけなので、タブごとの下線は幅 0 にして隠し、
/// 同じ形の下線を 1 本だけバーの上に描いて、選んでいるタブの下線の位置へばねで動かす。
/// </summary>
public static partial class TabIndicator
{
    public static readonly DependencyProperty SlidesProperty = DependencyProperty.RegisterAttached(
        "Slides", typeof(bool), typeof(TabIndicator), new PropertyMetadata(false, OnSlidesChanged));

    public static bool GetSlides(SelectorBar bar)
    {
        ArgumentNullException.ThrowIfNull(bar);
        return (bool)bar.GetValue(SlidesProperty);
    }

    public static void SetSlides(SelectorBar bar, bool value)
    {
        ArgumentNullException.ThrowIfNull(bar);
        bar.SetValue(SlidesProperty, value);
    }

    private static void OnSlidesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SelectorBar bar && e.NewValue is true)
        {
            _ = new Slider(bar);
        }
    }

    private sealed class Slider
    {
        /// <summary>標準の下線と同じ大きさ（幅 4 を 4 倍に伸ばしたもの、高さ 3）。</summary>
        private const float LineWidth = 16;
        private const float LineHeight = 3;

        /// <summary>タブの中の下線（標準のテンプレートの部品名）。</summary>
        private const string ItemLine = "PART_SelectionVisual";

        private readonly SelectorBar _bar;
        private readonly ShapeVisual _line;
        private readonly CompositionColorBrush _fill;
        private readonly HashSet<SelectorBarItem> _watched = [];
        private Vector3? _target;

        public Slider(SelectorBar bar)
        {
            _bar = bar;
            var compositor = ElementCompositionPreview.GetElementVisual(bar).Compositor;
            var geometry = compositor.CreateRoundedRectangleGeometry();
            geometry.Size = new Vector2(LineWidth, LineHeight);
            geometry.CornerRadius = new Vector2(LineHeight / 2);
            _fill = compositor.CreateColorBrush();
            var shape = compositor.CreateSpriteShape(geometry);
            shape.FillBrush = _fill;
            _line = compositor.CreateShapeVisual();
            _line.Size = new Vector2(LineWidth, LineHeight);
            _line.Shapes.Add(shape);
            _line.Opacity = 0;
            ElementCompositionPreview.SetElementChildVisual(bar, _line);

            bar.SelectionChanged += (_, _) => Place(animate: true);
            bar.SizeChanged += (_, _) => Place(animate: false);
            bar.ActualThemeChanged += (_, _) => UpdateColor();

            // 添付プロパティは子のタブが入る前に設定されるため、タブはバーが読み込まれてから拾う
            bar.Loaded += (_, _) =>
            {
                foreach (var item in bar.Items)
                {
                    HideItemLine(item);
                    if (_watched.Add(item))
                    {
                        // 名前が変わる（計画 / リスト）、ほかのタブが隠れるなどで位置が変わったら、その場で合わせる
                        item.SizeChanged += (_, _) => Place(animate: false);
                    }
                }

                UpdateColor();
                _target = null;
                Place(animate: false);
            };
        }

        /// <summary>選んでいるタブの下線の位置へ動かす。位置が変わらなければ、動いている途中のばねを止めない。</summary>
        private void Place(bool animate)
        {
            if (_bar.SelectedItem is not { ActualWidth: > 0 } item)
            {
                _line.Opacity = 0;
                _target = null;
                return;
            }

            var at = item.TransformToVisual(_bar).TransformPoint(default);
            var target = new Vector3((float)(at.X + (item.ActualWidth - LineWidth) / 2), (float)(at.Y + item.ActualHeight - LineHeight), 0);
            if (_target == target)
            {
                return;
            }

            Motion.SpringTo(_line, "Offset", target, Motion.Morph, animate: animate && _target is not null);
            _line.Opacity = 1;
            _target = target;
        }

        /// <summary>タブごとの下線を幅 0 にして隠す。高さは残し、タブの高さを変えない。</summary>
        private static void HideItemLine(SelectorBarItem item)
        {
            if (FindLine(item) is { } line)
            {
                line.Width = 0;
            }
        }

        /// <summary>下線の色は、タブごとの下線（テーマに追従する）から写す。</summary>
        private void UpdateColor()
        {
            var line = _bar.Items.Select(FindLine).FirstOrDefault(l => l is not null);
            _fill.Color = (line?.Fill ?? ThemeResources.Brush("AccentFillColorDefaultBrush")) is SolidColorBrush solid
                ? solid.Color
                : _fill.Color;
        }

        private static Rectangle? FindLine(DependencyObject parent) =>
            VisualTree.Descendants<Rectangle>(parent).FirstOrDefault(r => r.Name == ItemLine);
    }
}
