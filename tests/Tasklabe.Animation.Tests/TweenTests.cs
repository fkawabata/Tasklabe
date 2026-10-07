
namespace Tasklabe.Animation.Tests;

public class TweenTests
{
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.5, 0.875)]
    [InlineData(1.0, 1.0)]
    public void EaseOutCubic_MapsProgress(double progress, double expected)
    {
        Assert.Equal(expected, DispatcherQueueExtensions.EaseOutCubic(progress), 9);
    }

    [Fact]
    public void EaseOutCubic_DeceleratesMonotonically()
    {
        double previous = 0, previousStep = double.MaxValue;
        for (int i = 1; i <= 10; i++)
        {
            double value = DispatcherQueueExtensions.EaseOutCubic(i / 10.0);
            double step = value - previous;
            Assert.True(step > 0 && step < previousStep);
            previous = value;
            previousStep = step;
        }
    }
}
