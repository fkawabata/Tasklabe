using Tasklabe.Core.Calendar;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Scheduling;
using Tasklabe.Core.Wbs;

namespace Tasklabe.Core.Tests;

public class DependencyPlannerTests
{
    private static DateOnly D(int m, int d) => new(2026, m, d);

    private static TaskItem T(string id, string? parent = null, DateOnly? start = null, DateOnly? target = null,
        bool nonBlocking = false, double sort = 0, params string[] blockedBy) => new()
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
        Kind = TaskKind.Task,
        NonBlocking = nonBlocking,
        SortOrder = sort,
        BlockedBy = blockedBy.Select(b => "I-" + b).ToList(),
    };

    private static List<string> Links(IEnumerable<ProposedLink> links) =>
        [.. links.Select(l => $"{l.Predecessor.ItemId}>{l.Successor.ItemId}")];

    [Fact]
    public void Children_are_chained_in_plan_order_and_parallel_ones_are_not_linked()
    {
        // a と b は期間が重なる（並列）。c は両方の後、d は c の後
        TaskItem[] tasks =
        [
            T("p"),
            T("a", "p", D(9, 14), D(9, 16)),
            T("b", "p", D(9, 15), D(9, 17)),
            T("c", "p", D(9, 18), D(9, 18)),
            T("d", "p", D(9, 24), D(9, 25)),
            T("x", "p"),   // 予定のない子はつながない
        ];

        var links = DependencyPlanner.ChainChildren(TaskTree.Build(tasks), tasks[0]);

        Assert.Equal(["a>c", "b>c", "c>d"], Links(links));
    }

    [Fact]
    public void Existing_links_and_non_blocking_children_are_skipped()
    {
        TaskItem[] tasks =
        [
            T("p"),
            T("a", "p", D(9, 14), D(9, 15)),
            T("r", "p", D(9, 14), D(9, 15), nonBlocking: true),
            T("b", "p", D(9, 16), D(9, 17), blockedBy: ["a"]),
        ];

        var links = DependencyPlanner.ChainChildren(TaskTree.Build(tasks), tasks[0]);

        Assert.Empty(links);
    }

    [Fact]
    public void Links_inherited_from_ancestors_are_listed()
    {
        TaskItem[] tasks =
        [
            T("design"),
            T("build", blockedBy: ["design"]),
            T("login", "build"),
        ];

        var inherited = DependencyPlanner.InheritedPredecessors(TaskTree.Build(tasks), tasks[2]);

        Assert.Equal([("build", "design")], inherited.Select(i => (i.Ancestor.ItemId, i.Predecessor.ItemId)));
    }

    [Fact]
    public void Non_blocking_child_does_not_hold_back_the_successor_of_its_parent()
    {
        // フェーズ p の最後に振り返り r（9/18）がある。r を待たせない印にすると、後続 n は a の後（9/16）から始められる
        TaskItem[] tasks =
        [
            T("p"),
            T("a", "p", D(9, 14), D(9, 15)),
            T("r", "p", D(9, 18), D(9, 18), nonBlocking: true),
            T("n", start: D(9, 16), target: D(9, 16), blockedBy: ["p"]),
        ];

        Assert.Empty(DependencyScheduler.Resolve(TaskTree.Build(tasks), D(9, 1)));

        tasks[2] = tasks[2] with { NonBlocking = false };
        var shift = Assert.Single(DependencyScheduler.Resolve(TaskTree.Build(tasks), D(9, 1)));
        Assert.Equal("n", shift.Task.ItemId);
    }
}

/// <summary>暦を切り替えるテストは、ほかのテストと並行させない。</summary>
[CollectionDefinition(DisableParallelization = true)]
public class WorkCalendarCollection;

[Collection(typeof(WorkCalendarCollection))]
public class ProjectCalendarSchedulingTests
{
    [Fact]
    public void Shifts_follow_the_project_calendar()
    {
        var previous = WorkCalendar.Current;
        try
        {
            // 水曜を休み、土曜を稼働日にした暦。a が 9/15 (火) に終わると、b (2 稼働日) は 9/17 (木)〜9/18 (金)
            WorkCalendar.Current = new WorkCalendarRules(
                new HashSet<DayOfWeek> { DayOfWeek.Sunday, DayOfWeek.Wednesday }, true, new HashSet<DateOnly>());
            TaskItem[] tasks =
            [
                new() { ItemId = "a", ProjectId = "P", IssueId = "I-a", RepositoryNameWithOwner = "o/r", Number = 1, Title = "a",
                    Kind = TaskKind.Task, Start = new(2026, 9, 14), Target = new(2026, 9, 15) },
                new() { ItemId = "b", ProjectId = "P", IssueId = "I-b", RepositoryNameWithOwner = "o/r", Number = 2, Title = "b",
                    Kind = TaskKind.Task, Start = new(2026, 9, 14), Target = new(2026, 9, 15), BlockedBy = ["I-a"] },
            ];

            var shift = Assert.Single(DependencyScheduler.Resolve(TaskTree.Build(tasks), new DateOnly(2026, 9, 1)));

            Assert.Equal(new PlanDates(new DateOnly(2026, 9, 17), new DateOnly(2026, 9, 18)), shift.After);
        }
        finally
        {
            WorkCalendar.Current = previous;
        }
    }
}
