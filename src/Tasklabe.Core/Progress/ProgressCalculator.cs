using Tasklabe.Core.Domain;

namespace Tasklabe.Core.Progress;

/// <summary>想定工数 × 進捗率による進捗の積み上げ計算（要件 F-PRG-02〜05）。</summary>
public static class ProgressCalculator
{
    /// <summary>未見積りのタスクの重み（h）。未見積りでも進捗率に反映させるため、1 h とみなす。</summary>
    public const double UnestimatedWeight = 1;

    public static ProgressSummary Summarize(IProgressNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (node.Children.Count == 0)
        {
            return SummarizeLeaf(node);
        }

        var summary = SummarizeAll(node.Children);
        return node.Category == StatusCategory.Done ? summary with { ProgressPercent = 100 } : summary;
    }

    public static ProgressSummary SummarizeAll(IEnumerable<IProgressNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        double totalHours = 0;
        double totalWeight = 0;
        double doneWeight = 0;
        int unestimated = 0;
        bool any = false;
        bool allDone = true;

        // 中止したタスクは、工数にも進捗率にも数えない
        foreach (var child in nodes.Where(n => n.Category != StatusCategory.Canceled))
        {
            any = true;
            var s = Summarize(child);
            totalHours += s.EstimateHours;
            totalWeight += s.Weight;
            doneWeight += s.Weight * s.ProgressPercent / 100.0;
            unestimated += s.UnestimatedCount;
            allDone &= s.ProgressPercent >= 100;
        }

        double progress = totalWeight > 0
            ? doneWeight / totalWeight * 100.0
            : (any && allDone ? 100 : 0);

        return new ProgressSummary(totalHours, Clamp(progress), unestimated);
    }

    private static ProgressSummary SummarizeLeaf(IProgressNode node)
    {
        double progress = node.Category == StatusCategory.Done ? 100 : Clamp(node.ProgressPercent);

        return node.EstimateHours is { } hours
            ? new ProgressSummary(Math.Max(hours, 0), progress, 0)
            : new ProgressSummary(0, progress, 1);
    }

    private static double Clamp(double percent) => Math.Clamp(percent, 0, 100);
}
