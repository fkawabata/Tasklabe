using System.Globalization;
using Tasklabe.Core.Domain;

namespace Tasklabe.Core.Kanban;

/// <summary>タスクの並び順（UI デザイン設計書 3.4.1 節、3.5 節）。リストとカンバンで共通に使う。</summary>
public enum TaskOrdering
{
    /// <summary>計画の並び（マイタスクは期日の順）。</summary>
    Default,

    /// <summary>期日の早い順。期日のないものは末尾。</summary>
    Due,

    /// <summary>更新の新しい順。</summary>
    Updated,

    /// <summary>作成の新しい順（Issue の番号の大きい順）。</summary>
    Created,

    /// <summary>想定工数の大きい順。未見積りは末尾。</summary>
    Estimate,

    /// <summary>タイトルの順。</summary>
    Title,
}

/// <summary>カードを横に区切る単位（スイムレーン）。</summary>
public enum KanbanGrouping
{
    None,

    /// <summary>プロジェクトごと（マイタスク）。</summary>
    Project,

    /// <summary>担当者ごと（チームのプロジェクト）。</summary>
    Assignee,

    /// <summary>親タスクごと。</summary>
    Parent,

    /// <summary>計画のタスクと課題（チームのプロジェクト）。</summary>
    Kind,
}

/// <summary>完了・中止のカードをどこまで出すか。</summary>
public enum KanbanCompleted
{
    All,
    PastWeek,
    PastMonth,
    None,
}

/// <summary>カンバンの表示の設定。ビューごとに覚える。</summary>
public sealed record KanbanDisplay(
    TaskOrdering Ordering = TaskOrdering.Default,
    KanbanGrouping Grouping = KanbanGrouping.None,
    KanbanCompleted Completed = KanbanCompleted.All,
    bool ShowEmptyColumns = true)
{
    public static KanbanDisplay Default { get; } = new();

    /// <summary>
    /// 設定ファイルに保存する形（例: "ordering=Due;grouping=Project;completed=PastWeek"）。
    /// 空の列を出すかはアプリの設定で決めるため、ビューごとには保存しない。
    /// </summary>
    public override string ToString() =>
        $"ordering={Ordering};grouping={Grouping};completed={Completed}";

    /// <summary>保存した形から読む。読めない項目は既定値とする。</summary>
    public static KanbanDisplay Parse(string? text)
    {
        var display = Default;
        foreach (var part in (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2)
            {
                continue;
            }

            display = pair[0] switch
            {
                "ordering" when Enum.TryParse<TaskOrdering>(pair[1], out var o) && Enum.IsDefined(o) => display with { Ordering = o },
                "grouping" when Enum.TryParse<KanbanGrouping>(pair[1], out var g) && Enum.IsDefined(g) => display with { Grouping = g },
                "completed" when Enum.TryParse<KanbanCompleted>(pair[1], out var c) && Enum.IsDefined(c) => display with { Completed = c },
                _ => display,
            };
        }

        return display;
    }
}

/// <summary>カンバンの列。プロジェクトのカンバンではステータスの選択肢、マイタスクでは同じ名前のステータスをまとめたもの。</summary>
/// <param name="Key">列を見分けるキー。ステータスのないカードを集める列は <see cref="KanbanLayout.UnsetKey"/>。</param>
public sealed record KanbanColumn(string Key, string Name, StatusCategory? Category);

/// <summary>区切り（スイムレーン）。</summary>
/// <param name="Rank">並べる順（小さいほど上）。同じなら名前の順。</param>
public readonly record struct KanbanLaneKey(string Key, string Title, double Rank)
{
    /// <summary>区切らないときの、ただ 1 つの区切り。</summary>
    public static KanbanLaneKey All { get; } = new("", "", 0);
}

/// <summary>1 つの区切りに並ぶカード。<see cref="Cells"/> は列と同じ順。</summary>
public sealed record KanbanLane(KanbanLaneKey Key, IReadOnlyList<IReadOnlyList<TaskItem>> Cells)
{
    public int Count => Cells.Sum(c => c.Count);
}

public sealed record KanbanBoard(IReadOnlyList<KanbanColumn> Columns, IReadOnlyList<KanbanLane> Lanes)
{
    public IReadOnlyList<TaskItem> CellsOf(int column) => [.. Lanes.SelectMany(l => l.Cells[column])];
}

/// <summary>カンバンの列・区切り・並び順を決める（要件 F-UI-KB-01〜04）。</summary>
public static class KanbanLayout
{
    /// <summary>ステータスのないカードを集める列のキー。</summary>
    public const string UnsetKey = "";

    /// <summary>プロジェクトのカンバンの列。ステータスの選択肢の順に並べる。</summary>
    public static IReadOnlyList<KanbanColumn> ColumnsOf(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return [.. project.StatusOptions.Select(o => new KanbanColumn(o.Id, o.Name, o.Category))];
    }

