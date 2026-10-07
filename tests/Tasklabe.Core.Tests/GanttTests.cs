using Tasklabe.Core.Calendar;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Gantt;
using Tasklabe.Core.Wbs;

namespace Tasklabe.Core.Tests;

public class GanttTests
{
    private static DateOnly D(int m, int d) => new(2026, m, d);

    // 2026-09-14 (月) 〜 09-18 (金) は稼働日。09-21〜23 は祝日
    private static readonly DateOnly Today = D(9, 16);

    private static TaskItem T(string id, string? parent = null, DateOnly? start = null, DateOnly? target = null,
        DateOnly? actualStart = null, DateOnly? actualEnd = null, double progress = 0, StatusCategory category = StatusCategory.Todo,
        TaskKind kind = TaskKind.Task, params string[] blockedBy) => new()
    {
        ItemId = id,
        ProjectId = "P1",
        IssueId = "I-" + id,
        RepositoryNameWithOwner = "org/repo",
        Number = 1,
        Title = id,
        ParentIssueId = parent is null ? null : "I-" + parent,
        Start = start,
        Target = target,
        ActualStart = actualStart,
        ActualEnd = actualEnd,
        ProgressPercent = progress,
        Category = category,
        Kind = kind,
        BlockedBy = blockedBy.Select(b => "I-" + b).ToList(),
    };

    private static IReadOnlyList<GanttRow> Build(params TaskItem[] tasks) => GanttModel.Build(TaskTree.Build(tasks).All(), Today);

    [Fact]
    public void Forecast_prorates_remaining_working_days_from_progress()
    {
        // 9/14〜9/16 で 3 稼働日、進捗 50 % なら残り 3 稼働日 → 9/17, 9/18, 9/24（9/19〜23 は休日）
        Assert.Equal(D(9, 24), GanttSchedule.ForecastEnd(D(9, 14), 50, 5, Today));
    }

    [Fact]
    public void Forecast_without_progress_uses_planned_working_days_and_never_precedes_today()
    {
        Assert.Equal(D(9, 18), GanttSchedule.ForecastEnd(D(9, 14), 0, 5, Today));
        Assert.Equal(Today, GanttSchedule.ForecastEnd(D(9, 1), 0, 2, Today));
        Assert.Equal(Today, GanttSchedule.ForecastEnd(D(9, 14), 100, 5, Today));
    }

    [Fact]
    public void Inazuma_point_moves_by_progress_within_plan()
    {
        // 9/14〜9/17 の 4 日間で 50 % → 9/16 の始まり
        Assert.Equal(D(9, 16).DayNumber, GanttSchedule.InazumaPoint(D(9, 14), D(9, 17), 50, false, Today));
        Assert.Equal(D(9, 14).DayNumber + 1, GanttSchedule.InazumaPoint(D(9, 14), D(9, 17), 25, false, Today));
    }

    [Fact]
    public void Inazuma_point_is_today_for_done_or_not_yet_due_tasks()
    {
        Assert.Equal(Today.DayNumber, GanttSchedule.InazumaPoint(D(9, 1), D(9, 5), 100, true, Today));
        Assert.Equal(D(9, 26).DayNumber, GanttSchedule.InazumaPoint(D(9, 20), D(9, 25), 100, true, Today));
        Assert.Equal(Today.DayNumber, GanttSchedule.InazumaPoint(D(9, 20), D(9, 25), 0, false, Today));
    }

    [Fact]
    public void Canceled_task_is_left_off_the_inazuma_line()
    {
        // 中止したタスクは進み具合の点を持たず、線は前後の行の点を直接結ぶ
        var rows = Build(
            T("a", start: D(9, 14), target: D(9, 17), progress: 50),
            T("b", start: D(9, 14), target: D(9, 30), category: StatusCategory.Canceled));
        Assert.True(rows[0].IsOnInazuma);
        Assert.NotNull(rows[0].Inazuma);
        Assert.False(rows[1].IsOnInazuma);
        Assert.Null(rows[1].Inazuma);
    }

