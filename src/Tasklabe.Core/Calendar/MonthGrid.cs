namespace Tasklabe.Core.Calendar;

/// <summary>
/// 週ごとの範囲の帯。<see cref="Span"/> が 0 の帯は、範囲が伸びてくる側の端（<see cref="Column"/> の左端）に縮めて置く。
/// </summary>
/// <param name="Column">帯の始まりの列（0〜6。縮めた帯では 7 もありうる）。</param>
/// <param name="Span">帯が覆う日数。</param>
public readonly record struct WeekSegment(int Column, int Span);

/// <summary>
/// 1 か月分の暦の並び（UI デザイン設計書 3.4.3 節）。6 週 × 7 日のマスに並べ、月の外の日は空けておく。
/// </summary>
public static class MonthGrid
{
    public const int Rows = 6;

    /// <summary>左上のマスの日付（月の 1 日を含む週の、週の始まりの日）。</summary>
    public static DateOnly FirstCell(DateOnly month, DayOfWeek weekStart)
    {
        var first = new DateOnly(month.Year, month.Month, 1);
        return first.AddDays(-(((int)first.DayOfWeek - (int)weekStart + 7) % 7));
    }

    /// <summary>日付のマスの位置。</summary>
    public static (int Row, int Column) CellOf(DateOnly date, DateOnly firstCell)
    {
        int index = date.DayNumber - firstCell.DayNumber;
        return (index / 7, index % 7);
    }

    /// <summary>
    /// 範囲の帯を週ごとに求める。月の日を含まない週は null。
    /// 範囲にかからない週は、範囲のある側の端へ幅 0 で縮めた帯にし、範囲が伸びてきたときに端から伸びて見えるようにする。
    /// </summary>
    public static WeekSegment?[] Segments(DateOnly month, DayOfWeek weekStart, DateOnly start, DateOnly end)
    {
        var firstCell = FirstCell(month, weekStart);
        var monthFirst = new DateOnly(month.Year, month.Month, 1);
        var monthLast = monthFirst.AddMonths(1).AddDays(-1);
        var segments = new WeekSegment?[Rows];
        for (int row = 0; row < Rows; row++)
        {
            var rowFirst = Max(firstCell.AddDays(row * 7), monthFirst);
            var rowLast = Min(firstCell.AddDays(row * 7 + 6), monthLast);
            if (rowFirst > rowLast)
            {
                continue;
            }

            var from = Max(start, rowFirst);
            var to = Min(end, rowLast);
            int Column(DateOnly date) => CellOf(date, firstCell).Column;
            segments[row] = from <= to ? new WeekSegment(Column(from), Column(to) - Column(from) + 1)
                : rowLast < start ? new WeekSegment(Column(rowLast) + 1, 0)
                : new WeekSegment(Column(rowFirst), 0);
        }

        return segments;
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;
}