    /// <summary>
    /// プロジェクトをまたぐカンバンの列。ステータスのカテゴリごとに 1 列とし、カテゴリの順に並べる。
    /// プロジェクトごとのステータス名に関わらず、同じカテゴリのカードを同じ列に置く。
    /// </summary>
    /// <param name="nameOf">カテゴリの呼び名。</param>
    public static IReadOnlyList<KanbanColumn> CategoryColumns(Func<StatusCategory, string> nameOf)
    {
        ArgumentNullException.ThrowIfNull(nameOf);
        return [.. StatusCategories.All.Select(c => new KanbanColumn(CategoryKey(c), nameOf(c), c))];
    }

    /// <summary>カテゴリの列のキー。</summary>
    public static string CategoryKey(StatusCategory category) => "category:" + category;

    /// <summary>ステータス名を、まとめた列のキーにする。</summary>
    public static string NameKey(string name) => name.Trim().ToUpperInvariant();

    /// <summary>
    /// カードを列と区切りへ振り分ける。完了・中止のカードは設定に応じて絞り、区切りの中は並び順に従って並べる。
    /// どの列にも当たらないカードがあれば、先頭に「未設定」の列を足す。
    /// </summary>
    /// <param name="columnKeyOf">カードの列のキー。null は未設定。</param>
    /// <param name="laneOf">カードの区切り。null なら区切らない。</param>
    public static KanbanBoard Build(IEnumerable<TaskItem> tasks, IReadOnlyList<KanbanColumn> columns,
        Func<TaskItem, string?> columnKeyOf, KanbanDisplay display, DateOnly today, Func<TaskItem, KanbanLaneKey>? laneOf = null)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(columnKeyOf);
        ArgumentNullException.ThrowIfNull(display);

        var visible = Order(tasks.Where(t => ShowsCompleted(t, display.Completed, today)), display.Ordering).ToList();

        var keys = columns.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        List<KanbanColumn> all = [.. columns];
        if (visible.Any(t => columnKeyOf(t) is not { } k || !keys.Contains(k)))
        {
            all.Insert(0, new KanbanColumn(UnsetKey, "未設定", null));
        }

        int ColumnIndex(TaskItem t) =>
            columnKeyOf(t) is { } k && keys.Contains(k) ? all.FindIndex(c => c.Key == k) : 0;

        var lanes = visible
            .GroupBy(t => laneOf?.Invoke(t) ?? KanbanLaneKey.All)
            .OrderBy(g => g.Key.Rank)
            .ThenBy(g => g.Key.Title, StringComparer.CurrentCulture)
            .Select(g =>
            {
                var cells = all.Select(_ => new List<TaskItem>()).ToList();
                foreach (var task in g)
                {
                    cells[ColumnIndex(task)].Add(task);
                }

                return (Key: g.Key, Cells: cells);
            })
            .ToList();

        // 区切らないときは、カードがなくても列を出す（落とす先として使うため）
        if (lanes.Count == 0 && laneOf is null)
        {
            lanes.Add((KanbanLaneKey.All, all.Select(_ => new List<TaskItem>()).ToList()));
        }

        var keep = Enumerable.Range(0, all.Count)
            .Where(i => display.ShowEmptyColumns && all[i].Key != UnsetKey || lanes.Any(l => l.Cells[i].Count > 0))
            .ToList();

        return new KanbanBoard(
            [.. keep.Select(i => all[i])],
            [.. lanes.Select(l => new KanbanLane(l.Key, [.. keep.Select(i => (IReadOnlyList<TaskItem>)l.Cells[i])]))]);
    }

    /// <summary>完了・中止のカードを出すか。終えた日（実績終了日、なければ最後の更新日）で判断する。</summary>
    public static bool ShowsCompleted(TaskItem task, KanbanCompleted completed, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (!task.IsDone || completed == KanbanCompleted.All)
        {
            return true;
        }

        var finished = task.ActualEnd ?? DateOnly.FromDateTime(task.UpdatedAt.LocalDateTime);
        return completed switch
        {
            KanbanCompleted.PastWeek => finished > today.AddDays(-7),
            KanbanCompleted.PastMonth => finished > today.AddMonths(-1),
            _ => false,
        };
    }

    /// <summary>並び順に従って並べる。同じ順位のものは元の並びを保つ。</summary>
    public static IEnumerable<TaskItem> Order(IEnumerable<TaskItem> tasks, TaskOrdering ordering)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        var culture = CultureInfo.CurrentCulture;
        return ordering switch
        {
            TaskOrdering.Due => tasks.OrderBy(t => t.Target ?? DateOnly.MaxValue).ThenBy(t => t.Start ?? DateOnly.MaxValue),
            TaskOrdering.Updated => tasks.OrderByDescending(t => t.UpdatedAt),
            // 送信待ちのタスクは番号がまだないが、いちばん新しい
            TaskOrdering.Created => tasks.OrderByDescending(t => t.IsLocal ? int.MaxValue : t.Number),
            TaskOrdering.Estimate => tasks.OrderBy(t => t.EstimateHours is null).ThenByDescending(t => t.EstimateHours ?? 0),
            TaskOrdering.Title => tasks.OrderBy(t => t.Title, StringComparer.Create(culture, ignoreCase: true)),
            _ => tasks,
        };
    }

}
