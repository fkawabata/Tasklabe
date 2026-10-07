using Tasklabe.Core.Domain;
using Tasklabe.Core.Gantt;
using Tasklabe.Core.Milestones;
using Tasklabe.Core.Wbs;

namespace Tasklabe.Core.Tests;

/// <summary>ガントチャートのマイルストーンの行（UI デザイン設計書 3.3.7 節）。</summary>
public class GanttMilestoneRowTests
{
    private static DateOnly D(int m, int d) => new(2026, m, d);

    private static readonly DateOnly Today = D(10, 28);

    private static readonly Milestone Requirements = new("M1", 1, "要件確定", D(10, 16));
    private static readonly Milestone Design = new("M2", 2, "設計完了", D(11, 6));

    private static TaskItem T(string id, string? parent = null, DateOnly? start = null, DateOnly? target = null,
        string? milestone = null, double progress = 0, double? estimate = null) => new()
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
        MilestoneId = milestone,
        ProgressPercent = progress,
        EstimateHours = estimate,
        Kind = TaskKind.Task,
    };

    private static readonly TaskItem[] Plan =
    [
        T("要件定義"),
        T("ヒアリング", "要件定義", D(10, 5), D(10, 9), "M1", 100),
        T("まとめ", "要件定義", D(10, 12), D(10, 16), "M1", 100),
        T("設計"),
        T("画面設計", "設計", D(10, 19), D(10, 30), "M2", 80),
        T("API 設計", "設計", D(10, 26), D(11, 6), "M2", 0),
        T("実装"),
        T("ログイン", "実装", D(11, 9), D(11, 20)),
    ];

    private static IReadOnlyList<GanttRow> Build(TaskItem[] tasks, params Milestone[] milestones)
    {
        // 計画の順は並び順で決まるため、配列の順に並び順を振る
        var tree = TaskTree.Build(tasks.Select((t, i) => t with { SortOrder = i }));
        return GanttModel.Build(tree.All(), Today, tree, MilestonePlan.Summarize(tree, milestones));
    }

    private static string Name(GanttRow row) => row.IsMilestone ? "◆" + row.Milestone!.Milestone.Title : row.Task.Title;

    [Fact]
    public void Milestone_row_follows_the_last_top_level_row_that_ends_by_its_due_date()
    {
        var rows = Build(Plan, Design, Requirements);

        Assert.Equal(
            ["要件定義", "ヒアリング", "まとめ", "◆要件確定", "設計", "画面設計", "API 設計", "◆設計完了", "実装", "ログイン"],
            rows.Select(Name));
        Assert.Equal(Enumerable.Range(0, rows.Count), rows.Select(r => r.Index));
    }

    [Fact]
    public void Milestone_before_every_row_goes_first_and_one_after_every_row_goes_last()
    {
        var early = new Milestone("M0", 3, "キックオフ", D(10, 1));
        var late = new Milestone("M9", 4, "リリース", D(12, 25));

        var rows = Build(Plan, early, late);

        Assert.Equal("◆キックオフ", Name(rows[0]));
        Assert.Equal("◆リリース", Name(rows[^1]));
    }

    [Fact]
    public void Milestone_bar_spans_from_the_first_member_start_to_the_due_date_with_its_progress()
    {
        var row = Build(Plan, Requirements, Design).Single(r => r.IsMilestone && r.Milestone!.Milestone.Id == "M2");

        Assert.Equal(GanttRowKind.Milestone, row.Kind);
        Assert.Equal(D(10, 19), row.PlanStart);
        Assert.Equal(D(11, 6), row.PlanEnd);
        Assert.Equal(40, row.ProgressPercent, 1);   // 工数なし（1 h とみなす）の 80 % と 0 %
        Assert.False(row.IsDone);
    }

    [Fact]
    public void Milestone_without_members_starts_the_day_after_the_previous_one()
    {
        var empty = new Milestone("M3", 5, "空", D(11, 20));

        var row = Build(Plan, Design, empty).Single(r => r.IsMilestone && r.Milestone!.Milestone.Id == "M3");

        Assert.Equal(D(11, 7), row.PlanStart);
        Assert.Null(row.ExpectedPercent);
    }

    [Fact]
    public void Dependencies_keep_pointing_at_task_rows_after_milestone_rows_are_inserted()
    {
        TaskItem[] tasks = [T("a", start: D(10, 5), target: D(10, 9)), T("b", start: D(10, 12), target: D(10, 16)) with { BlockedBy = ["I-a"] }];

        var rows = Build(tasks, new Milestone("M1", 1, "途中", D(10, 10)));

        Assert.Equal(["a", "◆途中", "b"], rows.Select(Name));
        Assert.Equal([0], rows[2].Predecessors);
        Assert.Equal("milestone:M1", rows[1].Key);
    }

    [Fact]
    public void Expected_progress_spreads_each_estimate_evenly_over_its_planned_days()
    {
        // 10 日の予定の 5 日目まで（重み 30）と、まだ始まっていない予定（重み 10）
        var expected = MilestonePlan.ExpectedPercent(
            [T("x", start: D(10, 1), target: D(10, 10), estimate: 30), T("y", start: D(11, 1), target: D(11, 5), estimate: 10)],
            D(10, 5));

        Assert.Equal(37.5, expected!.Value, 3);
    }
}
