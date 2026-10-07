using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Tasklabe.Animation;
using Tasklabe.App.Services;
using Tasklabe.Core.Calendar;

namespace Tasklabe.App.Controls;

/// <summary>
/// 日付範囲のピッカーの、1 か月分の暦（UI デザイン設計書 3.4.3 節）。
/// 日付の下に、週ごとの範囲の帯と、範囲の両端の丸を Composition の図形で敷く。
/// 帯は範囲が伸びてくる側の端から伸び縮みし、両端の丸は日から日へ滑る。範囲が月の外へ出た端の丸は縮んで消える。
/// </summary>
internal sealed partial class RangeMonth : Grid
{
    /// <summary>1 日のマスの大きさ。</summary>
    public const double Cell = 36;

    /// <summary>帯と丸を、マスの内側へ寄せる幅。</summary>
    private const float Inset = 3;

    private static readonly TimeSpan HideDuration = TimeSpan.FromMilliseconds(120);

    /// <summary>端になった日の文字を、滑ってくる丸がおおむね着いてから白くする。先に白くすると丸が来るまで読めない。</summary>
    private static readonly TimeSpan EdgeTextDelay = TimeSpan.FromMilliseconds(150);

    private readonly DateOnly _month;
    private readonly DayOfWeek _weekStart;
    private readonly DateOnly _firstCell;
    private readonly Dictionary<DateOnly, Button> _days = [];
    private readonly Dictionary<DateOnly, TextBlock> _numbers = [];
    private readonly Dictionary<DateOnly, Ellipse> _todayDots = [];
    private readonly CompositionRoundedRectangleGeometry[] _bars = new CompositionRoundedRectangleGeometry[MonthGrid.Rows];
    private readonly bool[] _barShown = new bool[MonthGrid.Rows];
    private readonly CompositionSpriteShape _startThumb;
    private readonly CompositionSpriteShape _endThumb;
    private readonly Brush _text;
    private readonly Brush _onAccent;
    private readonly Brush _accent;
    private bool _startShown;
    private bool _endShown;
    private DateOnly? _edgeStart;
    private DateOnly? _edgeEnd;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _edgeTimer;

