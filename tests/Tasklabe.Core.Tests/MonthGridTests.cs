using Tasklabe.Core.Calendar;

namespace Tasklabe.Core.Tests;

public class MonthGridTests
{
    private static readonly DateOnly October = new(2026, 10, 1); // 木曜日に始まり、土曜日に終わる

    [Fact]
    public void The_grid_starts_on_the_week_start_before_the_first_day()
    {
        Assert.Equal(new DateOnly(2026, 9, 28), MonthGrid.FirstCell(October, DayOfWeek.Monday));
        Assert.Equal(new DateOnly(2026, 9, 27), MonthGrid.FirstCell(October, DayOfWeek.Sunday));
        Assert.Equal(new DateOnly(2026, 6, 1), MonthGrid.FirstCell(new DateOnly(2026, 6, 15), DayOfWeek.Monday));
    }

    [Fact]
    public void Cells_are_counted_from_the_first_cell()
    {
        var first = MonthGrid.FirstCell(October, DayOfWeek.Monday);
        Assert.Equal((0, 3), MonthGrid.CellOf(new DateOnly(2026, 10, 1), first));
        Assert.Equal((1, 0), MonthGrid.CellOf(new DateOnly(2026, 10, 5), first));
        Assert.Equal((4, 5), MonthGrid.CellOf(new DateOnly(2026, 10, 31), first));
    }

    [Fact]
    public void A_range_is_split_into_one_bar_per_week()
    {
        var segments = MonthGrid.Segments(October, DayOfWeek.Monday, new DateOnly(2026, 10, 7), new DateOnly(2026, 10, 14));

        Assert.Equal(new WeekSegment(7, 0), segments[0]); // 範囲より前の週は、範囲の側（右端）に縮める
        Assert.Equal(new WeekSegment(2, 5), segments[1]); // 10/7(水)〜10/11(日)
        Assert.Equal(new WeekSegment(0, 3), segments[2]); // 10/12(月)〜10/14(水)
        Assert.Equal(new WeekSegment(0, 0), segments[3]); // 範囲より後の週は、範囲の側（左端）に縮める
        Assert.Equal(new WeekSegment(0, 0), segments[4]);
        Assert.Null(segments[5]); // 月の日を含まない週
    }

    [Fact]
    public void Bars_stop_at_the_edges_of_the_month()
    {
        var segments = MonthGrid.Segments(October, DayOfWeek.Monday, new DateOnly(2026, 9, 20), new DateOnly(2026, 11, 3));

        Assert.Equal(new WeekSegment(3, 4), segments[0]); // 10/1(木)から
        Assert.Equal(new WeekSegment(0, 6), segments[4]); // 10/31(土)まで
    }
}
