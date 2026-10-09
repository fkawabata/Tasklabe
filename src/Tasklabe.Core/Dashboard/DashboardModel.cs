using Tasklabe.Core.Calendar;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Gantt;
using Tasklabe.Core.Progress;
using Tasklabe.Core.Wbs;

namespace Tasklabe.Core.Dashboard;

/// <summary>注意が必要なタスクの種類（要件 F-UI-DB-02）。</summary>
public enum TaskAttention
{
    /// <summary>予定終了日を過ぎた、または見込みが予定終了日を超えたタスク。</summary>
    Delayed,

    /// <summary>期限が近いタスク。</summary>
    DueSoon,

    /// <summary>想定工数が未入力のタスク。</summary>
    Unestimated,
}

/// <param name="ScheduleDays">予定比（日）。負なら遅れ、正なら前倒し。</param>
public sealed record DashboardProject(
    Project Project,
    ProgressSummary Summary,
    double ScheduleDays,
    int DelayedCount,
    int IssueCount);

public sealed record AttentionTask(TaskItem Task, string ProjectTitle, TaskAttention Attention, int DelayDays);

public sealed record WorkloadEntry(TaskItem Task, double Hours);

/// <summary>ある担当者の、ある日の割り当て。</summary>
/// <param name="Capacity">1 日に割り当てられる工数の上限（h）。プロジェクトの「1 日の稼働時間」。</param>
public sealed record WorkloadCell(IReadOnlyList<WorkloadEntry> Entries, double Capacity = DashboardModel.DefaultDailyCapacityHours)
{
    public double Hours => Entries.Sum(e => e.Hours);

    public bool IsOverloaded => Hours > Capacity + 0.01;
}

/// <param name="Cells">日ごとの割り当て（<see cref="DashboardView.WorkloadDates"/> と同じ並び）。</param>
public sealed record WorkloadRow(string Login, IReadOnlyList<WorkloadCell> Cells)
{
    public double Total => Cells.Sum(c => c.Hours);
}

public sealed record DashboardView(
    ProgressSummary Summary,
    int DelayedCount,
    int DueThisWeekCount,
    int UnestimatedCount,
    int IssueCount,
    IReadOnlyList<DashboardProject> Projects,
    IReadOnlyList<AttentionTask> Attention,
    IReadOnlyList<DateOnly> WorkloadDates,
    IReadOnlyList<WorkloadRow> Workload)
{
    public static readonly DashboardView Empty = new(default, 0, 0, 0, 0, [], [], [], []);
}

/// <summary>プロジェクトの状況の集計（要件 F-UI-DB-01〜04）。</summary>
public static class DashboardModel
{
    /// <summary>1 日に割り当てられる工数の目安（h）の既定。これを超える日を過負荷として示す。</summary>
    public const double DefaultDailyCapacityHours = 8;

    /// <summary>負荷を表示する日数。</summary>
    public const int WorkloadDays = 14;

    /// <summary>期限が近いとみなす日数。</summary>
    public const int DueSoonDays = 7;

    public const string Unassigned = "";

