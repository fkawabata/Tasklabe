namespace Tasklabe.Core.Wbs;

/// <summary>上端に残す行。</summary>
/// <param name="Index">行の位置。</param>
/// <param name="Top">表示範囲の上端からの位置。配下の終わりに押し上げられると、置き場所より上になる。</param>
public readonly record struct StickyRow(int Index, double Top);

/// <summary>
/// 縦にスクロールしたとき、上端に残す親の行（UI デザイン設計書 3.3.1 節・3.4.4 節）。行はすべて同じ高さで、階層の順に並んでいるものとする。
/// 上端に重ねた行の下から見え始める行の親を、外側から順に重ねる。重ねた行は、配下の最後の行が上へ抜けるときに一緒に押し上げる。
/// </summary>
public static class StickyRows
{
    /// <param name="depths">行ごとの階層の深さ（最上位は 0）。</param>
    /// <param name="scrollTop">表示範囲の上端の位置（内容の座標）。</param>
    /// <param name="maxCount">重ねる行の上限。</param>
    public static IReadOnlyList<StickyRow> Compute(IReadOnlyList<int> depths, double scrollTop, double rowHeight, int maxCount)
    {
        ArgumentNullException.ThrowIfNull(depths);
        var indexes = new List<int>();
        int? previous = null;
        while (indexes.Count < maxCount)
        {
            // 重ねた行の下から見え始める行
            double slotTop = scrollTop + indexes.Count * rowHeight;
            int under = Math.Max((int)Math.Floor(slotTop / rowHeight), 0);
            if (under >= depths.Count || NextInChain(depths, under, previous) is not { } candidate)
            {
                break;
            }

            // 見え始める行そのものは、配下が続く親で、上端から欠けているときだけ重ねる
            if (candidate == under && (!HasChildRows(depths, under) || under * rowHeight >= slotTop))
            {
                break;
            }

            indexes.Add(candidate);
            previous = candidate;
        }

        var result = new List<StickyRow>(indexes.Count);
        for (int k = 0; k < indexes.Count; k++)
        {
            double bottom = (SubtreeEnd(depths, indexes[k]) + 1) * rowHeight - scrollTop;
            result.Add(new StickyRow(indexes[k], Math.Min(k * rowHeight, bottom - rowHeight)));
        }

        return result;
    }

    /// <summary>重ねた行が覆う高さ（表示範囲の上端から）。</summary>
    public static double Covered(IReadOnlyList<StickyRow> rows, double rowHeight)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.Count == 0 ? 0 : Math.Max(rows.Max(r => r.Top) + rowHeight, 0);
    }

    /// <summary>
    /// 行が重ねた行に隠れずに見えるスクロール位置のうち、いまより上へ動かすときに最も近いもの（行の位置より上だけを探す）。
    /// </summary>
    public static double RevealTop(IReadOnlyList<int> depths, int index, double rowHeight, int maxCount)
    {
        ArgumentNullException.ThrowIfNull(depths);
        double top = index * rowHeight;
        for (int k = 0; k <= maxCount; k++)
        {
            double scroll = Math.Max(top - k * rowHeight, 0);
            if (scroll + Covered(Compute(depths, scroll, rowHeight, maxCount), rowHeight) <= top)
            {
                return scroll;
            }
        }

        return Math.Max(top - maxCount * rowHeight, 0);
    }

    /// <summary>重ねている行のうち、その行が見えている位置（表示範囲の上端から）。重ねていなければ null。</summary>
    public static double? TopOf(IReadOnlyList<StickyRow> rows, int index)
    {
        ArgumentNullException.ThrowIfNull(rows);
        foreach (var row in rows)
        {
            if (row.Index == index)
            {
                return row.Top;
            }
        }

        return null;
    }

    /// <summary>表示範囲の上端からの位置にある、重ねた行。外側の行を手前に描くため、外側から調べる。</summary>
    public static int? At(IReadOnlyList<StickyRow> rows, double y, double rowHeight)
    {
        ArgumentNullException.ThrowIfNull(rows);
        foreach (var row in rows)
        {
            if (y >= Math.Max(row.Top, 0) && y < row.Top + rowHeight)
            {
                return row.Index;
            }
        }

        return null;
    }

    /// <summary>
    /// 行とその親を外側から並べたうち、<paramref name="previous"/> のすぐ内側のもの（null なら最も外側）。
    /// 行が <paramref name="previous"/> の配下になければ null。
    /// </summary>
    private static int? NextInChain(IReadOnlyList<int> depths, int index, int? previous)
    {
        var chain = new List<int> { index };
        int depth = depths[index];
        for (int i = index - 1; i >= 0 && depth > 0; i--)
        {
            if (depths[i] < depth)
            {
                chain.Add(i);
                depth = depths[i];
            }
        }

        chain.Reverse();
        if (previous is not { } p)
        {
            return chain[0];
        }

        int at = chain.IndexOf(p);
        return at >= 0 && at + 1 < chain.Count ? chain[at + 1] : null;
    }

    private static bool HasChildRows(IReadOnlyList<int> depths, int index) =>
        index + 1 < depths.Count && depths[index + 1] > depths[index];

    /// <summary>配下の最後の行（配下がなければその行）。</summary>
    private static int SubtreeEnd(IReadOnlyList<int> depths, int index)
    {
        int end = index;
        while (end + 1 < depths.Count && depths[end + 1] > depths[index])
        {
            end++;
        }

        return end;
    }
}
