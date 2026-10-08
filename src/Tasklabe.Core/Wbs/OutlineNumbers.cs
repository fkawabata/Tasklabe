using Tasklabe.Core.Domain;

namespace Tasklabe.Core.Wbs;

/// <summary>
/// 計画のタスクの階層番号（要件 F-TSK-14。例: TLB_1_2_0）。番号は保存せず、計画の木の位置から表示のたびに求める。
/// 完了・中止したタスクも、表示の絞り込みによらず数える（絞り込みで番号が変わらないようにする）。
/// </summary>
public static class OutlineNumbers
{
    /// <summary>計画のタスクの番号（アイテムの ID → 番号）。</summary>
    /// <param name="plan">計画の木（チームのプロジェクトは課題を除いたもの、個人のプロジェクトはすべて）。</param>
    /// <param name="levels">あらかじめ用意する段の数。</param>
    public static IReadOnlyDictionary<string, string> Of(TaskTree plan, string key, int levels)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var numbers = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = new List<int>();
        Visit(plan.Roots);
        return numbers;

        void Visit(IReadOnlyList<TaskNode> siblings)
        {
            for (int i = 0; i < siblings.Count; i++)
            {
                path.Add(i + 1);
                numbers[siblings[i].Task.ItemId] = ProjectKey.FormatOutline(key, path, levels);
                Visit(siblings[i].ChildNodes);
                path.RemoveAt(path.Count - 1);
            }
        }
    }

    /// <summary>プロジェクトの計画の木に入るタスクか（チームは課題を除き、個人は区分によらずすべて。要件 F-UI-PR-01）。</summary>
    public static bool IsInPlan(TaskItem task, Project project)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(project);
        return task.IsPlanned || !project.IsTeam;
    }
}
