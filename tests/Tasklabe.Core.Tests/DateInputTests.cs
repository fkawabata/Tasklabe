using Tasklabe.Core.Editing;

namespace Tasklabe.Core.Tests;

public class DateInputTests
{
    private static readonly DateOnly Today = new(2026, 9, 19);

    [Theory]
    [InlineData("9/25", "2026-09-25")]
    [InlineData("12/1", "2026-12-01")]
    [InlineData("2027/1/5", "2027-01-05")]
    [InlineData("2026-10-03", "2026-10-03")]
    [InlineData("1003", "2026-10-03")]
    [InlineData("20261003", "2026-10-03")]
    [InlineData("１０／３", "2026-10-03")]
    [InlineData("t", "2026-09-19")]
    [InlineData("今日", "2026-09-19")]
    [InlineData("+3", "2026-09-22")]
    [InlineData("-19", "2026-08-31")]
    public void Valid_inputs_are_parsed(string input, string expected)
    {
        Assert.True(DateInput.TryParse(input, Today, out var date));
        Assert.Equal(DateOnly.Parse(expected), date);
    }

    [Fact]
    public void Empty_input_clears_the_date()
    {
        Assert.True(DateInput.TryParse("  ", Today, out var date));
        Assert.Null(date);
    }

    [Theory]
    [InlineData("明日")]
    [InlineData("13/40")]
    [InlineData("abc")]
    public void Invalid_inputs_are_rejected(string input)
    {
        Assert.False(DateInput.TryParse(input, Today, out _));
    }
}