    /// <param name="pick">日を押した。</param>
    /// <param name="hover">ポインターが日の上に来た。</param>
    /// <param name="key">日の上でキーを押した（矢印キーでの移動など）。</param>
    public RangeMonth(DateOnly month, DayOfWeek weekStart, DateOnly today,
        Action<DateOnly> pick, Action<DateOnly> hover, Action<DateOnly, KeyRoutedEventArgs> key)
    {
        _month = new DateOnly(month.Year, month.Month, 1);
        _weekStart = weekStart;
        _firstCell = MonthGrid.FirstCell(_month, weekStart);
        _text = ThemeResources.Brush("TextFillColorPrimaryBrush");
        _onAccent = ThemeResources.Brush("TextOnAccentFillColorPrimaryBrush");
        _accent = ThemeResources.Brush("AccentFillColorDefaultBrush");

        Width = Cell * 7;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(Cell * MonthGrid.Rows) });

        var title = new TextBlock
        {
            Text = _month.ToString("yyyy年M月", DateText.Japanese),
            Style = AppResources.Style("Text.BodyStrong"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Children.Add(title);

        var weekdays = new Grid();
        SetRow(weekdays, 1);
        for (int col = 0; col < 7; col++)
        {
            weekdays.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Cell) });
            var label = new TextBlock
            {
                Text = _firstCell.AddDays(col).ToString("ddd", DateText.Japanese),
                Style = AppResources.Style("Text.Caption"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            SetColumn(label, col);
            weekdays.Children.Add(label);
        }

        Children.Add(weekdays);

        // 帯と丸を描く層。日付のボタンの下に重ねる
        var layer = new Grid { IsHitTestVisible = false };
        SetRow(layer, 2);
        Children.Add(layer);
        var compositor = ElementCompositionPreview.GetElementVisual(layer).Compositor;
        var shapes = compositor.CreateShapeVisual();
        shapes.Size = new Vector2((float)Cell * 7, (float)Cell * MonthGrid.Rows);
        var accent = ((SolidColorBrush)_accent).Color;
        var barBrush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0x33, accent.R, accent.G, accent.B));
        float pill = ((float)Cell - Inset * 2) / 2;
        for (int row = 0; row < MonthGrid.Rows; row++)
        {
            var bar = compositor.CreateRoundedRectangleGeometry();
            bar.CornerRadius = new Vector2(pill);
            bar.Offset = new Vector2(0, row * (float)Cell + Inset);
            bar.Size = new Vector2(0, (float)Cell - Inset * 2);
            _bars[row] = bar;
            var shape = compositor.CreateSpriteShape(bar);
            shape.FillBrush = barBrush;
            shapes.Shapes.Add(shape);
        }

        _startThumb = Thumb(compositor, accent);
        _endThumb = Thumb(compositor, accent);
        shapes.Shapes.Add(_startThumb);
        shapes.Shapes.Add(_endThumb);
        ElementCompositionPreview.SetElementChildVisual(layer, shapes);

        // 日付。月の外のマスは空けておく
        var days = new Grid();
        SetRow(days, 2);
        for (int i = 0; i < 7; i++)
        {
            days.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Cell) });
        }

        for (int i = 0; i < MonthGrid.Rows; i++)
        {
            days.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Cell) });
        }

        var dayStyle = AppResources.Style("RangeCalendar.Day");
        for (var date = _month; date.Month == _month.Month; date = date.AddDays(1))
        {
            var day = date;
            var number = new TextBlock { Text = day.Day.ToString(DateText.Japanese), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var content = new Grid { Width = Cell, Height = Cell, Children = { number } };
            if (day == today)
            {
                var dot = new Ellipse { Width = 4, Height = 4, Fill = _accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 5) };
                content.Children.Add(dot);
                _todayDots[day] = dot;
            }

            var button = new Button { Style = dayStyle, Content = content, CornerRadius = new CornerRadius(Cell / 2) };
            AutomationProperties.SetName(button, day.ToString("yyyy年M月d日 dddd", DateText.Japanese) + (day == today ? "（今日）" : ""));
            button.Click += (_, _) => pick(day);
            button.PointerEntered += (_, _) => hover(day);
            button.KeyDown += (_, e) => key(day, e);
            var (row, col) = MonthGrid.CellOf(day, _firstCell);
            SetRow(button, row);
            SetColumn(button, col);
            days.Children.Add(button);
            _days[day] = button;
            _numbers[day] = number;
        }

        // Tab では暦に 1 度だけ入り、日のあいだは矢印キーで移る
        days.TabFocusNavigation = KeyboardNavigationMode.Once;
        Children.Add(days);
    }

    public DateOnly Month => _month;

    /// <summary>この月にある日のボタン。</summary>
    public Button? DayButton(DateOnly date) => _days.GetValueOrDefault(date);

    /// <summary>範囲を示す。両方の日付があるときだけ帯を引き、片方だけのときはその端の丸だけを置く。</summary>
    public void ShowRange(DateOnly? start, DateOnly? end, bool animate)
    {
        var segments = start is { } s && end is { } e
            ? MonthGrid.Segments(_month, _weekStart, s, e)
            : new WeekSegment?[MonthGrid.Rows];
        for (int row = 0; row < MonthGrid.Rows; row++)
        {
            PlaceBar(row, segments[row], animate);
        }

        var startCell = start is { } a && Contains(a) ? MonthGrid.CellOf(a, _firstCell) : ((int, int)?)null;
        var endCell = end is { } b && Contains(b) && b != start ? MonthGrid.CellOf(b, _firstCell) : ((int, int)?)null;
        PlaceThumb(_startThumb, startCell, ref _startShown, animate);
        PlaceThumb(_endThumb, endCell, ref _endShown, animate);

        _edgeStart = start;
        _edgeEnd = end;
        PaintEdges(lateToo: !animate || !Motion.IsEnabled);
        if (animate && Motion.IsEnabled)
        {
            _edgeTimer?.Stop();
            _edgeTimer = DispatcherQueue.After(EdgeTextDelay, () => PaintEdges(lateToo: true));
        }
    }

    /// <summary>端の日の文字と今日の印を、塗りの上の色にする。端でなくなった日はすぐ戻す。</summary>
    /// <param name="lateToo">端になったばかりの日も塗る（丸が着いたあと）。</param>
    private void PaintEdges(bool lateToo)
    {
        foreach (var (date, number) in _numbers)
        {
            bool edge = date == _edgeStart || date == _edgeEnd;
            if (edge && !lateToo && number.Foreground != _onAccent)
            {
                continue;
            }

            number.Foreground = edge ? _onAccent : _text;
            if (_todayDots.TryGetValue(date, out var dot))
            {
                dot.Fill = edge ? _onAccent : _accent;
            }
        }
    }

    private bool Contains(DateOnly date) => date.Year == _month.Year && date.Month == _month.Month;

    private void PlaceBar(int row, WeekSegment? segment, bool animate)
    {
        var bar = _bars[row];
        float y = row * (float)Cell + Inset;
        if (segment is not { } s)
        {
            bar.StopAnimation("Offset");
            bar.StopAnimation("Size");
            bar.Size = new Vector2(0, bar.Size.Y);
            _barShown[row] = false;
            return;
        }

        // 幅のある帯はマスの内側へ寄せる。幅 0 の帯は、伸びてくる側の端に置く
        var offset = new Vector2(s.Column * (float)Cell + (s.Span > 0 ? Inset : 0), y);
        var size = new Vector2(s.Span > 0 ? s.Span * (float)Cell - Inset * 2 : 0, (float)Cell - Inset * 2);
        bool stretch = animate && _barShown[row];
        Motion.SpringTo(bar, "Offset", offset, Motion.Stretch, animate: stretch);
        Motion.SpringTo(bar, "Size", size, Motion.Stretch, animate: stretch);
        _barShown[row] = true;
    }

    private static void PlaceThumb(CompositionSpriteShape thumb, (int Row, int Column)? cell, ref bool shown, bool animate)
    {
        if (cell is not { } c)
        {
            Motion.EaseTo(thumb, "Scale", Vector2.Zero, shown && animate ? HideDuration : TimeSpan.Zero);
            shown = false;
            return;
        }

        var offset = new Vector2(c.Column * (float)Cell, c.Row * (float)Cell);
        Motion.SpringTo(thumb, "Offset", offset, Motion.Glide, animate: shown && animate);
        if (!shown && animate)
        {
            // 初めて出る丸は、少し小さい大きさから膨らむ
            thumb.Scale = new Vector2(0.8f);
        }

        Motion.SpringTo(thumb, "Scale", Vector2.One, Motion.Glide, animate: animate);
        shown = true;
    }

    private static CompositionSpriteShape Thumb(Compositor compositor, Windows.UI.Color color)
    {
        var circle = compositor.CreateEllipseGeometry();
        float radius = (float)Cell / 2 - Inset;
        circle.Radius = new Vector2(radius);
        circle.Center = new Vector2((float)Cell / 2);
        var thumb = compositor.CreateSpriteShape(circle);
        thumb.FillBrush = compositor.CreateColorBrush(color);
        thumb.CenterPoint = new Vector2((float)Cell / 2);
        thumb.Scale = Vector2.Zero;
        return thumb;
    }
}
