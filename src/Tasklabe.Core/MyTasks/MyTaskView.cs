using Tasklabe.Core.Domain;
using Tasklabe.Core.Kanban;

namespace Tasklabe.Core.MyTasks;

public enum MyTaskGroupKind
{
    Open,
    Done,
}

public sealed record MyTaskGroup(MyTaskGroupKind Kind, IReadOnlyList<TaskItem> Tasks);

/// <summary>
/// マイタスクの並べ替え（要件 F-UI-MY-02、05、UI デザイン設計書 3.4 節）。
/// 期間での絞り込みは行わず、未完了を並び順（既定は期日の順）に並べ、完了は直近のものだけを末尾に置く。
/// </summary>
public static class MyTaskView
{
    /// <summary>末尾に出す完了タスクの件数。古いものは埋もれるだけなので出さない。</summary>
    public const int RecentDoneCount = 10;

    /// <param name="tasks">個人プロジェクトのタスクと、チームプロジェクトで自分が担当するタスク。</param>
    /// <param name="ordering">並び順。既定は、未完了を期日の順、完了を更新の新しい順とする。</param>
    public static IReadOnlyList<MyTaskGroup> Build(IEnumerable<TaskItem> tasks, TaskOrdering ordering = TaskOrdering.Default)
    {
        // 計画のタスクも課題も、自分のものであれば同じように扱う
        var all = tasks.ToList();

        MyTaskGroup[] groups =
        [
            new(MyTaskGroupKind.Open, [.. KanbanLayout.Order(Sort(all.Where(t => !t.IsDone)), ordering)]),
            new(MyTaskGroupKind.Done, [.. KanbanLayout.Order(all.Where(t => t.IsDone).OrderByDescending(t => t.UpdatedAt).Take(RecentDoneCount), ordering)]),
        ];

        return groups.Where(g => g.Tasks.Count > 0).ToList();
    }

    /// <summary>
    /// カンバンに並べるタスク。未完了は期日の順、続けて完了・中止を新しい順に、すべて並べる
    /// （どこまで出すかはカンバンの表示の設定で決める）。
    /// </summary>
    public static IReadOnlyList<TaskItem> ForBoard(IEnumerable<TaskItem> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        var all = tasks.ToList();
        return [.. Sort(all.Where(t => !t.IsDone)), .. all.Where(t => t.IsDone).OrderByDescending(t => t.UpdatedAt)];
    }


    /// <summary>週の最終日（日曜日）。週は月曜日に始まる。</summary>
    public static DateOnly EndOfWeek(DateOnly day) => day.AddDays((7 - (int)day.DayOfWeek) % 7);

    /// <summary>期日の早い順に並べる。期日のないタスクは末尾に置く。</summary>
    private static List<TaskItem> Sort(IEnumerable<TaskItem> tasks) =>
        tasks.OrderBy(t => t.Target ?? DateOnly.MaxValue)
            .ThenBy(t => t.Start ?? DateOnly.MaxValue)
            .ThenBy(t => t.Title, StringComparer.CurrentCulture)
            .ToList();
}
