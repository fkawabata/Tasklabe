using Tasklabe.Core.Domain;
using Tasklabe.Core.Editing;

namespace Tasklabe.Core.Tests;

public class TaskRulesTests
{
    private static readonly DateOnly Today = new(2026, 9, 19);

    private static readonly StatusOption Todo = new("o1", "Todo", "GRAY", StatusCategory.Todo);
    private static readonly StatusOption Doing = new("o2", "In Progress", "BLUE", StatusCategory.InProgress);
    private static readonly StatusOption Review = new("o3", "Review", "PURPLE", StatusCategory.InProgress);
    private static readonly StatusOption Done = new("o4", "Done", "GREEN", StatusCategory.Done);
    private static readonly StatusOption Canceled = new("o5", "Canceled", "RED", StatusCategory.Canceled);
    private static readonly StatusOption[] Options = [Todo, Doing, Review, Done, Canceled];

    private static TaskItem Task(StatusOption? status = null, bool closed = false, DateOnly? actualStart = null, DateOnly? actualEnd = null) => new()
    {
        ItemId = "I1",
        ProjectId = "P1",
        IssueId = "ISSUE_1",
        RepositoryNameWithOwner = "me/repo",
        Number = 1,
        Title = "task",
        StatusOptionId = status?.Id,
        StatusName = status?.Name,
        Category = closed ? StatusCategory.Done : status?.Category ?? StatusCategory.Todo,
        IsClosed = closed,
        ActualStart = actualStart,
        ActualEnd = actualEnd,
        ProgressPercent = 40,
    };

    private static Dictionary<TaskField, string?> AsMap(IReadOnlyList<TaskChange> changes) =>
        changes.ToDictionary(c => c.Field, c => c.NewValue);

    [Fact]
    public void Pausing_counts_as_started_and_backlog_does_not()
    {
        var pending = new StatusOption("o6", "Pending", "ORANGE", StatusCategory.Pending);
        var backlog = new StatusOption("o7", "Backlog", "GRAY", StatusCategory.Backlog);

        Assert.Equal(TaskValues.Date(Today), AsMap(TaskRules.ChangeStatus(Task(Todo), pending, Today))[TaskField.ActualStart]);
        Assert.DoesNotContain(TaskField.ActualStart, AsMap(TaskRules.ChangeStatus(Task(Todo), backlog, Today)).Keys);
    }

    [Fact]
    public void Canceling_closes_as_not_planned_and_keeps_actuals()
    {
        var task = Task(Doing, actualStart: new DateOnly(2026, 9, 1));
        var changes = TaskRules.ChangeStatus(task, Canceled, Today);
        var map = AsMap(changes);

        Assert.Equal(TaskValues.ClosedNotPlanned, map[TaskField.State]);
        Assert.DoesNotContain(TaskField.ActualEnd, map.Keys);
        Assert.DoesNotContain(TaskField.Progress, map.Keys);

        var applied = changes.Aggregate(task, (t, c) => TaskValues.Apply(t, c, Options));
        Assert.True(applied.IsCanceled);
        Assert.True(applied.IsDone);
        Assert.False(applied.IsCompleted);
        Assert.Equal(0, applied.RemainingHours ?? 0);
    }

    [Fact]
    public void Reopening_a_canceled_task_opens_the_issue()
    {
        var canceled = Task(Canceled, closed: true) with { Category = StatusCategory.Canceled };
        var map = AsMap(TaskRules.ChangeStatus(canceled, Todo, Today));

        Assert.Equal(TaskValues.Open, map[TaskField.State]);
    }

    [Fact]
    public void Closed_issue_counts_as_done_unless_canceled()
    {
        Assert.Equal(StatusCategory.Done, TaskValues.CategoryOf(true, Todo));
        Assert.Equal(StatusCategory.Canceled, TaskValues.CategoryOf(true, Canceled));
        Assert.Equal(StatusCategory.InProgress, TaskValues.CategoryOf(false, Doing));
    }

    [Fact]
    public void Starting_work_records_actual_start()
    {
        var changes = AsMap(TaskRules.ChangeStatus(Task(Todo), Doing, Today));

        Assert.Equal("o2", changes[TaskField.Status]);
        Assert.Equal("2026-09-19", changes[TaskField.ActualStart]);
        Assert.DoesNotContain(TaskField.State, changes.Keys);
    }

    [Fact]
    public void Actual_start_is_not_overwritten()
    {
        var changes = AsMap(TaskRules.ChangeStatus(Task(Doing, actualStart: new DateOnly(2026, 9, 1)), Review, Today));

        Assert.Equal(["o3"], changes.Values);
    }

    [Fact]
    public void Completing_records_actual_end_progress_and_closes()
    {
        var changes = AsMap(TaskRules.ChangeStatus(Task(Doing, actualStart: new DateOnly(2026, 9, 1)), Done, Today));

        Assert.Equal("o4", changes[TaskField.Status]);
        Assert.Equal("2026-09-19", changes[TaskField.ActualEnd]);
        Assert.Equal("100", changes[TaskField.Progress]);
        Assert.Equal(TaskValues.Closed, changes[TaskField.State]);
        Assert.DoesNotContain(TaskField.ActualStart, changes.Keys);
    }

    [Fact]
    public void Completing_without_start_fills_both_dates()
    {
        var changes = AsMap(TaskRules.ChangeStatus(Task(Todo), Done, Today));

        Assert.Equal("2026-09-19", changes[TaskField.ActualStart]);
        Assert.Equal("2026-09-19", changes[TaskField.ActualEnd]);
    }

    [Fact]
    public void Reopening_clears_actual_end_and_reopens_issue()
    {
        var task = Task(Done, closed: true, actualStart: new DateOnly(2026, 9, 1), actualEnd: new DateOnly(2026, 9, 10));

        var changes = AsMap(TaskRules.ChangeStatus(task, Doing, Today));

        Assert.Null(changes[TaskField.ActualEnd]);
        Assert.Equal(TaskValues.Open, changes[TaskField.State]);
    }

    [Fact]
    public void Toggle_done_uses_first_option_of_the_category()
    {
        var done = TaskRules.ToggleDone(Task(Doing), Options, Today);
        var undone = TaskRules.ToggleDone(Task(Done, closed: true), Options, Today);

        Assert.Equal("o4", AsMap(done)[TaskField.Status]);
        Assert.Equal("o1", AsMap(undone)[TaskField.Status]);
    }

    [Fact]
    public void Setting_the_same_value_produces_no_change()
    {
        Assert.Empty(TaskRules.Set(Task(Todo), TaskField.Progress, "40"));
        Assert.Empty(TaskRules.Set(Task(Todo), TaskField.Title, "task"));
    }

    [Fact]
    public void Progress_is_rounded_and_clamped()
    {
        Assert.Equal("100", TaskRules.Set(Task(Todo), TaskField.Progress, "130").Single().NewValue);
        Assert.Equal("33", TaskRules.Set(Task(Todo), TaskField.Progress, "33.4").Single().NewValue);
    }

    [Fact]
    public void Applying_changes_updates_category_from_status_and_state()
    {
        var task = Task(Todo);
        foreach (var change in TaskRules.ChangeStatus(task, Done, Today))
        {
            task = TaskValues.Apply(task, change, Options);
        }

        Assert.Equal(StatusCategory.Done, task.Category);
        Assert.True(task.IsClosed);
        Assert.Equal("Done", task.StatusName);
        Assert.Equal(100, task.ProgressPercent);
        Assert.Equal(Today, task.ActualEnd);
    }
}
