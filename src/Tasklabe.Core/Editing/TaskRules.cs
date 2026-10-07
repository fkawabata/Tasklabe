using Tasklabe.Core.Domain;

namespace Tasklabe.Core.Editing;

/// <summary>
/// 編集操作から、GitHub へ送る変更の一覧を作る（要件 F-TSK-07、F-STS-04、F-PRG-02）。
/// </summary>
public static class TaskRules
{
    /// <summary>1 項目を変更する。値が変わらない場合は空。</summary>
    public static IReadOnlyList<TaskChange> Set(TaskItem task, TaskField field, string? value)
    {
        if (field == TaskField.Status)
        {
            throw new ArgumentException("ステータスは ChangeStatus で変更する。", nameof(field));
        }

        if (field == TaskField.Progress && TaskValues.ParseNumber(value) is { } p)
        {
            value = TaskValues.Number(Math.Round(Math.Clamp(p, 0, 100)));
        }

        var old = TaskValues.Get(task, field);
        return old == value ? [] : [new TaskChange(field, old, value)];
    }

    /// <summary>
    /// 課題を計画へ移す（要件 F-TSK-09）。置き場所と日程、想定工数をまとめて与える。
    /// </summary>
    public static IReadOnlyList<TaskChange> PromoteToPlan(TaskItem task, string? parentIssueId, double sortOrder,
        DateOnly? start, DateOnly? target, double? estimate)
    {
        ArgumentNullException.ThrowIfNull(task);

        List<TaskChange> changes = [.. Set(task, TaskField.Kind, ProjectConventions.KindOptions.Task)];
        var planned = task with { Kind = TaskKind.Task };
        changes.AddRange(Set(planned, TaskField.Parent, parentIssueId));
        changes.AddRange(Set(planned, TaskField.SortOrder, TaskValues.Number(sortOrder)));
        changes.AddRange(Set(planned, TaskField.Start, TaskValues.Date(start)));
        changes.AddRange(Set(planned, TaskField.Target, TaskValues.Date(target)));
        changes.AddRange(Set(planned, TaskField.Estimate, TaskValues.Number(estimate)));
        return changes;
    }

    /// <summary>計画のタスクを課題へ戻す（要件 F-TSK-10）。親子関係は解除し、日程と工数は残す。</summary>
    public static IReadOnlyList<TaskChange> DemoteToIssue(TaskItem task)
    {
        ArgumentNullException.ThrowIfNull(task);

        List<TaskChange> changes = [.. Set(task, TaskField.Kind, ProjectConventions.KindOptions.Issue)];
        changes.AddRange(Set(task, TaskField.Parent, null));
        return changes;
    }

    /// <summary>
    /// ステータスを変更する。カテゴリの変化に応じて実績日、進捗率、Issue の開閉状態も変える。
    /// </summary>
    public static IReadOnlyList<TaskChange> ChangeStatus(TaskItem task, StatusOption option, DateOnly today)
    {
        var changes = new List<TaskChange>();
        void Add(TaskField field, string? value)
        {
            var old = TaskValues.Get(task, field);
            if (old != value)
            {
                changes.Add(new TaskChange(field, old, value));
            }
        }

        Add(TaskField.Status, option.Id);

        switch (option.Category)
        {
            case StatusCategory.InProgress or StatusCategory.Pending:
                if (task.ActualStart is null)
                {
                    Add(TaskField.ActualStart, TaskValues.Date(today));
                }

                ReopenIfClosed();
                break;

            case StatusCategory.Done:
                if (task.ActualStart is null)
                {
                    Add(TaskField.ActualStart, TaskValues.Date(today));
                }

                if (task.ActualEnd is null)
                {
                    Add(TaskField.ActualEnd, TaskValues.Date(today));
                }

                Add(TaskField.Progress, TaskValues.Number(100));
                Add(TaskField.State, TaskValues.Closed);
                break;

            case StatusCategory.Canceled:
                // 実績日と進捗率はそのまま残し、「対応しない」として Close する
                Add(TaskField.State, TaskValues.ClosedNotPlanned);
                break;

            default:
                ReopenIfClosed();
                break;
        }

        return changes;

        void ReopenIfClosed()
        {
            if (task.IsDone)
            {
                // 完了から戻す場合は実績終了日を取り消す
                Add(TaskField.ActualEnd, null);
                Add(TaskField.State, TaskValues.Open);
            }
        }
    }

    /// <summary>完了・未完了を切り替える（Space キー、状態アイコン）。</summary>
    public static IReadOnlyList<TaskChange> ToggleDone(TaskItem task, IReadOnlyList<StatusOption> options, DateOnly today)
    {
        var target = ProjectConventions.DefaultStatus(options, task.IsDone ? StatusCategory.Todo : StatusCategory.Done);

        return target is null ? [] : ChangeStatus(task, target, today);
    }
}
