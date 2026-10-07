using Tasklabe.Core.Domain;
using Tasklabe.Core.Wbs;

namespace Tasklabe.Core.Tests;

/// <summary>親タスクの予定（枠）と子の予定の関係（PlanFrame）。</summary>
public class PlanFrameTests
{
    private static DateOnly D(int month, int day) => new(2026, month, day);

    private static TaskItem T(string id, DateOnly? start, DateOnly? target, string? parent = null,
        StatusCategory category = StatusCategory.Todo) => new()
    {
        ItemId = id,
        ProjectId = "P",
        IssueId = "I" + id,
        RepositoryNameWithOwner = "o/r",
        Number = 1,
        Title = id,
        ParentIssueId = parent is null ? null : "I" + parent,
        Start = start,
        Target = target,
        Category = category,
    };

    private static TaskNode Phase(params TaskItem[] tasks) => TaskTree.Build(tasks).Find("p")!;

    [Fact]
    public void Child_ending_after_the_frame_is_an_overrun()
    {
        // 要件定義の枠は 10/23(金)。子のレビューは 10/27(火) → 2 稼働日（10/26、10/27）超える
        var phase = Phase(T("p", D(10, 5), D(10, 23)), T("a", D(10, 5), D(10, 20), "p"), T("review", D(10, 27), D(10, 27), "p"));

        Assert.Equal(D(10, 27), PlanFrame.OverrunEnd(phase));
        Assert.Equal(2, PlanFrame.OverrunDays(phase));
    }

    [Fact]
    public void Children_within_the_frame_or_without_a_frame_are_not_overruns()
    {
        Assert.Null(PlanFrame.OverrunEnd(Phase(T("p", D(10, 5), D(10, 23)), T("a", D(10, 5), D(10, 23), "p"))));
        Assert.Null(PlanFrame.OverrunEnd(Phase(T("p", null, null), T("a", D(10, 5), D(10, 30), "p"))));
    }

    [Fact]
    public void Child_starting_before_the_frame_is_an_overrun_too()
    {
        // 基本設計の枠は 10/28(水) から。子が 10/26(月) から始まる → 2 稼働日（10/26、10/27）前にはみ出す
        var phase = Phase(T("p", D(10, 28), D(11, 17)), T("a", D(10, 26), D(10, 30), "p"));

        Assert.Equal(D(10, 26), PlanFrame.EarlyStart(phase));
        Assert.Equal(2, PlanFrame.EarlyDays(phase));
        Assert.Null(PlanFrame.OverrunEnd(phase));
    }

    [Fact]
    public void Grandchildren_count_and_canceled_tasks_do_not()
    {
        var phase = Phase(
            T("p", D(10, 5), D(10, 23)),
            T("m", null, null, "p"),
            T("deep", D(10, 26), D(10, 26), "m"),
            T("dropped", D(10, 30), D(10, 30), "p", StatusCategory.Canceled));

        Assert.Equal(D(10, 26), PlanFrame.OverrunEnd(phase));
    }
}
