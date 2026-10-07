using Tasklabe.Core.Calendar;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Gantt;
using Tasklabe.Core.Wbs;

namespace Tasklabe.Core.Scheduling;

/// <summary>依存関係を張れるかの判定（要件 F-DEP-02）。</summary>
public enum LinkCheck
{
    Ok,

    /// <summary>自分自身。</summary>
    Same,

    /// <summary>計画に入っていないタスク（課題など）。</summary>
    NotPlanned,

    /// <summary>既に張ってある。</summary>
    AlreadyLinked,

    /// <summary>親子（祖先と子孫）のあいだ。</summary>
    Hierarchy,

    /// <summary>張ると循環する（親子を通じた循環を含む）。</summary>
    Cycle,
}

/// <summary>予定開始日と予定終了日の組。</summary>
public readonly record struct PlanDates(DateOnly? Start, DateOnly? Target);

/// <summary>依存関係に合わせて動かす予定。</summary>
/// <param name="Cause">この移動のもとになった先行タスク。</param>
/// <param name="CauseDelayed">先行タスクの見込みが予定終了日より遅れていることが原因か。</param>
/// <param name="Overlaps">
/// いまの予定で、先行タスクの終わりより前に始まっているか（食い違いそのもの）。false なら、前のタスクを動かすのに合わせて
/// 連鎖して動くだけのもの。
/// </param>
public sealed record ScheduleShift(TaskItem Task, PlanDates Before, PlanDates After, TaskItem Cause, bool CauseDelayed, bool Overlaps = false);

/// <summary>
/// 依存関係による日程の調整（要件 F-DEP-03〜05）。
/// 後続タスクは、先行タスクの終わり（未完了なら見込み）の翌稼働日より前に始めない。
/// 満たさない後続は、稼働日数を保ったまま後ろへずらす。前倒しはしない。
/// </summary>
/// <remarks>
/// 親タスク（フェーズ）が先行なら、その配下すべての終わりを先行の終わりとする。
/// 親タスクが後続なら、配下を同じ稼働日数だけずらし、フェーズの中の段取りを保つ。
/// 着手済み・完了のタスクと、利用者が指定した予定（<c>pinned</c>）は動かさない。
/// </remarks>
public static class DependencyScheduler
{
    /// <summary>後続をずらす繰り返しの上限。依存関係が矛盾していても止まるようにする。</summary>
    private const int MaxRelaxations = 100_000;

    // ================================================================ 依存関係を張れるか

    public static LinkCheck CanLink(TaskTree tree, TaskItem predecessor, TaskItem successor)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(predecessor);
        ArgumentNullException.ThrowIfNull(successor);

        if (predecessor.IssueId == successor.IssueId)
        {
            return LinkCheck.Same;
        }

        var graph = new Graph(tree);
        if (graph.NodeOf(predecessor.IssueId) is not { } pred || graph.NodeOf(successor.IssueId) is not { } succ)
        {
            return LinkCheck.NotPlanned;
        }

        if (successor.BlockedBy.Contains(predecessor.IssueId, StringComparer.Ordinal))
        {
            return LinkCheck.AlreadyLinked;
        }

        if (succ.IsDescendantOf(pred) || pred.IsDescendantOf(succ))
        {
            return LinkCheck.Hierarchy;
        }