    /// <param name="issueCounts">プロジェクトごとの課題の件数（計画に入っていないタスク）。</param>
    /// <param name="dailyCapacity">1 日に割り当てられる工数の上限（h）。これを超える日を過負荷とする。</param>
    public static DashboardView Build(IReadOnlyList<(Project Project, TaskTree Tree)> projects, DateOnly today,
        IReadOnlyDictionary<string, int>? issueCounts = null, double dailyCapacity = DefaultDailyCapacityHours)
    {
        ArgumentNullException.ThrowIfNull(projects);

        if (projects.Count == 0)
        {
            return DashboardView.Empty;
        }

        var rows = new List<DashboardProject>();
        var attention = new List<AttentionTask>();
        var dates = Enumerable.Range(0, WorkloadDays).Select(today.AddDays).ToList();
        var workload = new Dictionary<string, List<WorkloadEntry>[]>(StringComparer.OrdinalIgnoreCase);
        var weekEnd = today.AddDays(6 - ((int)today.DayOfWeek + 6) % 7); // 今週の日曜
        int dueThisWeek = 0;
        int unestimated = 0;

        foreach (var (project, tree) in projects)
        {
            var leaves = tree.All().Where(n => !n.HasChildren).Select(n => n.Task).ToList();
            int delayed = 0;

            foreach (var task in leaves)
            {
                if (Classify(task, today) is { } kind)
                {
                    int delayDays = kind == TaskAttention.Delayed ? DelayOf(task, today) : 0;
                    attention.Add(new AttentionTask(task, project.Title, kind, delayDays));
                    if (kind == TaskAttention.Delayed)
                    {
                        delayed++;
                    }
                }

                if (!task.IsDone && task.Target is { } target && target >= today && target <= weekEnd)
                {
                    dueThisWeek++;
                }

                if (!task.IsDone && task.EstimateHours is null && task.Kind == TaskKind.Task)
                {
                    unestimated++;
                }

                AddWorkload(workload, task, dates, today);
            }

            rows.Add(new DashboardProject(project, tree.Summary, ScheduleDays(leaves, today), delayed,
                issueCounts?.GetValueOrDefault(project.Id) ?? 0));
        }

        return new DashboardView(
            ProgressCalculator.SummarizeAll(projects.SelectMany(p => p.Tree.Roots)),
            rows.Sum(r => r.DelayedCount),
            dueThisWeek,
            unestimated,
            rows.Sum(r => r.IssueCount),
            [.. rows.OrderBy(r => r.ScheduleDays).ThenByDescending(r => r.Summary.RemainingHours)],
            [.. attention.OrderBy(a => a.Attention).ThenByDescending(a => a.DelayDays).ThenBy(a => a.Task.Target ?? DateOnly.MaxValue)],
            dates,
            [.. workload
                .Select(w => new WorkloadRow(w.Key, [.. w.Value.Select(day => new WorkloadCell(day, dailyCapacity > 0 ? dailyCapacity : DefaultDailyCapacityHours))]))
                .Where(w => w.Total > 0)

                // 担当を付け替えても行が入れ替わらないよう、負荷の大きさではなく名前の順に並べ、未割り当てを末尾に置く
                .OrderBy(w => w.Login == Unassigned)
                .ThenBy(w => People.Name(w.Login), StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>注意が必要なタスクか。該当しなければ null。</summary>
    public static TaskAttention? Classify(TaskItem task, DateOnly today)
    {
        if (task.IsDone)
        {
            return null;
        }

        if (DelayOf(task, today) > 0)
        {
            return TaskAttention.Delayed;
        }

        if (task.Target is { } target && target <= today.AddDays(DueSoonDays))
        {
            return TaskAttention.DueSoon;
        }

        return task.EstimateHours is null && task.Kind == TaskKind.Task ? TaskAttention.Unestimated : null;
    }

    /// <summary>予定終了日からの遅れ（稼働日）。遅れていなければ 0。</summary>
    public static int DelayOf(TaskItem task, DateOnly today)
    {
        if (task.IsDone || task.Target is not { } target)
        {
            return 0;
        }

        // 着手済みなら見込み終了日、未着手なら今日と比べる
        var end = GanttSchedule.ActualOrForecastEnd(task, today) ?? today;
        return GanttSchedule.DelayWorkingDays(target, end);
    }

    /// <summary>
    /// 予定比（日）。予定どおりに進めば今日までに得られるはずの出来高と、実際の出来高が
    /// 釣り合う日を求め、その日と今日との差で表す。負なら遅れ、正なら前倒し。
    /// </summary>
    public static double ScheduleDays(IReadOnlyList<TaskItem> leaves, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(leaves);

        var planned = leaves
            .Where(t => !t.IsCanceled && t.Start is not null && t.Target is not null && t.Target >= t.Start)
            .Select(t => (Start: t.Start!.Value, End: t.Target!.Value, Weight: Weight(t), Task: t))
            .ToList();
        if (planned.Count == 0)
        {
            return 0;
        }

        var first = planned.Min(p => p.Start);
        var last = planned.Max(p => p.End);
        int days = last.DayNumber - first.DayNumber + 2;

        // 予定の出来高の累積曲線。各タスクの工数を予定期間へ均等に割り付ける
        var curve = new double[days];
        foreach (var (start, end, weight, _) in planned)
        {
            double perDay = weight / (end.DayNumber - start.DayNumber + 1);
            for (int d = start.DayNumber - first.DayNumber; d <= end.DayNumber - first.DayNumber; d++)
            {
                curve[d + 1] += perDay;
            }
        }

        for (int d = 1; d < days; d++)
        {
            curve[d] += curve[d - 1];
        }

        // 出来高は、予定のあるタスクだけで比べる（予定のないタスクは計画曲線に含まれないため）
        double earned = planned.Sum(p => p.Weight * (p.Task.IsDone ? 100 : Math.Clamp(p.Task.ProgressPercent, 0, 100)) / 100);
        double total = curve[days - 1];
        if (total <= 0)
        {
            return 0;
        }

        // まだ出来高がない場合は、予定開始日を過ぎたぶんだけ遅れているとみなす
        if (earned <= 0)
        {
            return Math.Min(0, first.DayNumber - today.DayNumber);
        }

        // 出来高が釣り合う日を線形に補間して求める
        double reached = days - 1;
        for (int d = 0; d < days; d++)
        {
            if (curve[d] >= earned)
            {
                double previous = d == 0 ? 0 : curve[d - 1];
                reached = d - 1 + (curve[d] - previous is var step && step > 0 ? (earned - previous) / step : 0);
                break;
            }
        }

        return first.DayNumber + reached - today.DayNumber;
    }

    private static double Weight(TaskItem task) => task.EstimateHours ?? ProgressCalculator.UnestimatedWeight;

    /// <summary>残工数を、これからの稼働日と担当者へ割り振る（要件 F-UI-DB-03）。</summary>
    private static void AddWorkload(Dictionary<string, List<WorkloadEntry>[]> workload, TaskItem task, IReadOnlyList<DateOnly> dates, DateOnly today)
    {
        if (task.IsDone)
        {
            return;
        }

        double remaining = Weight(task) * (100 - Math.Clamp(task.ProgressPercent, 0, 100)) / 100;
        if (remaining <= 0)
        {
            return;
        }

        var last = dates[^1];
        var from = Max(task.ActualStart ?? task.Start ?? today, today);
        var to = task.Target is { } target && target >= from ? target : from;
        if (from > last)
        {
            return;
        }

        // 期限を過ぎているタスクは、残りを今日に積む
        var workingDays = Enumerable.Range(0, to.DayNumber - from.DayNumber + 1)
            .Select(from.AddDays)
            .Where(WorkCalendar.IsWorkingDay)
            .ToList();
        if (workingDays.Count == 0)
        {
            workingDays = [from];
        }

        double perDay = remaining / workingDays.Count;
        var logins = task.Assignees.Count > 0 ? task.Assignees : [Unassigned];
        foreach (var login in logins)
        {
            if (!workload.TryGetValue(login, out var days))
            {
                workload[login] = days = [.. Enumerable.Range(0, dates.Count).Select(_ => new List<WorkloadEntry>())];
            }

            foreach (var day in workingDays)
            {
                int index = day.DayNumber - dates[0].DayNumber;
                if (index >= 0 && index < days.Length)
                {
                    days[index].Add(new WorkloadEntry(task, perDay / logins.Count));
                }
            }
        }
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;
}
