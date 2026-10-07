using Tasklabe.Core.Domain;
using Tasklabe.Core.Milestones;
using Tasklabe.Core.Wbs;

namespace Tasklabe.Core.Gantt;

public enum GanttRowKind
{
    Task,
    Parent,

    /// <summary>マイルストーン（期限）。タスクではなく、期日へ向けた進み具合を示すための行（UI デザイン設計書 3.3.7 節）。</summary>
    Milestone,
}

/// <summary>ガントチャートの 1 行（要件 F-UI-GT-01〜06）。</summary>
public sealed class GanttRow
{
    /// <summary>タスクの行のノード。マイルストーンの行は持たない。</summary>
    public required TaskNode? Node { get; init; }

    public required int Index { get; set; }

    public required GanttRowKind Kind { get; init; }

    /// <summary>行のタスク。マイルストーンの行で読むのは誤りのため、例外にする。</summary>
    public TaskItem Task => Node?.Task ?? throw new InvalidOperationException("マイルストーンの行はタスクを持ちません。");

    /// <summary>マイルストーンの行の集計。タスクの行は null。</summary>
    public MilestoneSummary? Milestone { get; init; }

    public bool IsMilestone => Kind == GanttRowKind.Milestone;

    /// <summary>
    /// イナズマ線が通る行か。マイルストーンと中止したタスクは進み具合の点を持たず、線は前後の行の点を直接結ぶ。
    /// </summary>
    public bool IsOnInazuma => !IsMilestone && !Task.IsCanceled;

    public const string MilestoneKeyPrefix = "milestone:";

    /// <summary>行を見分けるキー（選択に使う）。タスクはアイテムの ID、マイルストーンは "milestone:" と ID。</summary>
    public string Key => Node?.Task.ItemId ?? MilestoneKeyPrefix + Milestone!.Milestone.Id;

    /// <summary>マイルストーンの行で、今日までに進んでいるはずの進捗率（予定の出来高）。予定のあるタスクがなければ null。</summary>
    public double? ExpectedPercent { get; init; }

    /// <summary>予定（親タスクは子の予定を含めた範囲）。マイルストーンは、入っているタスクの最初の開始から期日まで。</summary>
    public DateOnly? PlanStart { get; init; }

    /// <summary>親タスクの子の予定が枠（親の予定終了日）を超えているとき、その枠の終わり（PlanFrame）。超えていなければ null。</summary>
    public DateOnly? FrameEnd { get; init; }

    /// <summary>親タスクの子の予定が枠の始まり（親の予定開始日）より前に始まるとき、その枠の始まり。はみ出していなければ null。</summary>
    public DateOnly? FrameStart { get; init; }

    public DateOnly? PlanEnd { get; init; }

    /// <summary>実績（親タスクは子の実績を含めた範囲）。</summary>
    public DateOnly? ActualStart { get; init; }

    /// <summary>実績終了日、または未完了のタスクの見込み終了日。</summary>
    public DateOnly? ActualEnd { get; init; }

    /// <summary>実績の終端が見込み（未完了）か。</summary>
    public bool IsForecast { get; init; }

    public double ProgressPercent { get; init; }

    public bool IsDone { get; init; }

    /// <summary>イナズマ線の点（日単位の位置）。予定がないか、中止したタスクなら null。</summary>
    public double? Inazuma { get; init; }

    /// <summary>予定終了日を過ぎた稼働日数。</summary>
    public int DelayDays { get; init; }

    /// <summary>先行タスクの行番号（表示中の行のみ）。</summary>
    public IReadOnlyList<int> Predecessors { get; set; } = [];

    /// <summary>
    /// この行が先行のとき、矢印を出す行。展開している親タスクは、子のうちバーが最も右で終わるもの（中止と後続を待たせないものを除く）、
    /// その子も展開していればさらにその子から出す。それ以外は自分の行。
    /// </summary>
    public int ArrowFrom { get; set; }

    /// <summary>
    /// この行が後続のとき、矢印を受ける行。展開している親タスクは、子のうちバーが最も左で始まるもの（中止を除く）、
    /// その子も展開していればさらにその子で受ける。それ以外は自分の行。
    /// </summary>
    public int ArrowTo { get; set; }

    public bool HasPlan => PlanStart is not null && PlanEnd is not null;
}

