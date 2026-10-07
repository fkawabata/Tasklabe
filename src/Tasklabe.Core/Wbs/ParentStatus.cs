using Tasklabe.Core.Domain;
using Tasklabe.Core.Editing;

namespace Tasklabe.Core.Wbs;

/// <summary>
/// 子タスクを持つタスク（親タスク）のステータスを、子から決める（要件 F-PRG-03 と同じく、親の値は子から求める）。
/// 子の増減・移動・ステータスの変更のどれで子の顔ぶれが変わっても、その時点の子から求め直すため、決まりは 1 つで済む。
/// </summary>
public static class ParentStatus
{
    /// <summary>
    /// 子から求めた親のステータスのカテゴリ。子を持たなければ null。
    /// 子を持たない子孫（作業）を見て、中止を除いたものがすべて完了なら Done、どれかが着手・完了・保留なら In Progress、
    /// すべて Backlog なら Backlog、それ以外は Todo とする。作業がすべて中止なら Canceled とする。
    /// </summary>
    public static StatusCategory? Derive(TaskNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!node.HasChildren)
        {
            return null;
        }

        var leaves = node.Leaves().Select(n => n.Task.Category).ToList();
        var active = leaves.Where(c => c != StatusCategory.Canceled).ToList();
        return active switch
        {
            [] => StatusCategory.Canceled,
            _ when active.All(c => c == StatusCategory.Done) => StatusCategory.Done,
            _ when active.Any(c => c is StatusCategory.InProgress or StatusCategory.Done or StatusCategory.Pending) => StatusCategory.InProgress,
            _ when active.All(c => c == StatusCategory.Backlog) => StatusCategory.Backlog,
            _ => StatusCategory.Todo,
        };
    }

    /// <summary>
    /// ステータスが子と食い違っている親タスクを、子から求めたカテゴリの代表のステータスへ直す変更。
    /// プロジェクトにそのカテゴリがなければ近いカテゴリの代表にし、それでもいまと同じカテゴリなら直さない。
    /// </summary>
    public static IReadOnlyList<(TaskItem Task, IReadOnlyList<TaskChange> Changes)> Reconcile(
        TaskTree plan, IReadOnlyList<StatusOption> options, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);
        var result = new List<(TaskItem, IReadOnlyList<TaskChange>)>();
        foreach (var node in plan.All())
        {
            if (Derive(node) is not { } derived || derived == node.Task.Category
                || ProjectConventions.DefaultStatus(options, derived) is not { } option || option.Category == node.Task.Category)
            {
                continue;
            }

            var changes = TaskRules.ChangeStatus(node.Task, option, today);
            if (changes.Count > 0)
            {
                result.Add((node.Task, changes));
            }
        }

        return result;
    }
}
