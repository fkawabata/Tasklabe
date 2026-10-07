namespace Tasklabe.Core.Editing;

/// <summary>
/// 日付範囲のピッカーの入力欄で入力された範囲の解釈（UI デザイン設計書 3.4.3 節）。
/// 1 つの日付は 1 日だけの範囲とし、片側を空けた「〜10/9」「10/5〜」は、その側の日付を持たない範囲とする。
/// それぞれの日付は <see cref="DateInput"/> と同じ書き方で書ける。
/// </summary>
public static class DateRangeInput
{
    private static readonly string[] Separators = ["〜", "~", "–", "—", "から", ","];

    /// <summary>入力を範囲に変換する。空欄は両方 null（範囲の消去）として成功とする。開始が終了より後なら入れ替える。</summary>
    public static bool TryParse(string? text, DateOnly today, out DateOnly? start, out DateOnly? end)
    {
        start = end = null;
        var s = (text ?? "").Trim().Normalize(System.Text.NormalizationForm.FormKC);
        if (s.Length == 0)
        {
            return true;
        }

        if (DateInput.TryParse(s, today, out var single))
        {
            start = end = single;
            return true;
        }

        foreach (var separator in Separators)
        {
            int at = s.IndexOf(separator, StringComparison.Ordinal);
            if (at >= 0 && TrySplit(s[..at], s[(at + separator.Length)..], today, out start, out end))
            {
                return true;
            }
        }

        // 「10/5-10/9」のように - で区切ったもの。「2026-10-5」や「-3」とも読めるため、1 つの日付として読めないときだけ試す
        for (int at = s.IndexOf('-', 1); at > 0; at = s.IndexOf('-', at + 1))
        {
            if (TrySplit(s[..at], s[(at + 1)..], today, out start, out end))
            {
                return true;
            }
        }

        start = end = null;
        return false;
    }

    private static bool TrySplit(string left, string right, DateOnly today, out DateOnly? start, out DateOnly? end)
    {
        start = end = null;
        if (left.Trim().Length == 0 && right.Trim().Length == 0)
        {
            return false;
        }

        if (!DateInput.TryParse(left, today, out start) || !DateInput.TryParse(right, today, out end))
        {
            start = end = null;
            return false;
        }

        if (start > end)
        {
            (start, end) = (end, start);
        }

        return true;
    }
}