    [Fact]
    public void Parent_plan_range_leaves_out_canceled_children()
    {
        // 中止した子（9/30 まで）は親の範囲を広げず、親の点も残りの子の範囲から求める
        var rows = Build(
            T("p"),
            T("a", parent: "p", start: D(9, 14), target: D(9, 17), progress: 50),
            T("b", parent: "p", start: D(9, 14), target: D(9, 30), category: StatusCategory.Canceled));
        Assert.Equal(D(9, 14), rows[0].PlanStart);
        Assert.Equal(D(9, 17), rows[0].PlanEnd);
        Assert.Equal(rows[1].Inazuma, rows[0].Inazuma);
    }

    [Fact]
    public void Row_uses_forecast_end_and_reports_delay()
    {
        var row = Build(T("a", start: D(9, 14), target: D(9, 16), actualStart: D(9, 14), progress: 50)).Single();
        Assert.Equal(GanttRowKind.Task, row.Kind);
        Assert.Equal(D(9, 24), row.ActualEnd);
        Assert.True(row.IsForecast);
        Assert.Equal(3, row.DelayDays);
    }

    [Fact]
    public void Parent_row_spans_children_plans_and_actuals()
    {
        var rows = Build(
            T("p"),
            T("a", parent: "p", start: D(9, 1), target: D(9, 4), actualStart: D(9, 1), actualEnd: D(9, 3), category: StatusCategory.Done),
            T("b", parent: "p", start: D(9, 7), target: D(9, 11)));
        var parent = rows[0];
        Assert.Equal(GanttRowKind.Parent, parent.Kind);
        Assert.Equal((D(9, 1), D(9, 11)), (parent.PlanStart!.Value, parent.PlanEnd!.Value));
        Assert.Equal((D(9, 1), D(9, 3)), (parent.ActualStart!.Value, parent.ActualEnd!.Value));
        Assert.False(parent.IsDone);
    }

    [Fact]
    public void Parent_range_ignores_the_forecast_from_its_own_progress()
    {
        // 着手して進捗 10 % のまま子を足した親。親自身の進捗から求めた見込み（ずっと先）を、範囲に含めない
        var rows = Build(
            T("p", start: D(9, 7), target: D(9, 18), actualStart: D(9, 7), progress: 10, category: StatusCategory.InProgress),
            T("a", parent: "p", start: D(9, 14), target: D(9, 18), actualStart: D(9, 14), progress: 60, category: StatusCategory.InProgress));
        Assert.Equal(D(9, 7), rows[0].ActualStart);
        Assert.Equal(rows[1].ActualEnd, rows[0].ActualEnd);
    }

    [Fact]
    public void Predecessors_resolve_to_visible_rows()
    {
        var rows = Build(T("a"), T("b", blockedBy: ["a", "missing"]));
        Assert.Equal([0], rows[1].Predecessors);
    }

    [Fact]
    public void Arrows_of_expanded_parents_go_through_the_children_that_set_their_ends()
    {
        // p → q。p の終わりは孫 a2 が決め、q の始まりは子 b1 が決める。後続を待たせない a3 と中止した b0 は端にしない
        var rows = Build(
            T("p"),
            T("a", parent: "p"),
            T("a1", parent: "a", start: D(9, 1), target: D(9, 4)),
            T("a2", parent: "a", start: D(9, 7), target: D(9, 11)),
            T("a3", parent: "p", start: D(9, 7), target: D(9, 30)) with { NonBlocking = true },
            T("q", blockedBy: ["p"]),
            T("b0", parent: "q", start: D(9, 1), target: D(9, 2), category: StatusCategory.Canceled),
            T("b1", parent: "q", start: D(9, 14), target: D(9, 15)),
            T("b2", parent: "q", start: D(9, 16), target: D(9, 18)));
        int Row(string id) => rows.Single(r => r.Task.ItemId == id).Index;

        Assert.Equal(Row("a2"), rows[Row("p")].ArrowFrom);
        Assert.Equal(Row("b1"), rows[Row("q")].ArrowTo);
        Assert.Equal(Row("a1"), rows[Row("a1")].ArrowFrom);
    }

