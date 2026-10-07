using Tasklabe.Core.Domain;
using Tasklabe.Core.Progress;

namespace Tasklabe.Core.Wbs;

/// <summary>WBS の 1 ノード。子を持つノードの工数と進捗率は子から積み上げる（要件 F-PRG-03）。</summary>
public sealed class TaskNode : IProgressNode
{
    private readonly List<TaskNode> _children = [];

    internal TaskNode(TaskItem task, TaskNode? parent)
    {
        Task = task;
        Parent = parent;
        Depth = parent is null ? 0 : parent.Depth + 1;
    }

    public TaskItem Task { get; }

    public TaskNode? Parent { get; }

    public int Depth { get; }

    public IReadOnlyList<TaskNode> ChildNodes => _children;

    public bool HasChildren => _children.Count > 0;

    /// <summary>積み上げた工数と進捗率。</summary>
    public ProgressSummary Summary { get; private set; }

    double? IProgressNode.EstimateHours => Task.EstimateHours;

    double IProgressNode.ProgressPercent => Task.ProgressPercent;

    StatusCategory IProgressNode.Category => Task.IsCanceled ? StatusCategory.Canceled : Task.IsDone ? StatusCategory.Done : Task.Category;

    IReadOnlyList<IProgressNode> IProgressNode.Children => _children;

    /// <summary>自分と子孫。親を子より先に、並び順でたどる。</summary>
    public IEnumerable<TaskNode> SelfAndDescendants()
    {
        yield return this;
        foreach (var child in _children)
        {
            foreach (var node in child.SelfAndDescendants())
            {
                yield return node;
            }
        }
    }

    /// <summary>配下の作業（子を持たないノード）。自分が子を持たなければ自分。</summary>
    public IEnumerable<TaskNode> Leaves() => SelfAndDescendants().Where(n => !n.HasChildren);

    /// <summary>親から根元へ向かってたどる祖先。</summary>
    public IEnumerable<TaskNode> Ancestors()
    {
        for (var p = Parent; p is not null; p = p.Parent)
        {
            yield return p;
        }
    }

    /// <summary><paramref name="ancestor"/> の子孫か（自分自身は含まない）。</summary>
    public bool IsDescendantOf(TaskNode ancestor) => Ancestors().Any(p => ReferenceEquals(p, ancestor));

    internal void Add(TaskNode child) => _children.Add(child);

    internal void Sort()
    {
        _children.Sort(TaskTree.Compare);
        foreach (var child in _children)
        {
            child.Sort();
        }
    }

    internal void Summarize()
    {
        foreach (var child in _children)
        {
            child.Summarize();
        }

        Summary = ProgressCalculator.Summarize(this);
    }
}

/// <summary>1 つのプロジェクトのタスクを、Sub-issue の親子関係で木構造にしたもの。</summary>
public sealed class TaskTree
{
    private TaskTree(IReadOnlyList<TaskNode> roots, ProgressSummary summary)
    {
        Roots = roots;
        Summary = summary;
    }

    public IReadOnlyList<TaskNode> Roots { get; }

    /// <summary>プロジェクト全体の工数と進捗率（要件 F-PRG-04）。</summary>
    public ProgressSummary Summary { get; }

    /// <summary>
    /// 木構造を組み立てる。親が同じプロジェクトにないタスクは最上位に置く。
    /// 親子関係が循環している場合は、循環を断ち切って最上位に置く。
    /// </summary>
    public static TaskTree Build(IEnumerable<TaskItem> tasks)
    {
        var list = tasks.ToList();
        var byIssue = new Dictionary<string, TaskItem>(StringComparer.Ordinal);
        foreach (var t in list)
        {
            byIssue.TryAdd(t.IssueId, t);
        }

        var childrenOf = list
            .Where(t => t.ParentIssueId is { } p && byIssue.ContainsKey(p) && !CreatesCycle(t, byIssue))
            .GroupBy(t => t.ParentIssueId!)
            .ToDictionary(g => g.Key, g => g.ToList());
        var placed = childrenOf.Values.SelectMany(c => c).Select(c => c.ItemId).ToHashSet();

        var roots = new List<TaskNode>();
        foreach (var t in list.Where(t => !placed.Contains(t.ItemId)))
        {
            var node = new TaskNode(t, null);
            AddChildren(node, childrenOf);
            roots.Add(node);
        }

        roots.Sort(Compare);
        foreach (var r in roots)
        {
            r.Sort();
            r.Summarize();
        }

        return new TaskTree(roots, ProgressCalculator.SummarizeAll(roots));
    }

    /// <summary>表示順（深さ優先）で、折りたたまれていないノードを列挙する。</summary>
    public IEnumerable<TaskNode> Flatten(Func<TaskNode, bool> isExpanded)
    {
        var stack = new Stack<TaskNode>(Roots.Reverse());
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            if (node.HasChildren && isExpanded(node))
            {
                for (int i = node.ChildNodes.Count - 1; i >= 0; i--)
                {
                    stack.Push(node.ChildNodes[i]);
                }
            }
        }
    }

    public IEnumerable<TaskNode> All() => Flatten(_ => true);

    public TaskNode? Find(string itemId) => All().FirstOrDefault(n => n.Task.ItemId == itemId);

    /// <summary>ノードの兄弟（自分を含む、並び順）。</summary>
    public IReadOnlyList<TaskNode> SiblingsOf(TaskNode node) => node.Parent?.ChildNodes ?? Roots;

    internal static int Compare(TaskNode a, TaskNode b)
    {
        int c = a.Task.SortOrder.CompareTo(b.Task.SortOrder);
        return c != 0 ? c : string.CompareOrdinal(a.Task.ItemId, b.Task.ItemId);
    }

    private static void AddChildren(TaskNode node, Dictionary<string, List<TaskItem>> childrenOf)
    {
        if (!childrenOf.TryGetValue(node.Task.IssueId, out var children))
        {
            return;
        }

        foreach (var c in children)
        {
            var child = new TaskNode(c, node);
            node.Add(child);
            AddChildren(child, childrenOf);
        }
    }

    private static bool CreatesCycle(TaskItem task, Dictionary<string, TaskItem> byIssue)
    {
        var seen = new HashSet<string> { task.IssueId };
        var current = task.ParentIssueId;
        while (current is not null && byIssue.TryGetValue(current, out var parent))
        {
            if (!seen.Add(current))
            {
                return true;
            }

            current = parent.ParentIssueId;
        }

        return false;
    }
}
