using Tasklabe.Core.Domain;
using Tasklabe.Core.Wbs;

namespace Tasklabe.Core.Tests;

/// <summary>計画のタスクの階層番号（要件 F-TSK-14）。</summary>
public class OutlineNumbersTests
{
    private static TaskItem T(string id, double order, string? parent = null, TaskKind kind = TaskKind.Task, bool done = false) => new()
    {
        ItemId = id,
        ProjectId = "P1",
        IssueId = "I-" + id,
        RepositoryNameWithOwner = "org/repo",
        Number = 1,
        Title = id,
        ParentIssueId = parent is null ? null : "I-" + parent,
        SortOrder = order,
        Kind = kind,
        IsClosed = done,
    };

    private static readonly TaskItem[] Sample =
    [
        T("a", 1), T("a1", 1, "a"), T("a2", 2, "a"), T("a2x", 1, "a2"), T("a2xy", 1, "a2x"),
        T("b", 2, done: true), T("c", 3),
    ];

    [Fact]
    public void Numbers_follow_the_position_in_the_plan_including_done_tasks()
    {
        var numbers = OutlineNumbers.Of(TaskTree.Build(Sample), "TLB", 1);

        Assert.Equal("TLB_1", numbers["a"]);
        Assert.Equal("TLB_1_2", numbers["a2"]);
        Assert.Equal("TLB_1_2_1_1", numbers["a2xy"]);
        Assert.Equal("TLB_2", numbers["b"]);
        Assert.Equal("TLB_3", numbers["c"]);
    }

    [Fact]
    public void Prepared_levels_keep_parents_unchanged_when_deeper_tasks_appear()
    {
        var numbers = OutlineNumbers.Of(TaskTree.Build(Sample), "TLB", 3);

        Assert.Equal("TLB_1_0_0", numbers["a"]);
        Assert.Equal("TLB_1_1_0", numbers["a1"]);
        Assert.Equal("TLB_1_2_1", numbers["a2x"]);
        Assert.Equal("TLB_1_2_1_1", numbers["a2xy"]);
    }

    [Fact]
    public void Team_issues_are_outside_the_plan_but_personal_tasks_are_all_in_it()
    {
        var issue = T("i", 1, kind: TaskKind.Issue);
        var team = new Project { Id = "P1", Number = 1, Title = "T", Kind = ProjectKind.Team, OwnerLogin = "org" };

        Assert.False(OutlineNumbers.IsInPlan(issue, team));
        Assert.True(OutlineNumbers.IsInPlan(issue, team with { Kind = ProjectKind.Personal }));
    }
}
