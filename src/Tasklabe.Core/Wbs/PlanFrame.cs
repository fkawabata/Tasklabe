using Tasklabe.Core.Calendar;

namespace Tasklabe.Core.Wbs;

/// <summary>
/// 親タスクの予定（枠）と、子の予定の関係。親の予定開始日・予定終了日は「このフェーズはこの日までに終える」という枠として
/// 利用者が決め、子から書き換えない。子の予定が枠の終わりを超えたとき、または枠の始まりより前に始まるときは、枠超えとして示す。
/// </summary>
public static class PlanFrame
{
    /// <summary>子孫の予定の最も遅い終わり（親自身と中止したタスクを除く）。子を持たないか、子に予定がなければ null。</summary>
    public static DateOnly? ChildrenEnd(TaskNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        DateOnly? end = null;
        foreach (var child in node.ChildNodes)
        {
            if (!child.Task.IsCanceled && (child.Task.Target ?? child.Task.Start) is { } own && (end is null || own > end))
            {
                end = own;
            }

            if (ChildrenEnd(child) is { } deeper && (end is null || deeper > end))
            {
                end = deeper;
            }
        }

        return end;
    }

    /// <summary>子孫の予定の最も早い始まり（親自身と中止したタスクを除く）。子を持たないか、子に予定がなければ null。</summary>
    public static DateOnly? ChildrenStart(TaskNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        DateOnly? start = null;
        foreach (var child in node.ChildNodes)
        {
            if (!child.Task.IsCanceled && (child.Task.Start ?? child.Task.Target) is { } own && (start is null || own < start))
            {
                start = own;
            }

            if (ChildrenStart(child) is { } deeper && (start is null || deeper < start))
            {
                start = deeper;
            }
        }

        return start;
    }

    /// <summary>子の予定が親の予定開始日（枠の始まり）より早く始まるなら、その始まり。枠がないか、はみ出していなければ null。</summary>
    public static DateOnly? EarlyStart(TaskNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.HasChildren && node.Task.Start is { } frame && ChildrenStart(node) is { } start && start < frame ? start : null;
    }

    /// <summary>枠の始まりより前にはみ出した稼働日数（子の最も早い始まりから、枠の始まりの前日まで）。はみ出していなければ 0。</summary>
    public static int EarlyDays(TaskNode node) =>
        EarlyStart(node) is { } start ? WorkCalendar.CountWorkingDays(start, node.Task.Start!.Value.AddDays(-1)) : 0;

    /// <summary>子の予定が親の予定終了日（枠の終わり）より遅く終わるなら、その終わり。枠がないか、超えていなければ null。</summary>
    public static DateOnly? OverrunEnd(TaskNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.HasChildren && node.Task.Target is { } frame && ChildrenEnd(node) is { } end && end > frame ? end : null;
    }

    /// <summary>枠超えの稼働日数（枠の終わりの翌日から、子の最も遅い終わりまで）。超えていなければ 0。</summary>
    public static int OverrunDays(TaskNode node) =>
        OverrunEnd(node) is { } end ? WorkCalendar.CountWorkingDays(node.Task.Target!.Value.AddDays(1), end) : 0;
}
