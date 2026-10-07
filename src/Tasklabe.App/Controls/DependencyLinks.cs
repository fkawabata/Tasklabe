using Tasklabe.Core.Domain;
using Tasklabe.Core.Scheduling;

namespace Tasklabe.App.Controls;

/// <summary>依存関係を張れない理由の伝え方（ガントとタスク詳細ペインで共通）。</summary>
public static class DependencyLinks
{
    /// <summary>利用者に伝える理由。伝えるまでもないもの（張れる、同じもの、既にある）は null。</summary>
    public static string? Explain(LinkCheck check, TaskItem predecessor, TaskItem successor)
    {
        ArgumentNullException.ThrowIfNull(predecessor);
        ArgumentNullException.ThrowIfNull(successor);

        return check switch
        {
            LinkCheck.Hierarchy =>
                $"「{predecessor.Title}」と「{successor.Title}」は親子の関係です。親タスクの中の順序は、中の作業どうしをつないでください。",
            LinkCheck.Cycle =>
                $"「{successor.Title}」は「{predecessor.Title}」より前に行う必要があるため、依存関係が循環します。",
            LinkCheck.NotPlanned =>
                "課題とは依存関係を張れません。先に「計画に移す」でタスクにしてください。",
            _ => null,
        };
    }
}
