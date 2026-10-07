using Tasklabe.Core.Domain;
using Tasklabe.Core.Editing;

namespace Tasklabe.Core.Tests;

/// <summary>元に戻す・やり直す（要件 F-UNDO-01）。</summary>
public class EditHistoryTests
{
    private static readonly StatusOption Todo = new("o1", "Todo", "GRAY", StatusCategory.Todo);
    private static readonly StatusOption Done = new("o4", "Done", "GREEN", StatusCategory.Done);
    private static readonly StatusOption[] Options = [Todo, Done];

    private static TaskItem Task() => new()
    {
        ItemId = "I1",
        ProjectId = "P1",
        IssueId = "ISSUE_1",
        RepositoryNameWithOwner = "me/repo",
        Number = 1,
        Title = "設計",
        StatusOptionId = Todo.Id,
        ProgressPercent = 40,
    };

    private static TaskItem ApplyAll(TaskItem task, IEnumerable<TaskChange> changes) =>
        changes.Aggregate(task, (t, c) => TaskValues.Apply(t, c, Options));

    [Fact]
    public void Inverse_restores_every_field_of_a_status_change()
    {
        var before = Task();
        var changes = TaskRules.ChangeStatus(before, Done, new DateOnly(2026, 9, 25));
        var after = ApplyAll(before, changes);

        var restored = ApplyAll(after, EditHistory.Inverse(after, changes));

        Assert.Equal(before.StatusOptionId, restored.StatusOptionId);
        Assert.False(restored.IsClosed);
        Assert.Equal(before.ProgressPercent, restored.ProgressPercent);
        Assert.Null(restored.ActualEnd);
    }

    [Fact]
    public void Inverse_sends_the_status_before_reopening_the_issue()
    {
        // Status が Done のまま Issue を開き直すと、GitHub の Project の自動化が閉じ直してしまう
        var before = Task();
        var changes = TaskRules.ChangeStatus(before, Done, new DateOnly(2026, 9, 25));
        var after = ApplyAll(before, changes);

        var fields = EditHistory.Inverse(after, changes).Select(c => c.Field).ToList();

        Assert.True(fields.IndexOf(TaskField.Status) < fields.IndexOf(TaskField.State));
    }

    [Fact]
    public void Inverse_leaves_fields_that_changed_afterwards()
    {
        var before = Task();
        var changes = TaskRules.Set(before, TaskField.Progress, TaskValues.Number(60));

        // その後に GitHub 上で 80 % に変わった
        var now = before with { ProgressPercent = 80 };

        Assert.Empty(EditHistory.Inverse(now, changes));
    }

    [Fact]
    public void Inverse_of_repeated_changes_goes_back_to_the_first_value()
    {
        var before = Task();
        var first = TaskRules.Set(before, TaskField.Progress, TaskValues.Number(60));
        var middle = ApplyAll(before, first);
        var second = TaskRules.Set(middle, TaskField.Progress, TaskValues.Number(70));
        var after = ApplyAll(middle, second);

        var inverse = EditHistory.Inverse(after, [.. first, .. second]);

        Assert.Equal(40, ApplyAll(after, inverse).ProgressPercent);
    }

    [Fact]
    public void Linking_with_schedule_shifts_is_described_as_one_operation()
    {
        // 依存関係を張るのに合わせて後続をずらした操作は、依存関係も戻ることが分かる説明にする
        var step = new EditStep(
        [
            new TaskEdit("I1", "基本設計書", [new TaskChange(TaskField.BlockedBy, "", "I9"), new TaskChange(TaskField.Start, "2026-10-28", "2026-10-30")]),
            new TaskEdit("I2", "レビュー", [new TaskChange(TaskField.Start, "2026-10-29", "2026-11-02")]),
        ]);

        Assert.Equal("「基本設計書」の先行タスクの変更と、2 件の予定の変更", step.Description);
    }

    [Fact]
    public void Recording_a_new_step_clears_redo()
    {
        var history = new EditHistory();
        var step = new EditStep([new TaskEdit("I1", "設計", [new TaskChange(TaskField.Title, "a", "b")])]);
        history.Record(step);
        history.PushRedo(history.PopUndo()!);
        Assert.True(history.CanRedo);

        history.Record(step);

        Assert.False(history.CanRedo);
        Assert.True(history.CanUndo);
    }

    [Fact]
    public void Oldest_steps_are_dropped_beyond_capacity()
    {
        var history = new EditHistory(capacity: 2);
        for (int i = 0; i < 3; i++)
        {
            history.Record(new EditStep([new TaskEdit("I1", "設計", [new TaskChange(TaskField.Title, $"{i}", $"{i + 1}")])]));
        }

        Assert.Equal("2", history.PopUndo()!.Edits[0].Changes[0].OldValue);
        Assert.Equal("1", history.PopUndo()!.Edits[0].Changes[0].OldValue);
        Assert.Null(history.PopUndo());
    }

    [Fact]
    public void Replacing_a_local_id_keeps_the_step_usable()
    {
        var history = new EditHistory();
        history.Record(new EditStep([new TaskEdit("local:1", "設計", [new TaskChange(TaskField.Title, "a", "b")])]));

        history.ReplaceItemId("local:1", "ITEM_1");

        Assert.Equal("ITEM_1", history.PopUndo()!.Edits[0].ItemId);
    }

    [Fact]
    public void Description_names_the_task_and_the_field()
    {
        var one = new EditStep([new TaskEdit("I1", "設計", [new TaskChange(TaskField.Status, "o1", "o4")])]);
        var many = new EditStep([one.Edits[0], new TaskEdit("I2", "実装", [new TaskChange(TaskField.Start, null, "2026-09-25")])]);

        Assert.Equal("「設計」のステータスの変更", one.Description);
        Assert.Equal("2 件のタスクの変更", many.Description);
    }

    [Fact]
    public void Description_prefers_the_status_over_its_side_effects()
    {
        var step = new EditStep([new TaskEdit("I1", "設計", [
            new TaskChange(TaskField.State, "CLOSED", "OPEN"),
            new TaskChange(TaskField.ActualEnd, "2026-09-25", null),
            new TaskChange(TaskField.Status, "o4", "o1"),
        ])]);

        Assert.Equal("「設計」のステータスの変更", step.Description);
    }
}
