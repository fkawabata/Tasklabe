using Tasklabe.Core.Settings;

namespace Tasklabe.Core.Tests;

public class ProjectOrderTests
{
    [Fact]
    public void Saved_order_comes_first_and_new_projects_keep_their_order_at_the_end()
    {
        string[] items = ["a", "b", "c", "d"];

        var ordered = ProjectOrder.Apply(items, x => x, ["c", "gone", "a"]);

        Assert.Equal(["c", "a", "b", "d"], ordered);
    }

    [Theory]
    [InlineData("a", "c", false, "b,a,c,d")]
    [InlineData("a", "c", true, "b,c,a,d")]
    [InlineData("d", "a", false, "d,a,b,c")]
    [InlineData("b", "b", true, "a,b,c,d")]
    [InlineData("x", "a", false, "a,b,c,d")]
    public void A_project_moves_before_or_after_the_target(string moved, string target, bool after, string expected)
    {
        var result = ProjectOrder.Move(["a", "b", "c", "d"], moved, target, after);

        Assert.Equal(expected.Split(','), result);
    }
}