/// <summary>
/// 親タスク（フェーズ）の読み方。イナズマ線は配下の進捗をまとめた仕事の量の進み具合を、バーの色は配下の見込みのうち最も遅い
/// 終わりを示し、両者は食い違うことがある（量は足りていても、1 つの作業の遅れで終わりが延びる）。それぞれの根拠を示すために使う。
/// </summary>
/// <param name="PaceDays">イナズマ線の点の、今日からのずれ（日）。正なら先行、負なら遅れ。</param>
/// <param name="Bottleneck">見込みの終わりが親の予定の終わりを超える作業のうち、最も遅いもの。超えるものがなければ null。</param>
/// <param name="BottleneckEnd">Bottleneck の見込み終了日（完了していれば実績終了日）。</param>
public sealed record PhaseReading(double PaceDays, TaskItem? Bottleneck, DateOnly? BottleneckEnd);

public static class GanttModel
{
    /// <summary>親タスクの行の読み方。完了した親、予定やイナズマ線の点のない親、タスクの行では null。</summary>
    public static PhaseReading? ReadPhase(GanttRow row, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Kind != GanttRowKind.Parent || row.IsDone || row.Inazuma is not { } point || row.PlanEnd is not { } planEnd)
        {
            return null;
        }

        TaskItem? bottleneck = null;
        DateOnly? bottleneckEnd = null;
        foreach (var leaf in row.Node!.Leaves().Where(n => !n.Task.IsCanceled))
        {
            if (GanttSchedule.ActualOrForecastEnd(leaf.Task, today) is { } end && end > planEnd && (bottleneckEnd is null || end > bottleneckEnd))
            {
                bottleneck = leaf.Task;
                bottleneckEnd = end;
            }
        }

