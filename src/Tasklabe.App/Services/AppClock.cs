using System.Globalization;

namespace Tasklabe.App.Services;

/// <summary>
/// アプリが「今日」とする日付。日がたつにつれての進捗や遅れの見え方を確かめるため、Debug ビルドでは
/// 環境変数 TASKLABE_TODAY=yyyy-MM-dd で起動すると、その日を今日として動く（実績の日付もその日で入る）。
/// 起動時の差を保つため、日付が変われば差し替えた今日も 1 日進む。
/// </summary>
public static class AppClock
{
    /// <summary>実際の今日から差し替えた今日までの日数。差し替えていなければ 0。</summary>
    private static readonly int OffsetDays = ReadOffset();

    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Now).AddDays(OffsetDays);

    /// <summary>今日を差し替えて動いているか。</summary>
    public static bool IsOverridden => OffsetDays != 0;

    private static int ReadOffset()
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable("TASKLABE_TODAY") is { } value
            && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var today))
        {
            int offset = today.DayNumber - DateOnly.FromDateTime(DateTime.Now).DayNumber;
            AppLog.Info($"TASKLABE_TODAY により {today:yyyy-MM-dd} を今日として動かす（実際の今日との差 {offset} 日）");
            return offset;
        }
#endif
        return 0;
    }
}
