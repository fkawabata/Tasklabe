using System.Globalization;

namespace Tasklabe.Core.Calendar;

/// <summary>
/// 稼働日の決め方（要件 F-CAL-01）。休む曜日、日本の祝日を休むか、独自の休日を持つ。
/// 既定（<see cref="Standard"/>）は土日と日本の祝日を休日とする。
/// </summary>
public sealed record WorkCalendarRules(IReadOnlySet<DayOfWeek> DaysOff, bool JapaneseHolidaysOff, IReadOnlySet<DateOnly> ExtraHolidays)
{
    public static WorkCalendarRules Standard { get; } =
        new(new HashSet<DayOfWeek> { DayOfWeek.Saturday, DayOfWeek.Sunday }, true, new HashSet<DateOnly>());

    public bool IsHoliday(DateOnly date) =>
        DaysOff.Contains(date.DayOfWeek) || (JapaneseHolidaysOff && JapaneseHolidays.IsHoliday(date)) || ExtraHolidays.Contains(date);

    /// <summary>毎週どこかが稼働日か（すべての曜日を休みにすると、稼働日を探せなくなるため）。</summary>
    public bool HasWorkingWeekday => DaysOff.Count < 7;

    public bool IsStandard =>
        DaysOff.SetEquals(Standard.DaysOff) && JapaneseHolidaysOff && ExtraHolidays.Count == 0;

    /// <summary>設定ファイルに保存する形（例: "off=Saturday,Sunday;holidays=1;extra=2026-12-29,2026-12-30"）。</summary>
    public override string ToString() =>
        $"off={string.Join(',', DaysOff.Order())};holidays={(JapaneseHolidaysOff ? 1 : 0)};"
        + $"extra={string.Join(',', ExtraHolidays.Order().Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))}";

    /// <summary>保存した形から読む。読めない項目は既定値とする。</summary>
    public static WorkCalendarRules Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Standard;
        }

        var daysOff = new HashSet<DayOfWeek>(Standard.DaysOff);
        bool holidays = true;
        var extra = new HashSet<DateOnly>();
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2)
            {
                continue;
            }

            var values = pair[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            switch (pair[0])
            {
                case "off":
                    daysOff = [.. values.Select(v => Enum.TryParse<DayOfWeek>(v, out var d) && Enum.IsDefined(d) ? (DayOfWeek?)d : null)
                        .OfType<DayOfWeek>()];
                    break;
                case "holidays":
                    holidays = pair[1] != "0";
                    break;
                case "extra":
                    extra = [.. values.Select(v => DateOnly.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? (DateOnly?)d : null)
                        .OfType<DateOnly>()];
                    break;
            }
        }

        var rules = new WorkCalendarRules(daysOff, holidays, extra);
        return rules.HasWorkingWeekday ? rules : Standard;
    }
}

/// <summary>
/// 稼働日の暦（要件 F-CAL-02）。いま表示しているプロジェクトの決め方（<see cref="Current"/>）に従う。
/// 画面はプロジェクトを開くときに <see cref="Current"/> を切り替える。
/// </summary>
public static class WorkCalendar
{
    private static WorkCalendarRules _current = WorkCalendarRules.Standard;

    /// <summary>いま使う稼働日の決め方。</summary>
    public static WorkCalendarRules Current
    {
        get => _current;
        set => _current = value is { HasWorkingWeekday: true } ? value : WorkCalendarRules.Standard;
    }

    public static bool IsHoliday(DateOnly date) => Current.IsHoliday(date);

    public static bool IsWorkingDay(DateOnly date) => !IsHoliday(date);

    /// <summary>from から to まで（両端を含む）の稼働日数。to が from より前なら 0。</summary>
    public static int CountWorkingDays(DateOnly from, DateOnly to)
    {
        int count = 0;
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (IsWorkingDay(d))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>from 以降（from を含む）で n 番目の稼働日。n が 1 未満なら from。</summary>
    public static DateOnly NthWorkingDay(DateOnly from, int n)
    {
        if (n < 1)
        {
            return from;
        }

        var d = from;
        while (true)
        {
            if (IsWorkingDay(d) && --n == 0)
            {
                return d;
            }

            d = d.AddDays(1);
        }
    }
}
