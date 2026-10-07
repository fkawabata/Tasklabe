using System.Globalization;

namespace Tasklabe.App.Services;

/// <summary>画面に出す日付の表記。どの画面でも同じ形にする。</summary>
public static class DateText
{
    public static CultureInfo Japanese { get; } = CultureInfo.GetCultureInfo("ja-JP");

    private static DateOnly Today => AppClock.Today;

    /// <summary>「9/25(金)」の形。今年でなければ「2027/1/5(火)」と年を添える。</summary>
    public static string Short(DateOnly date, DateOnly today) =>
        date.ToString(date.Year == today.Year ? "M/d(ddd)" : "yyyy/M/d(ddd)", Japanese);

    /// <inheritdoc cref="Short(DateOnly, DateOnly)"/>
    public static string Short(DateOnly date) => Short(date, Today);

    /// <summary>
    /// 範囲を「10/5(月) 〜 10/9(金)」の形にする。1 日だけなら日付 1 つ、片側だけなら「10/5(月) 〜」「〜 10/9(金)」。
    /// どちらもなければ null。
    /// </summary>
    public static string? Range(DateOnly? start, DateOnly? end) => (start, end) switch
    {
        (null, null) => null,
        ({ } s, null) => $"{Short(s)} 〜",
        (null, { } e) => $"〜 {Short(e)}",
        ({ } s, { } e) when s == e => Short(s),
        ({ } s, { } e) => $"{Short(s)} 〜 {Short(e)}",
    };

    /// <summary>「2026/9/25(金)」の形（休日の一覧など、年をまたいで並べるもの）。</summary>
    public static string Long(DateOnly date) => date.ToString("yyyy/M/d(ddd)", Japanese);
}
