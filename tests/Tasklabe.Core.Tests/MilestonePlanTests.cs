using Tasklabe.Core.Domain;
using Tasklabe.Core.Milestones;
using Tasklabe.Core.Wbs;

namespace Tasklabe.Core.Tests;

/// <summary>マイルストーンとタスクの関係（要件 F-MS-02〜04）。</summary>
public class MilestonePlanTests
{
    private static DateOnly D(int month, int day) => new(2026, month, day);

    private static readonly Milestone Requirements = new("M1", 1, "要件確定", D(10, 16));
    private static readonly Milestone Design = new("M2", 2, "設計完了", D(10, 30));
    private static readonly Milestone[] Both = [Design, Requirements];

    private static TaskItem T(string id, DateOnly? target, string? milestone = null, TaskKind kind = TaskKind.Task, string? parent = null,
        double? estimate = null, double progress = 0, StatusCategory category = StatusCategory.Todo) => new()
    {
        ItemId = id,
        ProjectId = "P",
        IssueId = "I" + id,
        RepositoryNameWithOwner = "o/r",
        Number = 1,
        Title = id,
        Target = target,
        MilestoneId = milestone,
        Kind = kind,
        ParentIssueId = parent,
        EstimateHours = estimate,
        ProgressPercent = progress,
        Category = category,
    };

    [Theory]
    [InlineData(10, 5, "M1")]
    [InlineData(10, 16, "M1")]   // 期日の当日はそのマイルストーン
    [InlineData(10, 17, "M2")]
    [InlineData(10, 31, null)]   // 最後のマイルストーンより後
    public void Task_belongs_to_the_first_milestone_on_or_after_its_due_date(int month, int day, string? expected)
    {
        Assert.Equal(expected, MilestonePlan.Auto(D(month, day), Both)?.Id);
    }

    [Fact]
    public void Task_without_due_date_and_milestones_without_dates_do_not_match()
    {
        Assert.Null(MilestonePlan.Auto(null, Both));
        Assert.Null(MilestonePlan.Auto(D(10, 1), [new Milestone("X", 9, "未定", null)]));
    }

    [Fact]
    public void Changing_the_due_date_keeps_the_milestone()
    {
        // M1 に入っていたものは、期日を M1 の後（10/20）へずらしても M1 のまま（期日超えとして示す）
        Assert.Equal("M1", MilestonePlan.Follow("M1", D(10, 10), D(10, 20), Both));

        // 手で M2 を選んでいたものも、期日を変えても M2 のまま
        Assert.Equal("M2", MilestonePlan.Follow("M2", D(10, 10), D(10, 12), Both));

        // 初めて期日が入ったものだけ、期日から入り先を決める
        Assert.Equal("M1", MilestonePlan.Follow(null, null, D(10, 12), Both));

        // 期日があってどこにも入っていないもの（なしを選んだ、最後のマイルストーンより後）は、期日を変えても入れない
        Assert.Null(MilestonePlan.Follow(null, D(11, 5), D(10, 12), Both));
    }

    [Fact]
    public void Adding_or_removing_a_milestone_moves_only_matching_members()
    {
        Milestone[] withDesignOnly = [Design];

        // M1 を消すと、M1 にいたものは期日から決め直して M2 へ。M2 にいたものはそのまま
        Assert.Equal("M2", MilestonePlan.Reassign("M1", D(10, 10), Both, withDesignOnly));
        Assert.Equal("M2", MilestonePlan.Reassign("M2", D(10, 10), Both, withDesignOnly));

        // M1 を足すと、期日どおり M2 にいた 10/10 のものは M1 へ
        Assert.Equal("M1", MilestonePlan.Reassign("M2", D(10, 10), withDesignOnly, Both));

        // 期日を超えて M2 にいるもの（期日 11/5）は、M1 を足しても動かない
        Assert.Equal("M2", MilestonePlan.Reassign("M2", D(11, 5), withDesignOnly, Both));
    }

