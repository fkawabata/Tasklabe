using System.Globalization;

namespace Tasklabe.App.Services;

/// <summary>画面に出す日付の表記。どの画面でも同じ形にする。書式は表示の言語の文言から引く（UX 規約 UX-30、UX-35）。</summary>
public static class DateText
{
    /// <summary>表示の言語の書式（曜日の名前など）。</summary>
    public static CultureInfo Culture => CultureInfo.CurrentCulture;

    // 書式は言語を決めたあとは変わらないため、初めて使うときに一度だけ引く
    private static readonly string ShortFormat = Texts.Get("Date_Short");
    private static readonly string ShortWithYearFormat = Texts.Get("Date_ShortWithYear");

    private static DateOnly Today => AppClock.Today;

    /// <summary>「9/25(金)」（英語は「Fri 9/25」）の形。今年でなければ「2027/1/5(火)」と年を添える。</summary>
    public static string Short(DateOnly date, DateOnly today) =>
        date.ToString(date.Year == today.Year ? ShortFormat : ShortWithYearFormat, Culture);

    /// <inheritdoc cref="Short(DateOnly, DateOnly)"/>
    public static string Short(DateOnly date) => Short(date, Today);

    /// <summary>
    /// 範囲を「10/5(月) 〜 10/9(金)」の形にする。1 日だけなら日付 1 つ、片側だけなら「10/5(月) 〜」「〜 10/9(金)」。
    /// どちらもなければ null。
    /// </summary>
    public static string? Range(DateOnly? start, DateOnly? end) => (start, end) switch
    {
        (null, null) => null,
        ({ } s, null) => Texts.Format("Date_RangeFrom", Short(s)),
        (null, { } e) => Texts.Format("Date_RangeTo", Short(e)),
        ({ } s, { } e) when s == e => Short(s),
        ({ } s, { } e) => Texts.Format("Date_Range", Short(s), Short(e)),
    };

    /// <summary>「2026/9/25(金)」の形（休日の一覧など、年をまたいで並べるもの）。</summary>
    public static string Long(DateOnly date) => date.ToString(ShortWithYearFormat, Culture);
}
