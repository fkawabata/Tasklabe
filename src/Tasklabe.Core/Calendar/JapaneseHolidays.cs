using System.Collections.Concurrent;

namespace Tasklabe.Core.Calendar;

/// <summary>
/// 日本の祝日（国民の祝日に関する法律）。2007 年以降の規則と、特例の年（2019〜2021 年）に対応する。
/// 春分・秋分の日は 1980〜2099 年に有効な近似式で求める。
/// </summary>
public static class JapaneseHolidays
{
    private static readonly ConcurrentDictionary<int, IReadOnlyDictionary<DateOnly, string>> Cache = new();

    public static bool IsHoliday(DateOnly date) => ForYear(date.Year).ContainsKey(date);

    public static string? NameOf(DateOnly date) => ForYear(date.Year).GetValueOrDefault(date);

    /// <summary>その年の祝日（振替休日・国民の休日を含む）。</summary>
    public static IReadOnlyDictionary<DateOnly, string> ForYear(int year) => Cache.GetOrAdd(year, Compute);

    private static IReadOnlyDictionary<DateOnly, string> Compute(int year)
    {
        var days = new SortedDictionary<DateOnly, string>();
        void Add(int month, int day, string name) => days[new DateOnly(year, month, day)] = name;
        void AddMonday(int month, int week, string name) => days[NthMonday(year, month, week)] = name;

        Add(1, 1, "元日");
        AddMonday(1, 2, "成人の日");
        Add(2, 11, "建国記念の日");
        if (year >= 2020)
        {
            Add(2, 23, "天皇誕生日");
        }

        Add(3, EquinoxDay(year, 20.8431), "春分の日");
        Add(4, 29, "昭和の日");
        Add(5, 3, "憲法記念日");
        Add(5, 4, "みどりの日");
        Add(5, 5, "こどもの日");

        switch (year)
        {
            case 2020:
                Add(7, 23, "海の日");
                Add(7, 24, "スポーツの日");
                Add(8, 10, "山の日");
                break;
            case 2021:
                Add(7, 22, "海の日");
                Add(7, 23, "スポーツの日");
                Add(8, 8, "山の日");
                break;
            default:
                AddMonday(7, 3, "海の日");
                if (year >= 2016)
                {
                    Add(8, 11, "山の日");
                }

                AddMonday(10, 2, year >= 2020 ? "スポーツの日" : "体育の日");
                break;
        }

        AddMonday(9, 3, "敬老の日");
        Add(9, EquinoxDay(year, 23.2488), "秋分の日");
        Add(11, 3, "文化の日");
        Add(11, 23, "勤労感謝の日");

        if (year <= 2018)
        {
            Add(12, 23, "天皇誕生日");
        }

        if (year == 2019)
        {
            Add(4, 30, "国民の休日");
            Add(5, 1, "天皇の即位の日");
            Add(5, 2, "国民の休日");
            Add(10, 22, "即位礼正殿の儀の行われる日");
        }

        // 国民の休日: 前日と翌日が祝日である平日（日曜以外）
        var holidays = days.Keys.ToList();
        foreach (var d in holidays)
        {
            var next = d.AddDays(1);
            if (days.ContainsKey(d.AddDays(2)) && !days.ContainsKey(next) && next.DayOfWeek != DayOfWeek.Sunday)
            {
                days[next] = "国民の休日";
            }
        }

        // 振替休日: 日曜の祝日の後で、最も近い祝日でない日
        foreach (var d in days.Keys.Where(d => d.DayOfWeek == DayOfWeek.Sunday).ToList())
        {
            var substitute = d.AddDays(1);
            while (days.ContainsKey(substitute))
            {
                substitute = substitute.AddDays(1);
            }

            if (substitute.Year == year)
            {
                days[substitute] = "振替休日";
            }
        }

        return new Dictionary<DateOnly, string>(days);
    }

    private static DateOnly NthMonday(int year, int month, int week)
    {
        var first = new DateOnly(year, month, 1);
        int offset = ((int)DayOfWeek.Monday - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(offset + (week - 1) * 7);
    }

    private static int EquinoxDay(int year, double constant) =>
        (int)Math.Floor(constant + 0.242194 * (year - 1980) - Math.Floor((year - 1980) / 4.0));
}
