using Tasklabe.App.Services;
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
                Texts.Format("Link_Hierarchy", predecessor.Title, successor.Title),
            LinkCheck.Cycle =>
                Texts.Format("Link_Cycle", successor.Title, predecessor.Title),
            LinkCheck.NotPlanned =>
                Texts.Get("Link_NotPlanned"),
            _ => null,
        };
    }
}
