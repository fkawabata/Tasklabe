using Tasklabe.Core.Calendar;

namespace Tasklabe.Core.Tests;

public class CalendarTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static IReadOnlyList<string> Dates(int year) =>
        JapaneseHolidays.ForYear(year).Keys.Order().Select(d => d.ToString("MM-dd")).ToList();

    [Fact]
    public void Holidays_2026_include_substitute_and_citizens_holidays()
    {
        Assert.Equal(
            ["01-01", "01-12", "02-11", "02-23", "03-20", "04-29", "05-03", "05-04", "05-05", "05-06",
             "07-20", "08-11", "09-21", "09-22", "09-23", "10-12", "11-03", "11-23"],
            Dates(2026));
        Assert.Equal("振替休日", JapaneseHolidays.NameOf(D(2026, 5, 6)));
        Assert.Equal("国民の休日", JapaneseHolidays.NameOf(D(2026, 9, 22)));
    }

    [Fact]
    public void Holidays_2025()
    {
        Assert.Equal(
            ["01-01", "01-13", "02-11", "02-23", "02-24", "03-20", "04-29", "05-03", "05-04", "05-05", "05-06",
             "07-21", "08-11", "09-15", "09-23", "10-13", "11-03", "11-23", "11-24"],
            Dates(2025));
    }

    [Fact]
    public void Holidays_2019_follow_the_enthronement_special_law()
    {
        Assert.Equal(
            ["01-01", "01-14", "02-11", "03-21", "04-29", "04-30", "05-01", "05-02", "05-03", "05-04", "05-05", "05-06",
             "07-15", "08-11", "08-12", "09-16", "09-23", "10-14", "10-22", "11-03", "11-04", "11-23"],
            Dates(2019));
    }

    [Fact]
    public void Holidays_2020_moved_for_the_olympics()
    {
        var names = JapaneseHolidays.ForYear(2020);
        Assert.Equal("スポーツの日", names[D(2020, 7, 24)]);
        Assert.Equal("山の日", names[D(2020, 8, 10)]);
        Assert.False(names.ContainsKey(D(2020, 10, 12)));
    }

    [Fact]
    public void Working_days_skip_weekends_and_holidays()
    {
        // 2026-05-01 (金) 〜 05-08 (金): 5/2〜5/6 は土日と祝日
        Assert.Equal(3, WorkCalendar.CountWorkingDays(D(2026, 5, 1), D(2026, 5, 8)));
        Assert.Equal(0, WorkCalendar.CountWorkingDays(D(2026, 5, 8), D(2026, 5, 1)));
        Assert.Equal(D(2026, 5, 7), WorkCalendar.NthWorkingDay(D(2026, 5, 2), 1));
        Assert.Equal(D(2026, 5, 8), WorkCalendar.NthWorkingDay(D(2026, 5, 1), 3));
    }

    [Fact]
    public void Custom_rules_change_days_off_holidays_and_extra_days()
    {
        var rules = new WorkCalendarRules(
            new HashSet<DayOfWeek> { DayOfWeek.Sunday, DayOfWeek.Wednesday }, false, new HashSet<DateOnly> { D(2026, 12, 29) });

        Assert.False(rules.IsHoliday(D(2026, 9, 26)));   // 土曜は稼働日
        Assert.True(rules.IsHoliday(D(2026, 9, 30)));    // 水曜は休み
        Assert.False(rules.IsHoliday(D(2026, 9, 22)));   // 祝日（火）は稼働日
        Assert.True(rules.IsHoliday(D(2026, 12, 29)));   // 独自の休日
        Assert.False(rules.IsStandard);
        Assert.True(WorkCalendarRules.Standard.IsStandard);
    }

    [Fact]
    public void Rules_round_trip_and_fall_back_to_standard()
    {
        var rules = new WorkCalendarRules(
            new HashSet<DayOfWeek> { DayOfWeek.Friday }, false, new HashSet<DateOnly> { D(2026, 8, 13), D(2026, 8, 14) });
        var parsed = WorkCalendarRules.Parse(rules.ToString());

        Assert.True(parsed.DaysOff.SetEquals(rules.DaysOff));
        Assert.False(parsed.JapaneseHolidaysOff);
        Assert.True(parsed.ExtraHolidays.SetEquals(rules.ExtraHolidays));

        Assert.True(WorkCalendarRules.Parse(null).IsStandard);
        Assert.True(WorkCalendarRules.Parse("junk;extra=nope").IsStandard);
        // すべての曜日を休みにした設定は、稼働日を探せなくなるため既定に戻す
        Assert.True(WorkCalendarRules.Parse("off=Monday,Tuesday,Wednesday,Thursday,Friday,Saturday,Sunday").IsStandard);
    }
}
