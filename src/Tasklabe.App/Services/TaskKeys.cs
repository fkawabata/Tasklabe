using Tasklabe.Core.Domain;
using Tasklabe.Core.Wbs;

namespace Tasklabe.App.Services;

/// <summary>
/// タスクの番号（例: TLB-123、TLB_1_2）の表記（要件 F-TSK-14）。プロジェクトのキーと、階層番号で呼ぶプロジェクトの
/// 計画の番号を、プロジェクトとタスクを読み直すたびに覚え直す。
/// </summary>
public static class TaskKeys
{
    private static IReadOnlyDictionary<string, string> s_keys = new Dictionary<string, string>();
    private static IReadOnlyDictionary<string, string> s_outline = new Dictionary<string, string>();

    /// <summary>プロジェクトのキーと、階層番号を覚え直す。</summary>
    public static void Update(IEnumerable<Project> projects, IEnumerable<TaskItem> tasks)
    {
        var list = projects.ToList();
        var keys = ProjectKey.Resolve(list);
        var outline = new Dictionary<string, string>(StringComparer.Ordinal);
        var byProject = tasks.ToLookup(t => t.ProjectId);
        foreach (var p in list)
        {
            if (p.Settings.OutlineLevels is { } levels && keys.TryGetValue(p.Id, out var key))
            {
                var plan = TaskTree.Build(byProject[p.Id].Where(t => OutlineNumbers.IsInPlan(t, p)));
                foreach (var (itemId, number) in OutlineNumbers.Of(plan, key, levels))
                {
                    outline[itemId] = number;
                }
            }
        }

        s_keys = keys;
        s_outline = outline;
    }

    /// <summary>プロジェクトのキー。知らないプロジェクトなら null。</summary>
    public static string? KeyOf(string projectId) => s_keys.GetValueOrDefault(projectId);

    /// <summary>
    /// タスクの番号。階層番号で呼ぶプロジェクトの計画のタスクは、計画の位置の番号とする。
    /// 通し番号は Issue の番号のため、GitHub にまだ送っていないタスクは番号を持たず、空にする。
    /// </summary>
    public static string Of(TaskItem task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (s_outline.TryGetValue(task.ItemId, out var outline))
        {
            return outline;
        }

        if (task.Number <= 0)
        {
            return "";
        }

        return KeyOf(task.ProjectId) is { } key ? ProjectKey.Format(key, task.Number) : $"#{task.Number}";
    }

    /// <summary>タスクが番号を持つか。</summary>
    public static bool Has(TaskItem task) => Of(task).Length > 0;

    /// <summary>
    /// 階層番号で呼ぶタスクの、Issue の番号の通し番号（TLB-123）。コミットやコメントに書かれた番号からも探せるようにする。
    /// 階層番号で呼ばないタスクでは null。
    /// </summary>
    public static string? IssueKeyOf(TaskItem task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return s_outline.ContainsKey(task.ItemId) && task.Number > 0 && KeyOf(task.ProjectId) is { } key
            ? ProjectKey.Format(key, task.Number)
            : null;
    }
}
