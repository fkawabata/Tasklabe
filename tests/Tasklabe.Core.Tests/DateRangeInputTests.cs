using Tasklabe.Core.Editing;

namespace Tasklabe.Core.Tests;

public class DateRangeInputTests
{
    private static readonly DateOnly Today = new(2026, 10, 3);

    [Theory]
    [InlineData("10/5-10/9", "2026-10-05", "2026-10-09")]
    [InlineData("10/5〜10/9", "2026-10-05", "2026-10-09")]
    [InlineData("10/5 ~ 10/9", "2026-10-05", "2026-10-09")]
    [InlineData("10/5から10/9", "2026-10-05", "2026-10-09")]
    [InlineData("10/9-10/5", "2026-10-05", "2026-10-09")]
    [InlineData("t-+4", "2026-10-03", "2026-10-07")]
    [InlineData("+1〜+7", "2026-10-04", "2026-10-10")]
    [InlineData("2026/12/28-2027/1/8", "2026-12-28", "2027-01-08")]
    public void Ranges_are_parsed_in_order(string input, string start, string end)
    {
        Assert.True(DateRangeInput.TryParse(input, Today, out var s, out var e));
        Assert.Equal(DateOnly.Parse(start), s);
        Assert.Equal(DateOnly.Parse(end), e);
    }

    [Theory]
    [InlineData("10/9", "2026-10-09")]
    [InlineData("2026-10-09", "2026-10-09")]
    [InlineData("-3", "2026-09-30")]
    [InlineData("t", "2026-10-03")]
    public void A_single_date_is_a_one_day_range(string input, string date)
    {
        Assert.True(DateRangeInput.TryParse(input, Today, out var s, out var e));
        Assert.Equal(DateOnly.Parse(date), s);
        Assert.Equal(DateOnly.Parse(date), e);
    }

    [Fact]
    public void An_open_side_leaves_that_date_empty()
    {
        Assert.True(DateRangeInput.TryParse("〜10/9", Today, out var s, out var e));
        Assert.Null(s);
        Assert.Equal(new DateOnly(2026, 10, 9), e);

        Assert.True(DateRangeInput.TryParse("10/5〜", Today, out s, out e));
        Assert.Equal(new DateOnly(2026, 10, 5), s);
        Assert.Null(e);
    }

    [Fact]
    public void Empty_input_clears_the_range()
    {
        Assert.True(DateRangeInput.TryParse(" ", Today, out var s, out var e));
        Assert.Null(s);
        Assert.Null(e);
    }

    [Theory]
    [InlineData("〜")]
    [InlineData("10/5-abc")]
    [InlineData("明日〜10/9")]
    public void Invalid_inputs_are_rejected(string input)
    {
        Assert.False(DateRangeInput.TryParse(input, Today, out var s, out var e));
        Assert.Null(s);
        Assert.Null(e);
    }
}
