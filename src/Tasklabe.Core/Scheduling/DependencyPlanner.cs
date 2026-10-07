using Tasklabe.Core.Domain;
using Tasklabe.Core.Wbs;

namespace Tasklabe.Core.Scheduling;

/// <summary>自動でつなぐ依存関係の 1 本。</summary>
public sealed record ProposedLink(TaskItem Predecessor, TaskItem Successor);

/// <summary>
/// 依存関係をまとめて張る案を作る（要件 F-DEP-08）。
/// </summary>
public static class DependencyPlanner
{
    /// <summary>
    /// 親タスクの配下（直下の子）を、予定の順につなぐ案。予定期間が重なる子どうしは並列とみなしてつながず、
    /// 次のまとまりの子は、前のまとまりのすべての子の後に続ける。予定のない子と、後続を待たせない子は先行にしない。
    /// 既に張ってあるもの、親子の関係にあるもの、循環するものは含めない。
    /// </summary>
    public static IReadOnlyList<ProposedLink> ChainChildren(TaskTree tree, TaskItem parent)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(parent);

        var node = tree.Find(parent.ItemId);
        if (node is null)
        {
            return [];
        }

        var groups = ParallelGroups(node.ChildNodes.Select(c => c.Task));
        var tasks = tree.All().Select(n => n.Task).ToDictionary(t => t.ItemId, StringComparer.Ordinal);
        var current = tree;
        var links = new List<ProposedLink>();

        for (int g = 1; g < groups.Count; g++)
        {
            foreach (var succ in groups[g])
            {
                foreach (var pred in groups[g - 1].Where(p => !p.NonBlocking))
                {
                    var successor = tasks[succ.ItemId];
                    if (DependencyScheduler.CanLink(current, pred, successor) != LinkCheck.Ok)
                    {
                        continue;
                    }

                    links.Add(new ProposedLink(pred, successor));

                    // 次の判定（循環の確認）に、張ったものとして反映する
                    tasks[successor.ItemId] = successor with { BlockedBy = [.. successor.BlockedBy, pred.IssueId] };
                    current = TaskTree.Build(tasks.Values);
                }
            }
        }

        return links;
    }

    /// <summary>
    /// 予定の始まりの順に並べ、期間が重なるものを 1 つのまとまり（並列）にする。予定のないものは含めない。
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<TaskItem>> ParallelGroups(IEnumerable<TaskItem> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        var dated = tasks
            .Select(t => (Task: t, Start: t.Start ?? t.Target, End: t.Target ?? t.Start))
            .Where(x => x.Start is not null)
            .OrderBy(x => x.Start)
            .ThenBy(x => x.Task.SortOrder)
            .ToList();

        var groups = new List<List<TaskItem>>();
        DateOnly groupEnd = DateOnly.MinValue;
        foreach (var (task, start, end) in dated)
        {
            if (groups.Count == 0 || start!.Value > groupEnd)
            {
                groups.Add([task]);
                groupEnd = end!.Value;
            }
            else
            {
                groups[^1].Add(task);
                if (end!.Value > groupEnd)
                {
                    groupEnd = end.Value;
                }
            }
        }

        return groups;
    }

    /// <summary>
    /// 親から受け継いでいる先行タスク（祖先に張った依存関係）。このタスク自身に張ったものは含めない（要件 F-DEP-10）。
    /// </summary>
    public static IReadOnlyList<(TaskItem Ancestor, TaskItem Predecessor)> InheritedPredecessors(TaskTree tree, TaskItem task)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(task);

        var byIssue = tree.All().GroupBy(n => n.Task.IssueId).ToDictionary(g => g.Key, g => g.First().Task, StringComparer.Ordinal);
        var result = new List<(TaskItem, TaskItem)>();
        foreach (var ancestor in tree.Find(task.ItemId)?.Ancestors() ?? [])
        {
            foreach (var id in ancestor.Task.BlockedBy)
            {
                if (byIssue.TryGetValue(id, out var pred) && !task.BlockedBy.Contains(id, StringComparer.Ordinal))
                {
                    result.Add((ancestor.Task, pred));
                }
            }
        }

        return result;
    }
}
