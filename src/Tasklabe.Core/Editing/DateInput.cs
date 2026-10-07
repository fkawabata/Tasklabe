using System.Globalization;

namespace Tasklabe.Core.Editing;

/// <summary>
/// 表のセルで入力された日付の解釈（UI デザイン設計書 3.4.4 節）。
/// 年を省略した場合は今年とする。
/// </summary>
public static class DateInput
{
    private static readonly string[] Formats =
    [
        "yyyy-M-d", "yyyy/M/d", "yyyy.M.d", "M/d", "M-d", "M.d", "MMdd", "yyyyMMdd",
    ];

    /// <summary>入力を日付に変換する。空欄は null（日付の消去）として成功とする。</summary>
    public static bool TryParse(string? text, DateOnly today, out DateOnly? date)
    {
        date = null;
        var s = (text ?? "").Trim().Normalize(System.Text.NormalizationForm.FormKC); // 全角数字・記号を半角へ
        if (s.Length == 0)
        {
            return true;
        }

        if (s is "t" or "T" or "今日" or "きょう")
        {
            date = today;
            return true;
        }

        if ((s[0] is '+' or '-') && int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var offset))
        {
            date = today.AddDays(offset);
            return true;
        }

        if (DateOnly.TryParseExact(s, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            // 年を含まない書式は、今年の日付として解釈する
            bool hasYear = s.Length >= 8 || s.Count(c => c is '/' or '-' or '.') == 2;
            date = hasYear ? parsed : new DateOnly(today.Year, parsed.Month, parsed.Day);
            return true;
        }

        return false;
    }
}
