
namespace Tasklabe.Animation.Tests;

public class SpringTests
{
    private static readonly Spring Bouncy = new(0.48, 0.12);
    private static readonly Spring Critical = new(0.38, 0);

    /// <summary>ばねの固有角振動数（Period の逆数）。</summary>
    private static double Omega(Spring spring) => 1 / spring.Period.TotalSeconds;

    [Fact]
    public void Period_IsOneRoundTripOfVisualDurationDividedBy2Pi()
    {
        var spring = new Spring(0.42, 0.16);

        Assert.Equal(0.42 * 1.2 / (2 * Math.PI), spring.Period.TotalSeconds, 6);
    }

    [Fact]
    public void Damping_IsOneMinusBounce()
    {
        Assert.Equal(0.88f, Bouncy.Damping, 5);
        Assert.Equal(1f, Critical.Damping);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2.7)]
    public void ForDistance_ShortDistance_KeepsSpring(double distance)
    {
        Assert.Equal(Bouncy, Bouncy.ForDistance(distance));
    }

    [Fact]
    public void ForDistance_LongerDistance_IsNeverSlower()
    {
        double previous = Bouncy.VisualDuration;
        foreach (double distance in new[] { 10.0, 100, 400, 1000, 4000 })
        {
            var spring = Bouncy.ForDistance(distance);
            Assert.True(spring.VisualDuration <= previous, $"{distance} px で {spring.VisualDuration} 秒");
            Assert.Equal(Bouncy.Bounce, spring.Bounce);
            previous = spring.VisualDuration;
        }

        Assert.True(previous < Bouncy.VisualDuration);
    }

    [Theory]
    [InlineData(0.0, 100.0)]
    [InlineData(0.0, 900.0)]
    [InlineData(0.12, 100.0)]
    [InlineData(0.12, 900.0)]
    [InlineData(0.4, 300.0)]
    public void Landing_ArrivesWithinHalfPixelAtOriginalDuration(double bounce, double distance)
    {
        var original = new Spring(0.38, bounce);

        var landing = original.Landing(distance);

        // もとの見た目の時間の時点で、残りが 0.5 px を切っていて、かつ必要以上に固くしていない
        double remaining = distance * Spring.Remaining(landing.Damping, Omega(landing) * original.VisualDuration);
        Assert.InRange(remaining, 0.49, 0.5);
        Assert.True(landing.VisualDuration <= original.VisualDuration);
        Assert.Equal(bounce, landing.Bounce);
    }

    [Fact]
    public void Landing_DistanceShorterThanTolerance_KeepsSpring()
    {
        Assert.Equal(Critical, Critical.Landing(0.3));
    }

    [Fact]
    public void Landing_IsStifferThanForDistance()
    {
        // ForDistance の目安では見た目の時間にまだ手前にいるため、着き切らせるばねはそれより固い
        Assert.True(Bouncy.Landing(500).VisualDuration < Bouncy.ForDistance(500).VisualDuration);
    }
}
