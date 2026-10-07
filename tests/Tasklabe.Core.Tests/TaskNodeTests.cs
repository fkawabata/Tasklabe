using Tasklabe.Core.Domain;
using Tasklabe.Core.Wbs;

namespace Tasklabe.Core.Tests;

/// <summary>WBS の木のたどり方。</summary>
public class TaskNodeTests
{
    private static TaskItem T(string id, double order, string? parent = null) => new()
    {
        ItemId = id,
        ProjectId = "P1",
        IssueId = "I-" + id,
        RepositoryNameWithOwner = "org/repo",
        Number = 1,
        Title = id,
        ParentIssueId = parent is null ? null : "I-" + parent,
        SortOrder = order,
    };

    // A ─┬─ A1 ─┬─ A1a
    //    │      └─ A1b
    //    └─ A2
    // B
    private static readonly TaskTree Tree = TaskTree.Build(
        [T("A", 1), T("A1", 1, "A"), T("A1a", 1, "A1"), T("A1b", 2, "A1"), T("A2", 2, "A"), T("B", 2)]);

    private static TaskNode Node(string id) => Tree.Find(id)!;

    private static IReadOnlyList<string> Ids(IEnumerable<TaskNode> nodes) => nodes.Select(n => n.Task.ItemId).ToList();

    [Fact]
    public void SelfAndDescendants_lists_parents_before_children_in_order()
    {
        Assert.Equal(["A", "A1", "A1a", "A1b", "A2"], Ids(Node("A").SelfAndDescendants()));
        Assert.Equal(["B"], Ids(Node("B").SelfAndDescendants()));
    }

    [Fact]
    public void Leaves_are_the_nodes_without_children()
    {
        Assert.Equal(["A1a", "A1b", "A2"], Ids(Node("A").Leaves()));
        Assert.Equal(["A2"], Ids(Node("A2").Leaves()));
    }

    [Fact]
    public void Ancestors_go_from_the_parent_to_the_root()
    {
        Assert.Equal(["A1", "A"], Ids(Node("A1b").Ancestors()));
        Assert.Empty(Node("A").Ancestors());
    }

    [Theory]
    [InlineData("A1a", "A", true)]
    [InlineData("A1a", "A1", true)]
    [InlineData("A", "A1a", false)]
    [InlineData("A", "A", false)]
    [InlineData("A2", "A1", false)]
    [InlineData("B", "A", false)]
    public void IsDescendantOf_checks_the_ancestors_only(string node, string ancestor, bool expected)
    {
        Assert.Equal(expected, Node(node).IsDescendantOf(Node(ancestor)));
    }
}