    [Fact]
    public void Arrows_go_to_the_child_whose_drawn_bar_starts_first()
    {
        // 完了したタスクのバーは予定だけで描くため、予定より前の実績の開始では比べない
        var rows = Build(
            T("p"),
            T("q", blockedBy: ["p"]),
            T("b1", parent: "q", start: D(9, 28), target: D(10, 2), actualStart: D(9, 1), actualEnd: D(9, 1), category: StatusCategory.Done),
            T("b2", parent: "q", start: D(9, 21), target: D(9, 25)));
        Assert.Equal(rows.Single(r => r.Task.ItemId == "b2").Index, rows.Single(r => r.Task.ItemId == "q").ArrowTo);
    }

    [Fact]
    public void Phase_reading_separates_pace_from_the_task_that_pushes_the_end()
    {
        // 量は予定どおり（a は完了、b は予定どおり進行）でも、c の見込みが親の予定の終わりを超える
        var rows = Build(
            T("p"),
            T("a", parent: "p", start: D(9, 7), target: D(9, 11), actualStart: D(9, 7), actualEnd: D(9, 11), progress: 100, category: StatusCategory.Done),
            T("b", parent: "p", start: D(9, 14), target: D(9, 18), actualStart: D(9, 14), progress: 60, category: StatusCategory.InProgress),
            T("c", parent: "p", start: D(9, 14), target: D(9, 18), actualStart: D(9, 15), progress: 10, category: StatusCategory.InProgress));
        var reading = GanttModel.ReadPhase(rows[0], Today);

        Assert.NotNull(reading);
        Assert.Equal("c", reading.Bottleneck?.ItemId);
        Assert.True(reading.BottleneckEnd > rows[0].PlanEnd);
        Assert.Null(GanttModel.ReadPhase(rows[1], Today));
    }

    [Fact]
    public void Phase_reading_has_no_bottleneck_when_every_forecast_fits()
    {
        var rows = Build(
            T("p"),
            T("a", parent: "p", start: D(9, 14), target: D(9, 18), actualStart: D(9, 14), progress: 60, category: StatusCategory.InProgress));
        Assert.Null(GanttModel.ReadPhase(rows[0], Today)!.Bottleneck);
    }

    [Fact]
    public void Arrows_of_collapsed_parents_stay_on_the_parent_row()
    {
        var tree = TaskTree.Build([T("p"), T("a", parent: "p", start: D(9, 1), target: D(9, 4)), T("q", blockedBy: ["p"])]);
        var rows = GanttModel.Build(tree.Flatten(n => n.Task.ItemId != "p"), Today);
        Assert.Equal(0, rows[0].ArrowFrom);
    }

    [Fact]
    public void Row_counts_delay_for_tasks_that_never_started()
    {
        var row = Build(T("a", start: D(9, 7), target: D(9, 11))).Single();
        Assert.Equal(3, row.DelayDays); // 9/14, 9/15, 9/16
        Assert.Null(row.ActualEnd);
    }

    [Fact]
    public void Parent_row_includes_its_own_actual_dates()
    {
        var rows = Build(
            T("p", start: D(9, 1), target: D(9, 11), actualStart: D(9, 2), progress: 30),
            T("a", parent: "p", start: D(9, 7), target: D(9, 11)));

        // 親自身の実績開始日は範囲に含める。子にまだ実績がなければ、親自身の進捗から見込みは出さず、着手した日だけを示す
        Assert.Equal(D(9, 2), rows[0].ActualStart);
        Assert.Equal(D(9, 2), rows[0].ActualEnd);
    }

    [Fact]
    public void Forecast_is_capped_for_barely_started_tasks()
    {
        // 進捗 1 % のまま長く続いても、見込み終了日は上限までしか伸びない
        var forecast = GanttSchedule.ForecastEnd(D(1, 5), 1, 5, Today);
        Assert.True(forecast <= WorkCalendar.NthWorkingDay(Today.AddDays(1), GanttSchedule.MaxForecastWorkingDays));
    }
}
