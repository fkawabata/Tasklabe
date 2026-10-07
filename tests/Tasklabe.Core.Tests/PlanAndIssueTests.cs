using Tasklabe.Core.Domain;
using Tasklabe.Core.Editing;
using Tasklabe.Core.MyTasks;

namespace Tasklabe.Core.Tests;

/// <summary>計画と課題の分離（要件 F-TSK-08〜12）。</summary>
public class PlanAndIssueTests
{
    private static DateOnly D(int m, int d) => new(2026, m, d);

    private static TaskItem T(string id, TaskKind kind = TaskKind.Issue, string? parent = null, string assignee = "me") => new()
    {
        ItemId = id,
        ProjectId = "P1",
        IssueId = "I-" + id,
        RepositoryNameWithOwner = "org/repo",
        Number = 1,
        Title = id,
        Kind = kind,
        ParentIssueId = parent,
        Assignees = [assignee],
    };

    [Fact]
    public void Unset_kind_depends_on_the_project_kind()
    {
        Assert.Equal(TaskKind.Issue, ProjectConventions.ParseKind(null, ProjectKind.Team));
        Assert.Equal(TaskKind.Task, ProjectConventions.ParseKind(null, ProjectKind.Personal));
        Assert.Equal(TaskKind.Task, ProjectConventions.ParseKind("Task", ProjectKind.Team));
        Assert.Equal(TaskKind.Issue, ProjectConventions.ParseKind("Issue", ProjectKind.Personal));
    }

    [Fact]
    public void Promoting_sets_the_kind_place_and_schedule_in_one_edit()
    {
        var task = T("a");
        var changes = TaskRules.PromoteToPlan(task, "I-parent", 2048, D(9, 14), D(9, 18), 8);

        Assert.Equal(
            [TaskField.Kind, TaskField.Parent, TaskField.SortOrder, TaskField.Start, TaskField.Target, TaskField.Estimate],
            changes.Select(c => c.Field));

        var planned = changes.Aggregate(task, (t, c) => TaskValues.Apply(t, c, []));
        Assert.Equal(TaskKind.Task, planned.Kind);
        Assert.True(planned.IsPlanned);
        Assert.Equal("I-parent", planned.ParentIssueId);
        Assert.Equal(D(9, 14), planned.Start);
        Assert.Equal(8, planned.EstimateHours);
    }

    [Fact]
    public void Promoting_without_a_place_only_changes_what_is_needed()
    {
        var task = T("a") with { Start = D(9, 14), Target = D(9, 18), EstimateHours = 8, SortOrder = 1024 };
        var changes = TaskRules.PromoteToPlan(task, null, 1024, D(9, 14), D(9, 18), 8);

        Assert.Equal([TaskField.Kind], changes.Select(c => c.Field));
    }

    [Fact]
    public void Demoting_clears_the_parent_but_keeps_the_schedule()
    {
        var task = T("a", TaskKind.Task, parent: "I-parent") with { Start = D(9, 14), EstimateHours = 8 };
        var changes = TaskRules.DemoteToIssue(task);
        var issue = changes.Aggregate(task, (t, c) => TaskValues.Apply(t, c, []));

        Assert.Equal(TaskKind.Issue, issue.Kind);
        Assert.False(issue.IsPlanned);
        Assert.Null(issue.ParentIssueId);
        Assert.Equal(D(9, 14), issue.Start);
        Assert.Equal(8, issue.EstimateHours);
    }

    [Fact]
    public void My_tasks_include_issues_and_planned_tasks()
    {
        var tasks = new[]
        {
            T("課題", TaskKind.Issue) with { Target = D(9, 21) },
            T("計画のタスク", TaskKind.Task) with { Target = D(9, 21) },
        };

        var groups = MyTaskView.Build(tasks);

        // 並び順は環境の照合順序に左右されるため、順序によらず比べる
        Assert.Equal(
            ["計画のタスク", "課題"],
            groups.Single(g => g.Kind == MyTaskGroupKind.Open).Tasks.Select(t => t.Title).Order(StringComparer.Ordinal));
    }
}
