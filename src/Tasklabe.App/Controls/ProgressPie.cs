using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Tasklabe.App.Controls;

/// <summary>
/// 進行中カテゴリの状態アイコン。外周の円と、進捗率に応じた扇形で表す。0 % は中心の点で示す（UI デザイン設計書 2.4 節）。
/// </summary>
public sealed partial class ProgressPie : Grid
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ProgressPie), new PropertyMetadata(0.0, (d, _) => ((ProgressPie)d).Update()));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(ProgressPie), new PropertyMetadata(null, (d, e) =>
        {
            var pie = (ProgressPie)d;
            pie._ring.Stroke = (Brush)e.NewValue;
            pie._pie.Fill = (Brush)e.NewValue;
        }));

    private const double Size = 16;
    private const double Stroke = 1.5;

    private readonly Ellipse _ring = new() { StrokeThickness = Stroke };
    private readonly Path _pie = new();

    public ProgressPie()
    {
        Width = Size;
        Height = Size;
        Children.Add(_ring);
        Children.Add(_pie);
        Update();
    }

    /// <summary>進捗率（0〜100）。</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>外周と扇形の色。テーマに追従させるため ThemeResource で指定する。</summary>
    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    private void Update()
    {
        // 外周の内側に 1px の余白を空けて扇形を描く
        double radius = Size / 2 - Stroke - 1;
        var center = new Point(Size / 2, Size / 2);
        double fraction = Math.Clamp(Value / 100, 0, 1);

        // 0 % でも未着手（空の円）と形で見分けられるよう、中心に点を置く
        if (fraction <= 0)
        {
            _pie.Data = new EllipseGeometry { Center = center, RadiusX = 2.5, RadiusY = 2.5 };
            return;
        }

        if (fraction >= 1)
        {
            _pie.Data = new EllipseGeometry { Center = center, RadiusX = radius, RadiusY = radius };
            return;
        }

        double angle = fraction * 2 * Math.PI;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));

        var figure = new PathFigure { StartPoint = center, IsClosed = true };
        figure.Segments.Add(new LineSegment { Point = start });
        figure.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = new Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = angle > Math.PI,
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        _pie.Data = geometry;
    }
}
