using Tasklabe.Core.Dashboard;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Wbs;

namespace Tasklabe.Core.Tests;

public class DashboardTests
{
    // 2026-09-14 (月) 〜 09-18 (金) は稼働日、09-19〜09-23 は休日
    private static readonly DateOnly Today = new(2026, 9, 16);

    private static DateOnly D(int m, int d) => new(2026, m, d);

    private static TaskItem T(string id, DateOnly? start = null, DateOnly? target = null, double? estimate = 8,
        double progress = 0, StatusCategory category = StatusCategory.Todo, TaskKind kind = TaskKind.Task,
        DateOnly? actualStart = null, params string[] assignees) => new()
    {
        ItemId = id,
        ProjectId = "P1",
        IssueId = "I-" + id,
        RepositoryNameWithOwner = "org/repo",
        Number = 1,
        Title = id,
        Start = start,
        Target = target,
        ActualStart = actualStart,
        EstimateHours = estimate,
        ProgressPercent = progress,
        Category = category,
        Kind = kind,
        Assignees = assignees,
    };

    private static Project P(string id = "P1", string title = "プロジェクト") => new()
    {
        Id = id,
        Number = 1,
        Title = title,
        Kind = ProjectKind.Team,
        OwnerLogin = "org",
    };

    private static DashboardView Build(params TaskItem[] tasks) => DashboardModel.Build([(P(), TaskTree.Build(tasks))], Today);

    [Fact]
    public void Tasks_are_classified_as_delayed_due_soon_or_unestimated()
    {
        var dashboard = Build(
            T("遅れ", start: D(9, 7), target: D(9, 11)),
            T("期限間近", start: D(9, 14), target: D(9, 18)),
            T("未見積り", estimate: null),
            T("完了", target: D(9, 7), category: StatusCategory.Done),
            T("先の予定", start: D(10, 1), target: D(10, 9)));

        Assert.Equal(
            [("遅れ", TaskAttention.Delayed), ("期限間近", TaskAttention.DueSoon), ("未見積り", TaskAttention.Unestimated)],
            dashboard.Attention.Select(a => (a.Task.Title, a.Attention)));
        Assert.Equal(1, dashboard.DelayedCount);
        Assert.Equal(3, dashboard.Attention[0].DelayDays); // 9/14, 9/15, 9/16
    }

    [Fact]
    public void Due_this_week_counts_targets_up_to_sunday()
    {
        var dashboard = Build(
            T("今週", target: D(9, 18)),
            T("日曜", target: D(9, 20)),
            T("来週", target: D(9, 24)),
            T("過去", target: D(9, 10)));

        Assert.Equal(2, dashboard.DueThisWeekCount);
    }

    [Fact]
    public void Schedule_days_are_negative_when_behind_plan()
    {
        // 9/14〜9/18 の 5 日間で 8h。今日 (9/16) の始まりに予定どおりなら 2 日分の 40 %
        double behind = DashboardModel.ScheduleDays([T("a", start: D(9, 14), target: D(9, 18), progress: 20)], Today);
        Assert.Equal(-1, behind, 3);

        double onPlan = DashboardModel.ScheduleDays([T("a", start: D(9, 14), target: D(9, 18), progress: 40)], Today);
        Assert.Equal(0, onPlan, 3);

        // 予定の最終日まで終わっている場合は、9/19 の始まりまで進んでいることになる
        double ahead = DashboardModel.ScheduleDays([T("a", start: D(9, 14), target: D(9, 18), progress: 100)], Today);
        Assert.Equal(3, ahead, 3);
    }

    [Fact]
    public void Schedule_days_are_zero_without_plans()
    {
        Assert.Equal(0, DashboardModel.ScheduleDays([T("a", estimate: null)], Today));
    }

    [Fact]
    public void Workload_spreads_remaining_hours_over_working_days_and_assignees()
    {
        var dashboard = Build(T("a", start: D(9, 14), target: D(9, 18), estimate: 12, progress: 50, assignees: ["sato", "suzuki"]));

        // 残 6h を 9/16〜9/18 の 3 稼働日と 2 人で分ける → 1 人 1 日 1h
        var sato = dashboard.Workload.Single(w => w.Login == "sato");
        Assert.Equal(3, sato.Total, 3);
        Assert.Equal([1, 1, 1], sato.Cells.Take(3).Select(c => Math.Round(c.Hours, 3)));
        Assert.All(sato.Cells.Skip(3), c => Assert.Empty(c.Entries));
        Assert.Equal(2, dashboard.Workload.Count);
    }

    [Fact]
    public void Workload_rows_are_ordered_by_name_with_unassigned_last()
    {
        // 負荷の大きさ（suzuki が最も大きい）によらず、名前の順に並べ、未割り当てを末尾に置く
        var dashboard = Build(
            T("a", start: D(9, 14), target: D(9, 18), estimate: 40, assignees: ["suzuki"]),
            T("b", start: D(9, 14), target: D(9, 18), estimate: 4, assignees: ["sato"]),
            T("c", start: D(9, 14), target: D(9, 18), estimate: 80));

        Assert.Equal(["sato", "suzuki", DashboardModel.Unassigned], dashboard.Workload.Select(w => w.Login));
    }

    [Fact]
    public void Workload_puts_overdue_work_on_today_and_groups_unassigned()
    {
        var dashboard = Build(T("a", start: D(9, 7), target: D(9, 11), estimate: 4));

        var row = dashboard.Workload.Single();
        Assert.Equal(DashboardModel.Unassigned, row.Login);
        Assert.Equal(4, row.Cells[0].Hours, 3);
        Assert.Equal("a", row.Cells[0].Entries.Single().Task.Title);
    }

    [Fact]
    public void Projects_are_ordered_by_schedule_and_summarized_together()
    {
        var late = TaskTree.Build([T("a", start: D(9, 14), target: D(9, 18), progress: 0)]);
        var ahead = TaskTree.Build([T("b", start: D(9, 14), target: D(9, 18), progress: 100)]);
        var dashboard = DashboardModel.Build([(P("P2", "先行"), ahead), (P("P1", "遅れ"), late)], Today);

        Assert.Equal(["遅れ", "先行"], dashboard.Projects.Select(p => p.Project.Title));
        Assert.Equal(50, dashboard.Summary.ProgressPercent, 3);
        Assert.Equal(8, dashboard.Summary.RemainingHours, 3);
    }

    [Fact]
    public void Schedule_days_ignore_tasks_without_plans()
    {
        // 予定のない完了タスクがあっても、予定のあるタスクの遅れがそのまま表れる
        var leaves = new[]
        {
            T("計画あり", start: D(9, 14), target: D(9, 18), progress: 20),
            T("計画なし", category: StatusCategory.Done),
        };

        Assert.Equal(-1, DashboardModel.ScheduleDays(leaves, Today), 3);
    }

    [Fact]
    public void Schedule_days_do_not_look_ahead_before_the_plan_starts()
    {
        Assert.Equal(0, DashboardModel.ScheduleDays([T("a", start: D(10, 1), target: D(10, 9))], Today), 3);
        Assert.Equal(-2, DashboardModel.ScheduleDays([T("a", start: D(9, 14), target: D(9, 18))], Today), 3);
    }

    [Fact]
    public void Unestimated_count_excludes_finished_tasks()
    {
        var dashboard = Build(
            T("未見積り", estimate: null),
            T("完了", estimate: null, category: StatusCategory.Done));

        Assert.Equal(1, dashboard.UnestimatedCount);
    }
}
