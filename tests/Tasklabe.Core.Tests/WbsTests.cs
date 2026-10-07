using Tasklabe.Core.Domain;
using Tasklabe.Core.Editing;
using Tasklabe.Core.Wbs;

namespace Tasklabe.Core.Tests;

public class WbsTests
{
    private static TaskItem T(string id, double order, string? parent = null, double? estimate = null, double progress = 0) => new()
    {
        ItemId = id,
        ProjectId = "P1",
        IssueId = "I-" + id,
        RepositoryNameWithOwner = "org/repo",
        Number = 1,
        Title = id,
        ParentIssueId = parent is null ? null : "I-" + parent,
        SortOrder = order,
        EstimateHours = estimate,
        ProgressPercent = progress,
    };

    private static IReadOnlyList<string> Order(TaskTree tree) => tree.All().Select(n => new string(' ', n.Depth) + n.Task.ItemId).ToList();

    private static TaskTree Apply(IEnumerable<TaskItem> tasks, string id, Func<TaskTree, TaskNode, IReadOnlyList<TaskChange>> op)
    {
        var list = tasks.ToList();
        var tree = TaskTree.Build(list);
        var changes = op(tree, tree.Find(id)!);
        var updated = list.Select(t => t.ItemId == id ? changes.Aggregate(t, (acc, c) => TaskValues.Apply(acc, c, [])) : t);
        return TaskTree.Build(updated);
    }

    private static readonly TaskItem[] Sample =
    [
        T("a", 1024), T("a1", 1024, "a"), T("a2", 2048, "a"),
        T("b", 2048), T("c", 3072),
    ];

    [Fact]
    public void Tree_is_built_in_sort_order_with_depths()
    {
        var tree = TaskTree.Build(Sample.Reverse());

        Assert.Equal(["a", " a1", " a2", "b", "c"], Order(tree));
    }

    [Fact]
    public void Tasks_with_parent_outside_project_are_roots()
    {
        var tree = TaskTree.Build([T("x", 1, "missing")]);

        Assert.Single(tree.Roots);
    }

    [Fact]
    public void Cycles_are_broken()
    {
        var tree = TaskTree.Build([T("x", 1, "y"), T("y", 2, "x")]);

        Assert.Equal(2, tree.Roots.Count);
    }

    [Fact]
    public void Parent_progress_rolls_up_and_project_summary_uses_roots()
    {
        var tree = TaskTree.Build([T("p", 1), T("c1", 1, "p", 10, 100), T("c2", 2, "p", 30, 0), T("q", 2, null, 10, 50)]);

        var parent = tree.Roots[0];
        Assert.Equal(40, parent.Summary.EstimateHours);
        Assert.Equal(25, parent.Summary.ProgressPercent, precision: 6);
        Assert.Equal(50, tree.Summary.EstimateHours);
        Assert.Equal(30, tree.Summary.ProgressPercent, precision: 6); // (10 + 5) / 50
    }

    [Fact]
    public void Indent_makes_node_last_child_of_previous_sibling()
    {
        var tree = Apply(Sample, "b", WbsOperations.Indent);

        Assert.Equal(["a", " a1", " a2", " b", "c"], Order(tree));
    }

    [Fact]
    public void Indent_of_first_sibling_does_nothing()
    {
        var tree = TaskTree.Build(Sample);

        Assert.Empty(WbsOperations.Indent(tree, tree.Find("a")!));
        Assert.Empty(WbsOperations.Indent(tree, tree.Find("a1")!));
    }

    [Fact]
    public void Outdent_places_node_right_after_its_parent()
    {
        var tree = Apply(Sample, "a1", WbsOperations.Outdent);

        Assert.Equal(["a", " a2", "a1", "b", "c"], Order(tree));
    }

    [Fact]
    public void Move_up_and_down_swaps_with_sibling()
    {
        Assert.Equal(["a", " a1", " a2", "c", "b"], Order(Apply(Sample, "b", (t, n) => WbsOperations.Move(t, n, +1))));
        Assert.Equal(["b", "a", " a1", " a2", "c"], Order(Apply(Sample, "b", (t, n) => WbsOperations.Move(t, n, -1))));
        Assert.Equal(["a", " a2", " a1", "b", "c"], Order(Apply(Sample, "a2", (t, n) => WbsOperations.Move(t, n, -1))));
    }

    [Fact]
    public void Move_at_edge_does_nothing()
    {
        var tree = TaskTree.Build(Sample);

        Assert.Empty(WbsOperations.Move(tree, tree.Find("a")!, -1));
        Assert.Empty(WbsOperations.Move(tree, tree.Find("c")!, +1));
    }

    [Fact]
    public void New_task_position_is_between_node_and_next_sibling()
    {
        var tree = TaskTree.Build(Sample);

        Assert.Equal(("I-a", 1536d), WbsOperations.PositionAfter(tree, tree.Find("a1")));
        Assert.Equal(((string?)null, 4096d), WbsOperations.PositionAfter(tree, tree.Find("c")));
        Assert.Equal(((string?)null, 4096d), WbsOperations.PositionAfter(tree, null));
    }

    [Fact]
    public void Dropping_between_rows_reorders_within_the_same_parent()
    {
        var tasks = new[] { T("a", 1024), T("b", 2048), T("c", 3072) };
        var tree = TaskTree.Build(tasks);

        var changes = WbsOperations.DropOn(tree, tree.Find("c")!, tree.Find("a")!, WbsOperations.DropPosition.Before);
        var moved = changes.Aggregate(tasks[2], (t, c) => TaskValues.Apply(t, c, []));

        Assert.Null(moved.ParentIssueId);
        Assert.True(moved.SortOrder < 1024);
    }

    [Fact]
    public void Dropping_on_the_middle_of_a_row_makes_it_a_child()
    {
        var tasks = new[] { T("a", 1024), T("b", 2048) };
        var tree = TaskTree.Build(tasks);

        var changes = WbsOperations.DropOn(tree, tree.Find("b")!, tree.Find("a")!, WbsOperations.DropPosition.Inside);
        var moved = changes.Aggregate(tasks[1], (t, c) => TaskValues.Apply(t, c, []));

        Assert.Equal("I-a", moved.ParentIssueId);
    }

    [Fact]
    public void A_task_cannot_be_dropped_into_its_own_descendant()
    {
        var tasks = new[] { T("parent", 1024), T("child", 1024, "parent") };
        var tree = TaskTree.Build(tasks);

        Assert.Empty(WbsOperations.DropOn(tree, tree.Find("parent")!, tree.Find("child")!, WbsOperations.DropPosition.Inside));
        Assert.Empty(WbsOperations.DropOn(tree, tree.Find("parent")!, tree.Find("parent")!, WbsOperations.DropPosition.After));
    }

    [Fact]
    public void New_tasks_can_be_placed_inside_a_task()
    {
        var tree = TaskTree.Build([T("parent", 1024), T("child", 1024, "parent")]);

        var (parentIssueId, order) = WbsOperations.PositionInside(tree.Find("parent")!);

        Assert.Equal("I-parent", parentIssueId);
        Assert.Equal(1024 + WbsOperations.Spacing, order);
    }
}
