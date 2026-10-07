using Tasklabe.Core.Domain;

namespace Tasklabe.Core.Progress;

/// <summary>進捗の積み上げ計算に必要な、タスク階層の 1 ノード。</summary>
public interface IProgressNode
{
    /// <summary>想定工数（h）。未見積りの場合は null。子を持つノードでは使わない。</summary>
    double? EstimateHours { get; }

    /// <summary>申告された進捗率（0〜100）。子を持つノードでは使わない。</summary>
    double ProgressPercent { get; }

    StatusCategory Category { get; }

    IReadOnlyList<IProgressNode> Children { get; }
}