        return new PhaseReading(point - today.DayNumber, bottleneck, bottleneckEnd);
    }

    /// <summary>
    /// 表示する行（WBS の順）からガントチャートの行を作る。マイルストーンを渡すと、その行を計画の中に置く（PlaceMilestones）。
    /// </summary>
    /// <param name="plan">マイルストーンに入っているタスクを探す計画（表示していない行も含む）。</param>
    public static IReadOnlyList<GanttRow> Build(IEnumerable<TaskNode> nodes, DateOnly today,
        TaskTree? plan = null, IReadOnlyList<MilestoneSummary>? milestones = null)
    {
        var rows = new List<GanttRow>();
        foreach (var node in nodes)
        {
            rows.Add(CreateRow(node, rows.Count, today));
        }

        if (plan is not null && milestones is { Count: > 0 })
        {
            rows = PlaceMilestones(rows, plan, milestones, today);
        }

        for (int i = 0; i < rows.Count; i++)
        {
            rows[i].Index = i;
        }

        var indexByIssue = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows.Where(r => !r.IsMilestone))
        {
            indexByIssue.TryAdd(row.Task.IssueId, row.Index);
        }

        foreach (var row in rows.Where(r => !r.IsMilestone && r.Task.BlockedBy.Count > 0))
        {
            row.Predecessors = row.Task.BlockedBy
                .Select(id => indexByIssue.TryGetValue(id, out var i) ? i : -1)
                .Where(i => i >= 0 && i != row.Index)
                .ToList();
        }

        RouteArrows(rows);
        return rows;
    }

    /// <summary>
    /// 依存関係の矢印の端を決める。親タスクの依存関係は日程の調整で配下に受け継がれ、先行としての終わりは配下の最も遅い終わり、
    /// 後続としての始まりは配下の最も早い始まりになる（要件 F-DEP-06）。展開して子が見えているときは、右端・左端にある子のバーへ
    /// 矢印を付け、親の範囲の線から矢印が出ないようにする。
    /// </summary>
    private static void RouteArrows(List<GanttRow> rows)
    {
        var rowOf = new Dictionary<TaskNode, GanttRow>();
        foreach (var row in rows.Where(r => !r.IsMilestone))
        {
            rowOf[row.Node!] = row;
        }

        foreach (var row in rows)
        {
            row.ArrowFrom = row.IsMilestone ? row.Index : Descend(row, r => r.Task.NonBlocking ? null : End(r), (a, b) => a > b).Index;
            row.ArrowTo = row.IsMilestone ? row.Index : Descend(row, Begin, (a, b) => a < b).Index;
        }

        // 子が行として並んでいる親は展開している。中止した子は予定に数えないため、矢印の端にもしない
        GanttRow Descend(GanttRow row, Func<GanttRow, DateOnly?> key, Func<DateOnly, DateOnly, bool> better)
        {
            GanttRow? best = null;
            DateOnly bestKey = default;
            foreach (var child in row.Node!.ChildNodes)
            {
                if (rowOf.TryGetValue(child, out var c) && !c.Task.IsCanceled && key(c) is { } k && (best is null || better(k, bestKey)))
                {
                    best = c;
                    bestKey = k;
                }
            }

            return best is null ? row : Descend(best, key, better);
        }

        // バーの描かれている端で比べる。展開している親は予定と実績の和の範囲の線、それ以外のバーは予定に、予定を超えた未完了の実績を足した範囲
        bool IsRange(GanttRow r) => r.Node!.ChildNodes.Any(rowOf.ContainsKey);

        DateOnly? End(GanttRow r) => !IsRange(r) && r.IsDone && r.PlanEnd is not null ? r.PlanEnd : Max(r.PlanEnd, r.ActualEnd);

        DateOnly? Begin(GanttRow r) => !IsRange(r) && r.PlanStart is not null ? r.PlanStart : Min(r.PlanStart, r.ActualStart);
    }

    /// <summary>行の予定・実績が及ぶ範囲。どの行にも日付がなければ null。</summary>
    public static (DateOnly Start, DateOnly End)? Extent(IEnumerable<GanttRow> rows)
    {
        DateOnly? min = null, max = null;
        foreach (var r in rows)
        {
            foreach (var d in (DateOnly?[])[r.PlanStart, r.PlanEnd, r.ActualStart, r.ActualEnd])
            {
                if (d is { } v)
                {
                    min = min is { } m && m <= v ? m : v;
                    max = max is { } x && x >= v ? x : v;
                }
            }
        }

        return min is { } s && max is { } e ? (s, e) : null;
    }

    /// <summary>
    /// マイルストーンの行を計画の中に置く。最上位の行を計画の順に見て、終わり（親は子の予定を含めた範囲の終わり）が期日以前の行のうち、
    /// 最後のものの配下の後ろに置く。そのような行がなければ先頭に置く。同じ場所に複数あれば期日の順とする。
    /// 計画の順を崩さずに、流れの中のどこが期限かを示すためである。
    /// </summary>
    private static List<GanttRow> PlaceMilestones(List<GanttRow> rows, TaskTree plan, IReadOnlyList<MilestoneSummary> milestones, DateOnly today)
    {
        var tops = rows.Select((r, i) => (Row: r, Index: i)).Where(t => t.Row.Node!.Parent is null).ToList();
        var placed = new Dictionary<int, List<GanttRow>>();
        DateOnly? previousDue = null;
        foreach (var s in milestones.Where(s => s.Milestone.Due is not null).OrderBy(s => s.Milestone.Due).ThenBy(s => s.Milestone.Number))
        {
            var due = s.Milestone.Due!.Value;
            int last = tops.FindLastIndex(t => t.Row.PlanEnd is { } end && end <= due);
            int position = last < 0 ? 0 : last + 1 < tops.Count ? tops[last + 1].Index : rows.Count;
            if (!placed.TryGetValue(position, out var list))
            {
                placed[position] = list = [];
            }

            list.Add(CreateMilestoneRow(s, plan, previousDue, today));
            previousDue = due;
        }

        var result = new List<GanttRow>(rows.Count + milestones.Count);
        for (int i = 0; i <= rows.Count; i++)
        {
            if (placed.TryGetValue(i, out var here))
            {
                result.AddRange(here);
            }

            if (i < rows.Count)
            {
                result.Add(rows[i]);
            }
        }

        return result;
    }

    /// <summary>
    /// マイルストーンの行。バーは、入っているタスクの最初の開始（なければ前のマイルストーンの期日の翌日）から期日までとする。
    /// </summary>
    private static GanttRow CreateMilestoneRow(MilestoneSummary summary, TaskTree plan, DateOnly? previousDue, DateOnly today)
    {
        var due = summary.Milestone.Due!.Value;
        var members = MilestonePlan.Members(plan, summary.Milestone.Id);
        var start = members.Select(t => t.Start ?? t.Target).Where(d => d is not null).Min()
            ?? previousDue?.AddDays(1) ?? due;
        if (start > due)
        {
            start = due;
        }

        return new GanttRow
        {
            Node = null,
            Index = 0,
            Kind = GanttRowKind.Milestone,
            Milestone = summary,
            PlanStart = start,
            PlanEnd = due,
            ProgressPercent = summary.Progress.ProgressPercent,
            IsDone = summary.Total > 0 && summary.Done == summary.Total,
            ExpectedPercent = MilestonePlan.ExpectedPercent(members, today),
        };
    }

    private static GanttRow CreateRow(TaskNode node, int index, DateOnly today)
    {
        var task = node.Task;
        double progress = node.Summary.ProgressPercent;
        bool done = task.IsDone || (node.HasChildren && progress >= 100);

        var (planStart, planEnd) = node.HasChildren ? SubtreePlan(node) : Plan(task);
        var (actualStart, actualEnd, forecast) = node.HasChildren ? SubtreeActual(node, today) : Actual(task, today);

        return new GanttRow
        {
            Node = node,
            Index = index,
            Kind = node.HasChildren ? GanttRowKind.Parent : GanttRowKind.Task,
            PlanStart = planStart,
            FrameEnd = PlanFrame.OverrunEnd(node) is not null ? task.Target : null,
            FrameStart = PlanFrame.EarlyStart(node) is not null ? task.Start : null,
            PlanEnd = planEnd,
            ActualStart = actualStart,
            ActualEnd = actualEnd,
            IsForecast = forecast,
            ProgressPercent = progress,
            IsDone = done,
            Inazuma = !task.IsCanceled && planStart is { } ps && planEnd is { } pe ? GanttSchedule.InazumaPoint(ps, pe, progress, done, today) : null,

            // 未着手のまま予定終了日を過ぎたタスクも、今日までの日数を遅れとして数える
            DelayDays = planEnd is { } end ? GanttSchedule.DelayWorkingDays(end, actualEnd ?? (done ? end : today)) : 0,
        };
    }

    private static (DateOnly? Start, DateOnly? End) Plan(TaskItem task)
    {
        var start = task.Start ?? task.Target;
        var end = task.Target ?? task.Start;
        return start is { } s && end is { } e && e < s ? (s, s) : (start, end);
    }

    private static (DateOnly? Start, DateOnly? End, bool Forecast) Actual(TaskItem task, DateOnly today)
    {
        var start = task.ActualStart ?? (task.IsDone ? task.ActualEnd : null);
        var end = GanttSchedule.ActualOrForecastEnd(task, today);
        return start is not null && end is not null ? (start, end, !task.IsDone) : (null, null, false);
    }

    /// <summary>親タスクの予定の範囲（親自身の枠と子の予定）。中止した子はもう行わないため、範囲にもイナズマ線の点にも含めない。</summary>
    private static (DateOnly? Start, DateOnly? End) SubtreePlan(TaskNode node)
    {
        var (start, end) = Plan(node.Task);
        foreach (var child in node.ChildNodes.Where(c => !c.Task.IsCanceled))
        {
            var (s, e) = child.HasChildren ? SubtreePlan(child) : Plan(child.Task);
            start = Min(start, s);
            end = Max(end, e);
        }

        return (start, end);
    }

    private static (DateOnly? Start, DateOnly? End, bool Forecast) SubtreeActual(TaskNode node, DateOnly today)
    {
        // 親自身に実績が入っていることもあるため、子と合わせて範囲を取る。ただし見込みは子から決め、親自身の進捗からは求めない
        // （子を足す前に着手したタスクは、親になったあとも当時の進捗を持ち続けるため）
        var own = node.Task;
        DateOnly? start = own.ActualStart ?? (own.IsDone ? own.ActualEnd : null);
        DateOnly? end = own.IsDone ? own.ActualEnd ?? own.ActualStart : null;
        bool forecast = false;
        foreach (var child in node.ChildNodes)
        {
            var (s, e, f) = child.HasChildren ? SubtreeActual(child, today) : Actual(child.Task, today);
            if (s is null)
            {
                continue;
            }

            start = Min(start, s);
            end = Max(end, e);
            forecast |= f;
        }

        // 親だけが着手していて、子に実績がまだなければ、着手した日だけを示す
        return start is null ? (null, null, false) : (start, end ?? start, forecast);
    }

    private static DateOnly? Min(DateOnly? a, DateOnly? b) => a is null ? b : b is null ? a : a < b ? a : b;

    private static DateOnly? Max(DateOnly? a, DateOnly? b) => a is null ? b : b is null ? a : a > b ? a : b;
}
