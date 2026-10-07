namespace Tasklabe.Core.Settings;

/// <summary>
/// ナビゲーションのプロジェクトの並び（要件 F-UI-NAV-02）。利用者が並べ替えた順を、プロジェクトの ID の並びとして持つ。
/// </summary>
public static class ProjectOrder
{
    /// <summary>
    /// 保存した並びに従って並べる。並びにないもの（あとから加わったプロジェクト）は、元の順のまま末尾に置く。
    /// </summary>
    public static IReadOnlyList<T> Apply<T>(IEnumerable<T> items, Func<T, string> idOf, IReadOnlyList<string> order)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(idOf);
        ArgumentNullException.ThrowIfNull(order);

        var position = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < order.Count; i++)
        {
            position.TryAdd(order[i], i);
        }

        // OrderBy は安定なので、並びにないもの同士は元の順を保つ
        return [.. items.OrderBy(item => position.TryGetValue(idOf(item), out var p) ? p : int.MaxValue)];
    }

    /// <summary>
    /// 1 つのプロジェクトを、別のプロジェクトの前か後ろへ移した並び。
    /// </summary>
    /// <param name="ids">いまの並び。</param>
    /// <param name="moved">移すプロジェクト。</param>
    /// <param name="target">基準にするプロジェクト。</param>
    /// <param name="after">基準の後ろへ置くか。</param>
    public static IReadOnlyList<string> Move(IReadOnlyList<string> ids, string moved, string target, bool after)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (moved == target || !ids.Contains(moved) || !ids.Contains(target))
        {
            return ids;
        }

        var list = ids.Where(id => id != moved).ToList();
        int index = list.IndexOf(target) + (after ? 1 : 0);
        list.Insert(index, moved);
        return list;
    }
}
