using Tasklabe.Core.Editing;

namespace Tasklabe.Core.Wbs;

/// <summary>
/// WBS の構造の操作（要件 F-UI-WBS-03）。並び順は兄弟の間でだけ比べるため、
/// 変更後の並び順は前後の兄弟の中間の値とする。
/// </summary>
public static class WbsOperations
{
    /// <summary>並び順の間隔。取得時は Project 内の位置 × この値とする。</summary>
    public const double Spacing = 1024;

    /// <summary>
    /// インデント: 直前の兄弟の子（末尾）にする。直前の兄弟がなければ何もしない。
    /// </summary>
    public static IReadOnlyList<TaskChange> Indent(TaskTree tree, TaskNode node)
    {
        var siblings = tree.SiblingsOf(node);
        int index = IndexOf(siblings, node);
        if (index <= 0)
        {
            return [];
        }

        var newParent = siblings[index - 1];
        double order = newParent.ChildNodes.Count == 0 ? Spacing : newParent.ChildNodes[^1].Task.SortOrder + Spacing;
        return Changes(node, newParent.Task.IssueId, order);
    }

    /// <summary>
    /// アウトデント: 親の兄弟にし、親の直後に置く。最上位なら何もしない。
    /// </summary>
    public static IReadOnlyList<TaskChange> Outdent(TaskTree tree, TaskNode node)
    {
        if (node.Parent is not { } parent)
        {
            return [];
        }

        var parentSiblings = tree.SiblingsOf(parent);
        int index = IndexOf(parentSiblings, parent);
        double after = parent.Task.SortOrder;
        double order = index + 1 < parentSiblings.Count ? Between(after, parentSiblings[index + 1].Task.SortOrder) : after + Spacing;
        return Changes(node, parent.Parent?.Task.IssueId, order);
    }

    /// <summary>兄弟の中で 1 つ上（-1）または下（+1）へ移す。</summary>
    public static IReadOnlyList<TaskChange> Move(TaskTree tree, TaskNode node, int direction)
    {
        var siblings = tree.SiblingsOf(node);
        int index = IndexOf(siblings, node);
        int target = index + Math.Sign(direction);
        if (index < 0 || target < 0 || target >= siblings.Count)
        {
            return [];
        }

        // 上へ: 1 つ上の兄弟の前へ。下へ: 1 つ下の兄弟の後ろへ。
        double order = direction < 0
            ? Between(target > 0 ? siblings[target - 1].Task.SortOrder : siblings[target].Task.SortOrder - 2 * Spacing, siblings[target].Task.SortOrder)
            : Between(siblings[target].Task.SortOrder, target + 1 < siblings.Count ? siblings[target + 1].Task.SortOrder : siblings[target].Task.SortOrder + 2 * Spacing);
        return TaskRules.Set(node.Task, TaskField.SortOrder, TaskValues.Number(order));
    }

    /// <summary>指定したタスクの子の末尾に置く位置。</summary>
    public static (string? ParentIssueId, double SortOrder) PositionInside(TaskNode parent)
    {
        ArgumentNullException.ThrowIfNull(parent);

        double order = parent.ChildNodes.Count == 0 ? Spacing : parent.ChildNodes[^1].Task.SortOrder + Spacing;
        return (parent.Task.IssueId, order);
    }

    /// <summary>新しいタスクを node の直後（同じ親）に置くときの親と並び順。node が null なら最上位の末尾。</summary>
    public static (string? ParentIssueId, double SortOrder) PositionAfter(TaskTree tree, TaskNode? node)
    {
        if (node is null)
        {
            return (null, tree.Roots.Count == 0 ? Spacing : tree.Roots[^1].Task.SortOrder + Spacing);
        }

        var siblings = tree.SiblingsOf(node);
        int index = IndexOf(siblings, node);
        double order = index + 1 < siblings.Count ? Between(node.Task.SortOrder, siblings[index + 1].Task.SortOrder) : node.Task.SortOrder + Spacing;
        return (node.Parent?.Task.IssueId, order);
    }

    /// <summary>ドラッグして落とす位置。</summary>
    public enum DropPosition
    {
        /// <summary>対象の前（同じ親）。</summary>
        Before,

        /// <summary>対象の後ろ（同じ親）。</summary>
        After,

        /// <summary>対象の子（末尾）。</summary>
        Inside,
    }

    /// <summary>
    /// node を target の位置へ移す。自分自身や自分の子孫の上へは落とせない。
    /// </summary>
    public static IReadOnlyList<TaskChange> DropOn(TaskTree tree, TaskNode node, TaskNode target, DropPosition position)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(target);

        if (ReferenceEquals(node, target) || target.IsDescendantOf(node))
        {
            return [];
        }

        switch (position)
        {
            case DropPosition.Inside:
            {
                var (parent, order) = PositionInside(target);
                return Changes(node, parent, order);
            }

            case DropPosition.After:
            {
                var (parent, order) = PositionAfter(tree, target);
                return Changes(node, parent, order);
            }

            default:
            {
                var siblings = tree.SiblingsOf(target);
                int index = IndexOf(siblings, target);
                double before = index > 0 ? siblings[index - 1].Task.SortOrder : target.Task.SortOrder - 2 * Spacing;
                return Changes(node, target.Parent?.Task.IssueId, Between(before, target.Task.SortOrder));
            }
        }
    }

    private static IReadOnlyList<TaskChange> Changes(TaskNode node, string? parentIssueId, double order)
    {
        var changes = new List<TaskChange>();
        changes.AddRange(TaskRules.Set(node.Task, TaskField.Parent, parentIssueId));
        changes.AddRange(TaskRules.Set(node.Task, TaskField.SortOrder, TaskValues.Number(order)));
        return changes;
    }

    private static double Between(double a, double b) => (a + b) / 2;

    private static int IndexOf(IReadOnlyList<TaskNode> list, TaskNode node)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], node))
            {
                return i;
            }
        }

        return -1;
    }
}
