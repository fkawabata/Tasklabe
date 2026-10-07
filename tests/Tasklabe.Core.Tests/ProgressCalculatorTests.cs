using Tasklabe.Core.Domain;
using Tasklabe.Core.Progress;

namespace Tasklabe.Core.Tests;

public class ProgressCalculatorTests
{
    private sealed record Node(
        double? EstimateHours,
        double ProgressPercent,
        StatusCategory Category,
        params IProgressNode[] Kids) : IProgressNode
    {
        public IReadOnlyList<IProgressNode> Children => Kids;
    }

    [Fact]
    public void Leaf_uses_reported_progress()
    {
        var s = ProgressCalculator.Summarize(new Node(8, 50, StatusCategory.InProgress));

        Assert.Equal(8, s.EstimateHours);
        Assert.Equal(50, s.ProgressPercent);
        Assert.Equal(4, s.DoneHours);
    }

    [Fact]
    public void Canceled_children_are_left_out_of_progress_and_estimate()
    {
        var parent = new Node(null, 0, StatusCategory.InProgress,
            new Node(8, 100, StatusCategory.Done),
            new Node(8, 0, StatusCategory.Todo),
            new Node(40, 0, StatusCategory.Canceled));

        var s = ProgressCalculator.Summarize(parent);

        Assert.Equal(16, s.EstimateHours);
        Assert.Equal(50, s.ProgressPercent);
    }

    [Fact]
    public void Done_leaf_is_always_100_percent()
    {
        var s = ProgressCalculator.Summarize(new Node(8, 30, StatusCategory.Done));

        Assert.Equal(100, s.ProgressPercent);
    }

    [Fact]
    public void Parent_weights_children_by_estimate()
    {
        var parent = new Node(null, 0, StatusCategory.InProgress,
            new Node(16, 100, StatusCategory.Done),
            new Node(24, 50, StatusCategory.InProgress));

        var s = ProgressCalculator.Summarize(parent);

        Assert.Equal(40, s.EstimateHours);
        Assert.Equal(70, s.ProgressPercent, precision: 6); // (16 + 12) / 40
    }

    [Fact]
    public void Parent_ignores_its_own_estimate_and_progress()
    {
        var parent = new Node(100, 90, StatusCategory.InProgress,
            new Node(10, 0, StatusCategory.Todo));

        var s = ProgressCalculator.Summarize(parent);

        Assert.Equal(10, s.EstimateHours);
        Assert.Equal(0, s.ProgressPercent);
    }

    [Fact]
    public void Unestimated_tasks_weigh_one_hour_and_are_counted()
    {
        var parent = new Node(null, 0, StatusCategory.InProgress,
            new Node(10, 50, StatusCategory.InProgress),
            new Node(null, 100, StatusCategory.InProgress));

        var s = ProgressCalculator.Summarize(parent);

        Assert.Equal(10, s.EstimateHours);
        Assert.Equal(6.0 / 11 * 100, s.ProgressPercent, precision: 6); // (5 + 1) / (10 + 1)
        Assert.Equal(1, s.UnestimatedCount);
    }

    [Fact]
    public void Remaining_unestimated_tasks_keep_project_below_100_percent()
    {
        var s = ProgressCalculator.SummarizeAll([
            new Node(18, 100, StatusCategory.Done),
            new Node(null, 0, StatusCategory.Todo),
            new Node(null, 0, StatusCategory.Todo)]);

        Assert.Equal(90, s.ProgressPercent, precision: 6); // 18 / 20
    }

    [Fact]
    public void Nested_hierarchy_rolls_up()
    {
        var root = new Node(null, 0, StatusCategory.InProgress,
            new Node(null, 0, StatusCategory.InProgress,
                new Node(4, 100, StatusCategory.Done),
                new Node(4, 0, StatusCategory.Todo)),
            new Node(8, 25, StatusCategory.InProgress));

        var s = ProgressCalculator.Summarize(root);

        Assert.Equal(16, s.EstimateHours);
        Assert.Equal(37.5, s.ProgressPercent, precision: 6); // (4 + 2) / 16
        Assert.Equal(10, s.RemainingHours, precision: 6);
    }

    [Fact]
    public void All_done_without_estimates_is_100_percent()
    {
        var s = ProgressCalculator.SummarizeAll([
            new Node(null, 0, StatusCategory.Done),
            new Node(null, 0, StatusCategory.Done)]);

        Assert.Equal(100, s.ProgressPercent);
        Assert.Equal(2, s.UnestimatedCount);
    }

    [Fact]
    public void Progress_is_clamped()
    {
        var s = ProgressCalculator.Summarize(new Node(8, 150, StatusCategory.InProgress));

        Assert.Equal(100, s.ProgressPercent);
    }
}
