using Tasklabe.Core.Domain;

namespace Tasklabe.App.Services;

/// <summary>
/// タスクの番号（例: TLB-123）の表記（要件 F-TSK-14）。プロジェクトのキーを、プロジェクトを読み直すたびに覚え直す。
/// </summary>
public static class TaskKeys
{
    private static IReadOnlyDictionary<string, string> s_keys = new Dictionary<string, string>();

    /// <summary>プロジェクトのキーを覚え直す。</summary>
    public static void Update(IEnumerable<Project> projects) => s_keys = ProjectKey.Resolve(projects);

    /// <summary>プロジェクトのキー。知らないプロジェクトなら null。</summary>
    public static string? KeyOf(string projectId) => s_keys.GetValueOrDefault(projectId);

    /// <summary>タスクの番号。GitHub にまだ送っていないタスクは番号を持たないため、空にする。</summary>
    public static string Of(TaskItem task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Number <= 0)
        {
            return "";
        }

        return KeyOf(task.ProjectId) is { } key ? ProjectKey.Format(key, task.Number) : $"#{task.Number}";
    }
}
