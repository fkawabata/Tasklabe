using Tasklabe.Core.Domain;
using Tasklabe.Core.Editing;
using Tasklabe.Core.Wbs;

namespace Tasklabe.Core.Tests;

/// <summary>親タスクのステータスを子から決める（ParentStatus）。</summary>
public class ParentStatusTests
{
    private static readonly DateOnly Today = new(2026, 10, 5);

    private static readonly StatusOption[] Options =
    [
        new("s-backlog", "Backlog", "GRAY", StatusCategory.Backlog),
        new("s-todo", "Todo", "BLUE", StatusCategory.Todo),
        new("s-doing", "In Progress", "YELLOW", StatusCategory.InProgress),
        new("s-pending", "Pending", "ORANGE", StatusCategory.Pending),
        new("s-done", "Done", "GREEN", StatusCategory.Done),
        new("s-canceled", "Canceled", "RED", StatusCategory.Canceled),
    ];

    private static TaskItem T(string id, StatusCategory category, string? parent = null) => new()
    {
        ItemId = id,
        ProjectId = "P",
        IssueId = "I" + id,
        RepositoryNameWithOwner = "o/r",
        Number = 1,
        Title = id,
        ParentIssueId = parent is null ? null : "I" + parent,
        Category = category,
        StatusOptionId = Options.First(o => o.Category == category).Id,
    };

    private static StatusCategory? Derive(params TaskItem[] tasks) => ParentStatus.Derive(TaskTree.Build(tasks).Find("p")!);

    [Fact]
    public void Parent_is_done_when_every_child_is_done_except_canceled_ones()
    {
        Assert.Equal(StatusCategory.Done, Derive(
            T("p", StatusCategory.Todo), T("a", StatusCategory.Done, "p"), T("b", StatusCategory.Canceled, "p")));
    }

    [Fact]
    public void Parent_is_in_progress_when_some_child_has_started()
    {
        Assert.Equal(StatusCategory.InProgress, Derive(
            T("p", StatusCategory.Todo), T("a", StatusCategory.Done, "p"), T("b", StatusCategory.Todo, "p")));
        Assert.Equal(StatusCategory.InProgress, Derive(
            T("p", StatusCategory.Todo), T("a", StatusCategory.Pending, "p"), T("b", StatusCategory.Backlog, "p")));
    }

    [Fact]
    public void Parent_follows_unstarted_children()
    {
        Assert.Equal(StatusCategory.Todo, Derive(T("p", StatusCategory.Done), T("a", StatusCategory.Todo, "p"), T("b", StatusCategory.Backlog, "p")));
        Assert.Equal(StatusCategory.Backlog, Derive(T("p", StatusCategory.Todo), T("a", StatusCategory.Backlog, "p")));
        Assert.Equal(StatusCategory.Canceled, Derive(T("p", StatusCategory.Todo), T("a", StatusCategory.Canceled, "p")));
    }

    [Fact]
    public void Grandchildren_count_through_intermediate_parents()
    {
        // p の子 m は子を持つため、m 自身ではなく m の子（作業）を数える
        Assert.Equal(StatusCategory.Done, Derive(
            T("p", StatusCategory.Todo), T("m", StatusCategory.Todo, "p"), T("a", StatusCategory.Done, "m"), T("b", StatusCategory.Done, "p")));
    }

    [Fact]
    public void Reconcile_closes_a_parent_whose_children_are_all_done()
    {
        var tree = TaskTree.Build([T("p", StatusCategory.Todo), T("a", StatusCategory.Done, "p"), T("b", StatusCategory.Done, "p")]);

        var (task, changes) = Assert.Single(ParentStatus.Reconcile(tree, Options, Today));

        Assert.Equal("p", task.ItemId);
        Assert.Contains(changes, c => c.Field == TaskField.Status && c.NewValue == "s-done");
        Assert.Contains(changes, c => c.Field == TaskField.State && c.NewValue == TaskValues.Closed);
    }

    [Fact]
    public void Reconcile_reopens_a_done_parent_when_an_unfinished_child_is_added()
    {
        var tree = TaskTree.Build([T("p", StatusCategory.Done), T("a", StatusCategory.Done, "p"), T("new", StatusCategory.Todo, "p")]);

        var (_, changes) = Assert.Single(ParentStatus.Reconcile(tree, Options, Today));

        Assert.Contains(changes, c => c.Field == TaskField.Status && c.NewValue == "s-doing");
    }

    [Fact]
    public void Reconcile_leaves_parents_that_already_match()
    {
        var tree = TaskTree.Build([T("p", StatusCategory.InProgress), T("a", StatusCategory.Done, "p"), T("b", StatusCategory.Todo, "p")]);

        Assert.Empty(ParentStatus.Reconcile(tree, Options, Today));
    }
}
