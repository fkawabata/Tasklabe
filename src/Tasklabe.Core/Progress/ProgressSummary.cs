namespace Tasklabe.Core.Progress;

/// <param name="EstimateHours">見積り済みタスクの想定工数の合計（h）。</param>
/// <param name="ProgressPercent">工数で重み付けした進捗率（0〜100）。未見積りのタスクは 1 h とみなす。</param>
/// <param name="UnestimatedCount">未見積りの末端タスクの数。</param>
public readonly record struct ProgressSummary(double EstimateHours, double ProgressPercent, int UnestimatedCount)
{
    /// <summary>進捗率の重み付けに用いる工数（未見積りのタスクを 1 h とみなした合計）。</summary>
    public double Weight => EstimateHours + UnestimatedCount * ProgressCalculator.UnestimatedWeight;

    public double DoneHours => EstimateHours * ProgressPercent / 100.0;

    public double RemainingHours => EstimateHours - DoneHours;
}
