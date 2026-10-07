using Tasklabe.Core.Calendar;
using Tasklabe.Core.Wbs;

namespace Tasklabe.Core.Tests;

/// <summary>計画をまとめて入力するときに自動で入れる値（要件 F-UI-WBS-05）。2026/10/12 はスポーツの日。</summary>
[Collection(typeof(WorkCalendarCollection))]
public class BulkPlanTests
{
    private static readonly DateOnly Oct5 = new(2026, 10, 5);

    public BulkPlanTests() => WorkCalendar.Current = WorkCalendarRules.Standard;

    private static DateOnly D(int month, int day) => new(2026, month, day);

    [Fact]
    public void Next_row_starts_on_the_working_day_after_the_previous_end()
    {
        var rows = BulkPlan.Resolve(
        [
            new(BulkKind.Parent, "要件定義", Oct5, D(10, 9)),
            new(BulkKind.Parent, "設計", End: D(10, 23)),
        ], BulkStart.AfterPrevious, link: true);

        // 10/9(金) の翌稼働日は、土日とスポーツの日を飛ばして 10/13(火)
        Assert.Equal(D(10, 13), rows[1].Start);
        Assert.True(rows[1].StartIsAuto);
        Assert.False(rows[0].StartIsAuto);
        Assert.Equal(5, rows[0].WorkingDays);
    }

    [Fact]
    public void Chosen_start_is_kept_and_blank_mode_leaves_it_empty()
    {
        var chosen = BulkPlan.Resolve(
        [
            new(BulkKind.Parent, "a", Oct5, D(10, 9)),
            new(BulkKind.Parent, "b", D(10, 20)),
        ], BulkStart.AfterPrevious, link: true);
        var blank = BulkPlan.Resolve(
        [
            new(BulkKind.Parent, "a", Oct5, D(10, 9)),
            new(BulkKind.Parent, "b"),
        ], BulkStart.Blank, link: true);

        Assert.Equal(D(10, 20), chosen[1].Start);
        Assert.False(chosen[1].StartIsAuto);
        Assert.Null(blank[1].Start);
    }

    [Fact]
    public void Milestone_is_dated_at_the_end_of_the_previous_parent_and_has_no_dependencies()
    {
        var rows = BulkPlan.Resolve(
        [
            new(BulkKind.Parent, "設計", Oct5, D(10, 30)),
            new(BulkKind.Milestone, "設計レビュー"),
            new(BulkKind.Parent, "実装"),
        ], BulkStart.AfterPrevious, link: true);

        // 節目の期日は前の親タスクの終わり。開始は持たない
        Assert.Null(rows[1].Start);
        Assert.Equal(D(10, 30), rows[1].End);
        Assert.True(rows[1].EndIsAuto);
        Assert.Null(rows[1].Predecessor);
        Assert.False(rows[1].CanLink);

        // 次の親タスクは、節目を飛ばして前の親タスクにつなぎ、その終わりから始める
        Assert.Equal(0, rows[2].Predecessor);
        Assert.Equal(D(11, 2), rows[2].Start);
    }

    [Fact]
    public void Milestone_with_a_later_date_does_not_move_the_next_parent()
    {
        var rows = BulkPlan.Resolve(
        [
            new(BulkKind.Parent, "設計", Oct5, D(10, 9)),
            new(BulkKind.Milestone, "顧客の承認", End: D(10, 20)),
            new(BulkKind.Parent, "実装"),
        ], BulkStart.AfterPrevious, link: true);

        Assert.False(rows[1].EndIsAuto);
        Assert.Equal(D(10, 13), rows[2].Start);
    }

    [Fact]
    public void Rows_are_linked_to_the_row_above_unless_unlinked_or_turned_off()
    {
        BulkRow[] input =
        [
            new(BulkKind.Parent, "a", Oct5, D(10, 9)),
            new(BulkKind.Parent, ""),
            new(BulkKind.Parent, "b"),
            new(BulkKind.Parent, "c", Unlinked: true),
            new(BulkKind.Parent, "d"),
        ];

        var linked = BulkPlan.Resolve(input, BulkStart.AfterPrevious, link: true);
        var off = BulkPlan.Resolve(input, BulkStart.AfterPrevious, link: false);

        Assert.Equal(["a", "b", "c", "d"], linked.Select(r => r.Title));
        Assert.Null(linked[0].Predecessor);
        Assert.Equal(0, linked[1].Predecessor);   // 名前のない行を飛ばして a につなぐ
        Assert.Null(linked[2].Predecessor);       // ✕ で外した
        Assert.True(linked[2].CanLink);
        Assert.Equal(3, linked[3].Predecessor);
        Assert.All(off, r => Assert.Null(r.Predecessor));
    }

    [Fact]
    public void End_before_start_is_a_problem()
    {
        var rows = BulkPlan.Resolve([new(BulkKind.Parent, "a", D(10, 9), Oct5)], BulkStart.AfterPrevious, link: true);

        Assert.NotNull(rows[0].Problem);
        Assert.Null(rows[0].WorkingDays);
    }

    [Theory]
    [InlineData(BulkSpan.OneWeek, 10, 9)]      // 10/5(月)〜10/11(日) → 休みを除いて 10/9(金)
    [InlineData(BulkSpan.TwoWeeks, 10, 16)]
    [InlineData(BulkSpan.OneMonth, 11, 4)]     // 10/5〜11/4(水)
    public void Span_candidates_count_calendar_weeks_and_months(BulkSpan span, int month, int day)
    {
        Assert.Equal(D(month, day), BulkPlan.EndAfter(Oct5, span));
    }

    [Fact]
    public void Same_as_previous_counts_working_days()
    {
        // 10/13(火) から 5 稼働日目は 10/19(月)
        Assert.Equal(D(10, 19), BulkPlan.EndAfter(D(10, 13), BulkSpan.SameAsPrevious, previousWorkingDays: 5));
        Assert.Null(BulkPlan.EndAfter(D(10, 13), BulkSpan.SameAsPrevious, previousWorkingDays: null));
    }
}
