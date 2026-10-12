using System.Collections.Concurrent;
using Tasklabe.Core.Resources;

namespace Tasklabe.Core.Calendar;

/// <summary>
/// 日本の祝日（国民の祝日に関する法律）。2007 年以降の規則と、特例の年（2019〜2021 年）に対応する。
/// 春分・秋分の日は 1980〜2099 年に有効な近似式で求める。
/// </summary>
public static class JapaneseHolidays
{
    private static readonly ConcurrentDictionary<int, IReadOnlyDictionary<DateOnly, string>> Cache = new();

    public static bool IsHoliday(DateOnly date) => Ids(date.Year).ContainsKey(date);

    /// <summary>祝日の名前（表示の言語の文言）。祝日でなければ null。</summary>
    public static string? NameOf(DateOnly date) => Ids(date.Year).GetValueOrDefault(date) is { } id ? Strings.Holiday(id) : null;

    /// <summary>その年の祝日（振替休日・国民の休日を含む）と名前。</summary>
    public static IReadOnlyDictionary<DateOnly, string> ForYear(int year) => Ids(year).ToDictionary(p => p.Key, p => Strings.Holiday(p.Value));

    /// <summary>その年の祝日と、名前の文言を引く ID。名前は言語で変わるため、ID で覚える。</summary>
    private static IReadOnlyDictionary<DateOnly, string> Ids(int year) => Cache.GetOrAdd(year, Compute);

    private static IReadOnlyDictionary<DateOnly, string> Compute(int year)
    {
        var days = new SortedDictionary<DateOnly, string>();
        void Add(int month, int day, string id) => days[new DateOnly(year, month, day)] = id;
        void AddMonday(int month, int week, string id) => days[NthMonday(year, month, week)] = id;

        Add(1, 1, "NewYear");
        AddMonday(1, 2, "ComingOfAge");
        Add(2, 11, "Foundation");
        if (year >= 2020)
        {
            Add(2, 23, "EmperorsBirthday");
        }

        Add(3, EquinoxDay(year, 20.8431), "VernalEquinox");
        Add(4, 29, "Showa");
        Add(5, 3, "Constitution");
        Add(5, 4, "Greenery");
        Add(5, 5, "Children");

        switch (year)
        {
            case 2020:
                Add(7, 23, "Marine");
                Add(7, 24, "Sports");
                Add(8, 10, "Mountain");
                break;
            case 2021:
                Add(7, 22, "Marine");
                Add(7, 23, "Sports");
                Add(8, 8, "Mountain");
                break;
            default:
                AddMonday(7, 3, "Marine");
                if (year >= 2016)
                {
                    Add(8, 11, "Mountain");
                }

                AddMonday(10, 2, year >= 2020 ? "Sports" : "HealthAndSports");
                break;
        }

        AddMonday(9, 3, "RespectForTheAged");
        Add(9, EquinoxDay(year, 23.2488), "AutumnalEquinox");
        Add(11, 3, "Culture");
        Add(11, 23, "LaborThanksgiving");

        if (year <= 2018)
        {
            Add(12, 23, "EmperorsBirthday");
        }

        if (year == 2019)
        {
            Add(4, 30, "Citizens");
            Add(5, 1, "Enthronement");
            Add(5, 2, "Citizens");
            Add(10, 22, "EnthronementCeremony");
        }

        // 国民の休日: 前日と翌日が祝日である平日（日曜以外）
        var holidays = days.Keys.ToList();
        foreach (var d in holidays)
        {
            var next = d.AddDays(1);
            if (days.ContainsKey(d.AddDays(2)) && !days.ContainsKey(next) && next.DayOfWeek != DayOfWeek.Sunday)
            {
                days[next] = "Citizens";
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
                days[substitute] = "Substitute";
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