    [Fact]
    public void Changing_a_milestone_due_date_keeps_its_members()
    {
        // M1 の期日を 10/16 から 10/9 へ早めても、10/10 に終わる M1 のタスクは M2 へ押し出されない
        Milestone[] earlier = [new("M1", 1, "要件確定", D(10, 9)), Design];
        Assert.Equal("M1", MilestonePlan.Reassign("M1", D(10, 10), Both, earlier));
    }

    [Fact]
    public void Summary_counts_tasks_ending_after_the_milestone_as_late()
    {
        var tree = TaskTree.Build(
        [
            T("review", D(10, 21), "M1"),                                    // 期日 10/16 を 3 稼働日超える
            T("spec", D(10, 19), "M1"),
            T("done", D(10, 23), "M1", category: StatusCategory.Done),     // 終わったものは数えない
            T("ok", D(10, 16), "M1"),
        ]);

        var summary = MilestonePlan.Summarize(tree, Both)[0];

        Assert.Equal(2, summary.Late);
        Assert.Equal(D(10, 21), summary.LateEnd);
        Assert.Equal(3, summary.LateDays);
    }

    [Fact]
    public void Only_planned_tasks_have_milestones()
    {
        Assert.True(MilestonePlan.Applies(T("a", D(10, 1))));
        Assert.False(MilestonePlan.Applies(T("b", D(10, 1), kind: TaskKind.Issue)));
    }

    [Fact]
    public void Summary_counts_leaf_tasks_of_each_milestone()
    {
        var tree = TaskTree.Build(
        [
            T("phase", D(10, 16), "M1"),
            T("a", D(10, 9), "M1", parent: "Iphase", estimate: 8, category: StatusCategory.Done),
            T("b", D(10, 16), "M1", parent: "Iphase", estimate: 8, progress: 50),
            T("c", D(10, 16), "M1", parent: "Iphase", category: StatusCategory.Canceled),
            T("d", D(10, 30), "M2", estimate: 4),
        ]);

        var summaries = MilestonePlan.Summarize(tree, Both);

        Assert.Equal(["M1", "M2"], summaries.Select(s => s.Milestone.Id));
        Assert.Equal(2, summaries[0].Total);   // 親タスクと中止は数えない
        Assert.Equal(1, summaries[0].Done);
        Assert.Equal(75, summaries[0].Progress.ProgressPercent);
        Assert.Equal(0, summaries[1].Done);
    }

    [Fact]
    public void Project_sees_its_own_milestones_and_untagged_ones_its_tasks_use()
    {
        // 同じリポジトリを使う 2 つのプロジェクト（P と Q）の Milestone
        Milestone own = new("M1", 1, "要件確定", D(10, 16), MilestonePlan.WithOwner("説明", "P"));
        Milestone other = new("M2", 2, "β リリース", D(10, 30), MilestonePlan.WithOwner(null, "Q"));
        Milestone usedOnGitHub = new("M3", 3, "GitHub で作った", D(11, 6));
        Milestone unusedOnGitHub = new("M4", 4, "使っていない", D(11, 13));
        TaskItem[] tasks = [T("a", D(11, 1), milestone: "M3"), T("b", D(10, 20), milestone: "M2")];

        var visible = MilestonePlan.ForProject("P", [own, other, usedOnGitHub, unusedOnGitHub], tasks);

        Assert.Equal(["M1", "M3"], visible.Select(m => m.Id));
    }

    [Fact]
    public void Owner_marker_is_added_once_after_the_description()
    {
        Assert.Equal("[tasklabe:project:P]", MilestonePlan.WithOwner(null, "P"));
        var described = MilestonePlan.WithOwner("リリースの判定", "P");
        Assert.Equal("リリースの判定\n\n[tasklabe:project:P]", described);
        Assert.Equal(described, MilestonePlan.WithOwner(described, "P"));
        Assert.False(MilestonePlan.NeedsOwner(new Milestone("M", 1, "x", null, described), "P"));
        Assert.True(MilestonePlan.NeedsOwner(new Milestone("M", 1, "x", null, described), "Q"));
    }
}
