using Tasklabe.Core.Domain;
using Tasklabe.Core.Editing;

namespace Tasklabe.Core.Tests;

public class AssigneeValueTests
{
    private static TaskItem Task(params string[] assignees) => new()
    {
        ItemId = "I1",
        ProjectId = "P1",
        IssueId = "ISSUE_1",
        RepositoryNameWithOwner = "org/repo",
        Number = 1,
        Title = "task",
        Assignees = assignees,
    };

    [Fact]
    public void Logins_are_sorted_deduplicated_and_comparable()
    {
        Assert.Equal("alice,Bob", TaskValues.Logins(["Bob", "alice", "bob"]));
        Assert.Null(TaskValues.Logins([]));
    }

    [Fact]
    public void Order_of_assignees_does_not_produce_a_change()
    {
        Assert.Empty(TaskRules.Set(Task("bob", "alice"), TaskField.Assignees, TaskValues.Logins(["alice", "bob"])));
    }

    [Fact]
    public void Assignees_are_applied()
    {
        var change = TaskRules.Set(Task(), TaskField.Assignees, TaskValues.Logins(["me"])).Single();
        var updated = TaskValues.Apply(Task(), change, []);

        Assert.Equal(["me"], updated.Assignees);
        Assert.Null(change.OldValue);
    }

    [Fact]
    public void Clearing_assignees_yields_empty_list()
    {
        var change = TaskRules.Set(Task("me"), TaskField.Assignees, null).Single();

        Assert.Empty(TaskValues.Apply(Task("me"), change, []).Assignees);
    }
}
