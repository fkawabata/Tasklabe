using Tasklabe.Core.Domain;
using Tasklabe.Core.Kanban;
using Tasklabe.Core.MyTasks;

namespace Tasklabe.Core.Tests;

public class MyTaskViewTests
{
    // 2026-09-18 は金曜日
    private static readonly DateOnly Today = new(2026, 9, 18);

    private static TaskItem Task(string title, DateOnly? start = null, DateOnly? target = null,
        StatusCategory category = StatusCategory.Todo, string project = "p1", TaskKind kind = TaskKind.Task,
        DateTimeOffset updatedAt = default) => new()
    {
        ItemId = title,
        ProjectId = project,
        IssueId = title,
        RepositoryNameWithOwner = "me/repo",
        Number = 1,
        Title = title,
        Category = category,
        Start = start,
        Target = target,
        Kind = kind,
        UpdatedAt = updatedAt,
    };

    private static IReadOnlyList<string> Titles(IReadOnlyList<MyTaskGroup> groups, MyTaskGroupKind kind) =>
        groups.FirstOrDefault(g => g.Kind == kind)?.Tasks.Select(t => t.Title).ToList() ?? [];

    [Fact]
    public void Open_tasks_are_sorted_by_target_date()
    {
        var tasks = new[]
        {
            Task("no date"),
            Task("next week", target: Today.AddDays(5)),
            Task("overdue", target: Today.AddDays(-1)),
            Task("today", target: Today),
        };

        var groups = MyTaskView.Build(tasks);

        Assert.Equal(["overdue", "today", "next week", "no date"], Titles(groups, MyTaskGroupKind.Open));
    }

    [Fact]
    public void Open_and_done_tasks_follow_the_chosen_ordering()
    {
        var tasks = new[]
        {
            Task("b", target: Today),
            Task("c", target: Today.AddDays(1)),
            Task("a", target: Today.AddDays(2)),
            Task("y done", category: StatusCategory.Done, updatedAt: new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero)),
            Task("x done", category: StatusCategory.Done, updatedAt: new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero)),
        };

        var groups = MyTaskView.Build(tasks, TaskOrdering.Title);

        Assert.Equal(["a", "b", "c"], Titles(groups, MyTaskGroupKind.Open));
        Assert.Equal(["x done", "y done"], Titles(groups, MyTaskGroupKind.Done));
    }

    [Fact]
    public void Done_tasks_come_last_and_are_limited_to_the_recent_ones()
    {
        var tasks = Enumerable.Range(0, MyTaskView.RecentDoneCount + 3)
            .Select(i => Task($"done{i:00}", category: StatusCategory.Done,
                updatedAt: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero).AddDays(i)))
            .Append(Task("open"))
            .ToList();

        var groups = MyTaskView.Build(tasks);

        Assert.Equal([MyTaskGroupKind.Open, MyTaskGroupKind.Done], groups.Select(g => g.Kind));
        var done = Titles(groups, MyTaskGroupKind.Done);
        Assert.Equal(MyTaskView.RecentDoneCount, done.Count);
        Assert.Equal("done12", done[0]);
    }

    [Fact]
    public void Issues_are_shown_together_with_planned_tasks()
    {
        var tasks = new[]
        {
            Task("issue", target: Today, kind: TaskKind.Issue),
            Task("planned", target: Today.AddDays(1)),
        };

        var groups = MyTaskView.Build(tasks);

        Assert.Equal(["issue", "planned"], Titles(groups, MyTaskGroupKind.Open));
    }

    [Fact]
    public void Empty_groups_are_omitted()
    {
        Assert.Empty(MyTaskView.Build([]));
    }

    [Theory]
    [InlineData("2026-09-14", "2026-09-20")] // 月曜日
    [InlineData("2026-09-18", "2026-09-20")] // 金曜日
    [InlineData("2026-09-20", "2026-09-20")] // 日曜日
    public void End_of_week_is_sunday(string day, string expected)
    {
        Assert.Equal(DateOnly.Parse(expected), MyTaskView.EndOfWeek(DateOnly.Parse(day)));
    }
}