        // 後続側の作業から先行側の作業へ既に道があれば、張ると循環する
        return graph.Reaches(succ.Leaves(), pred.Leaves()) ? LinkCheck.Cycle : LinkCheck.Ok;
    }

    // ================================================================ 日程の調整

    /// <summary>依存関係を満たすために動かす予定を求める。</summary>
    /// <param name="pinned">利用者が決めた予定（ItemId ごと）。これらは動かさず、この予定を前提に後続を求める。</param>
    /// <param name="downstreamOf">
    /// 指定すると、これらのタスク（ItemId）の変更が及ぶ後続だけを動かし、他の既存の食い違いには触れない。
    /// null なら計画全体の食い違いを解消する。
    /// </param>
    public static IReadOnlyList<ScheduleShift> Resolve(TaskTree tree, DateOnly today,
        IReadOnlyDictionary<string, PlanDates>? pinned = null, IEnumerable<string>? downstreamOf = null)
    {
        ArgumentNullException.ThrowIfNull(tree);

        pinned ??= new Dictionary<string, PlanDates>();
        var graph = new Graph(tree);
        var dates = tree.All().ToDictionary(n => n.Task.ItemId, n => pinned.TryGetValue(n.Task.ItemId, out var p)
            ? p
            : new PlanDates(n.Task.Start, n.Task.Target), StringComparer.Ordinal);

        HashSet<TaskNode>? allowed = null;
        if (downstreamOf is not null)
        {
            var sources = downstreamOf.ToHashSet(StringComparer.Ordinal);
            allowed = graph.Downstream(tree.All().Where(n => sources.Contains(n.Task.ItemId)));
        }

        bool Movable(TaskNode n) =>
            !pinned.ContainsKey(n.Task.ItemId) && !n.Task.IsDone && n.Task.ActualStart is null
            && (allowed is null || allowed.Contains(n));

        // いまの予定のまま、先行の終わりより前に始まっているもの（調整で動かす前の食い違いそのもの）
        var overlapping = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (pred, succ) in graph.Edges)
        {
            if (Finish(pred, dates, today) is { } finish)
            {
                var required = WorkCalendar.NthWorkingDay(finish.Date.AddDays(1), 1);
                overlapping.UnionWith(succ.SelfAndDescendants()
                    .Where(n => Movable(n) && HasDates(dates[n.Task.ItemId]) && StartOf(dates[n.Task.ItemId]) < required)
                    .Select(n => n.Task.ItemId));
            }
        }

        var causes = new Dictionary<string, (TaskItem Cause, bool Delayed)>(StringComparer.Ordinal);
        int relaxations = 0;
        bool changed = true;
        while (changed && relaxations < MaxRelaxations)
        {
            changed = false;
            foreach (var (pred, succ) in graph.Edges)
            {
                relaxations++;
                if (Finish(pred, dates, today) is not { } finish)
                {
                    continue;
                }

                var required = WorkCalendar.NthWorkingDay(finish.Date.AddDays(1), 1);
                var subtree = succ.SelfAndDescendants().Where(n => Movable(n) && HasDates(dates[n.Task.ItemId])).ToList();
                if (subtree.Count == 0)
                {
                    continue;
                }

                var anchor = subtree.Min(n => StartOf(dates[n.Task.ItemId]));
                if (anchor >= required)
                {
                    continue;
                }

                // 最も早い作業が required に来るだけずらす（稼働日で数え、配下の段取りを保つ）
                int delta = WorkCalendar.CountWorkingDays(Snap(anchor), required) - 1;
                foreach (var node in subtree)
                {
                    dates[node.Task.ItemId] = Shift(dates[node.Task.ItemId], delta);
                    causes[node.Task.ItemId] = (pred.Task, finish.Delayed);
                }

                changed = true;
            }
        }

        var shifts = new List<ScheduleShift>();
        foreach (var node in tree.All())
        {
            var task = node.Task;
            var before = new PlanDates(task.Start, task.Target);
            var after = dates[task.ItemId];
            if (after != before && !pinned.ContainsKey(task.ItemId) && causes.TryGetValue(task.ItemId, out var cause))
            {
                shifts.Add(new ScheduleShift(task, before, after, cause.Cause, cause.Delayed, overlapping.Contains(task.ItemId)));
            }
        }

        return shifts;
    }

    // ================================================================ 終わりと始まり

    /// <summary>先行としての終わり。親は配下の最も遅い終わり。日付がなければ null。</summary>
    private static (DateOnly Date, bool Delayed)? Finish(TaskNode node, Dictionary<string, PlanDates> dates, DateOnly today)
    {
        // 後続を待たせないタスク（とその配下）は、先行としての終わりを持たない
        if (node.Task.NonBlocking)
        {
            return null;
        }

        // 親の終わりは配下から決める。親自身の進捗は、子を足す前に着手したときの値が残っていることがあるため、見込みには使わない
        (DateOnly Date, bool Delayed)? result = node.HasChildren && !node.Task.IsDone
            ? null
            : LeafFinish(node.Task, dates[node.Task.ItemId], today);
        foreach (var child in node.ChildNodes)
        {
            if (Finish(child, dates, today) is { } f && (result is not { } r || f.Date > r.Date))
            {
                result = f;
            }
        }

        return result;
    }

    /// <summary>
    /// 1 つのタスクの終わり。完了なら実績終了日、着手済みなら予定終了日と見込みの遅いほう、
    /// 未着手で開始予定を過ぎていれば今日から始めた場合の終わりと予定終了日の遅いほう。
    /// </summary>
    private static (DateOnly Date, bool Delayed)? LeafFinish(TaskItem task, PlanDates plan, DateOnly today)
    {
        var start = plan.Start ?? plan.Target;
        var end = plan.Target ?? plan.Start;
        if (end is { } e && start is { } s && e < s)
        {
            end = s;
        }

        if (task.IsCanceled)
        {
            // 中止したタスクは、後続を待たせない
            return null;
        }

        if (task.IsDone)
        {
            var done = task.ActualEnd ?? end;
            return done is { } d ? (d, false) : null;
        }

        if (end is not { } planEnd)
        {
            return null;
        }

        int workingDays = start is { } ps ? Math.Max(WorkCalendar.CountWorkingDays(ps, planEnd), 1) : 1;
        DateOnly forecast;
        if (task.ActualStart is { } actualStart)
        {
            forecast = GanttSchedule.ForecastEnd(actualStart, task.EffectiveProgress, workingDays, today);
        }
        else if (start is { } plannedStart && plannedStart < today)
        {
            forecast = WorkCalendar.NthWorkingDay(today, workingDays);
        }
        else
        {
            return (planEnd, false);
        }

        return forecast > planEnd ? (forecast, true) : (planEnd, false);
    }

    private static bool HasDates(PlanDates d) => d.Start is not null || d.Target is not null;

    private static DateOnly StartOf(PlanDates d) => (d.Start ?? d.Target)!.Value;

    /// <summary>date 以降で最初の稼働日。</summary>
    private static DateOnly Snap(DateOnly date) => WorkCalendar.NthWorkingDay(date, 1);

    /// <summary>予定を稼働日で delta 日ずらす。期間（稼働日数）は保つ。</summary>
    private static PlanDates Shift(PlanDates d, int delta)
    {
        if (d.Start is { } start && d.Target is { } target)
        {
            int length = Math.Max(WorkCalendar.CountWorkingDays(start, target), 1);
            var newStart = WorkCalendar.NthWorkingDay(start, delta + 1);
            var newTarget = target == start ? newStart : WorkCalendar.NthWorkingDay(newStart, length);
            return new PlanDates(newStart, newTarget < newStart ? newStart : newTarget);
        }

        return new PlanDates(
            d.Start is { } s ? WorkCalendar.NthWorkingDay(s, delta + 1) : null,
            d.Target is { } t ? WorkCalendar.NthWorkingDay(t, delta + 1) : null);
    }

    // ================================================================ 依存関係

    /// <summary>計画の中の依存関係。</summary>
    private sealed class Graph
    {
        private readonly Dictionary<string, TaskNode> _byIssue;
        private readonly Dictionary<TaskNode, List<TaskNode>> _successors = [];

        public Graph(TaskTree tree)
        {
            _byIssue = new Dictionary<string, TaskNode>(StringComparer.Ordinal);
            foreach (var node in tree.All())
            {
                _byIssue.TryAdd(node.Task.IssueId, node);
            }

            var edges = new List<(TaskNode, TaskNode)>();
            foreach (var succ in tree.All())
            {
                foreach (var id in succ.Task.BlockedBy)
                {
                    // 計画にない先行（課題など）と、親子のあいだの依存は扱わない
                    if (_byIssue.TryGetValue(id, out var pred) && !ReferenceEquals(pred, succ)
                        && !succ.IsDescendantOf(pred) && !pred.IsDescendantOf(succ))
                    {
                        edges.Add((pred, succ));
                        if (!_successors.TryGetValue(pred, out var list))
                        {
                            _successors[pred] = list = [];
                        }

                        list.Add(succ);
                    }
                }
            }

            Edges = edges;
        }

        public IReadOnlyList<(TaskNode Predecessor, TaskNode Successor)> Edges { get; }

        public TaskNode? NodeOf(string issueId) => _byIssue.GetValueOrDefault(issueId);

        /// <summary>
        /// 作業（葉）の単位で、from のいずれかから to のいずれかへ依存関係をたどって行き着くか。
        /// 親タスクの依存関係は、その配下の作業すべてのあいだの依存関係とみなす。
        /// </summary>
        public bool Reaches(IEnumerable<TaskNode> from, IEnumerable<TaskNode> to)
        {
            var targets = to.ToHashSet();
            var visited = new HashSet<TaskNode>();
            var stack = new Stack<TaskNode>(from);
            while (stack.Count > 0)
            {
                var leaf = stack.Pop();
                if (targets.Contains(leaf))
                {
                    return true;
                }

                if (!visited.Add(leaf))
                {
                    continue;
                }

                // この作業、またはこれを含む親が先行になっている依存関係の、後続側の作業へ進む
                foreach (var source in leaf.Ancestors().Prepend(leaf))
                {
                    foreach (var succ in _successors.GetValueOrDefault(source) ?? [])
                    {
                        foreach (var next in succ.Leaves())
                        {
                            stack.Push(next);
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 指定したタスクの変更が及びうる後続（とその配下）。
        /// タスクの終わりが変わると、それを含む親の終わりも変わるため、親の後続もたどる。
        /// </summary>
        public HashSet<TaskNode> Downstream(IEnumerable<TaskNode> changed)
        {
            var result = new HashSet<TaskNode>();
            var sources = new HashSet<TaskNode>();
            var queue = new Queue<TaskNode>(changed);
            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                foreach (var source in node.SelfAndDescendants().Concat(node.Ancestors()))
                {
                    if (!sources.Add(source))
                    {
                        continue;
                    }

                    foreach (var succ in _successors.GetValueOrDefault(source) ?? [])
                    {
                        foreach (var moved in succ.SelfAndDescendants())
                        {
                            if (result.Add(moved))
                            {
                                queue.Enqueue(moved);
                            }
                        }
                    }
                }
            }

            return result;
        }
    }
}
