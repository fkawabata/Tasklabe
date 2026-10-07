using System.Globalization;
using System.Text;
using Tasklabe.Core.Calendar;

namespace Tasklabe.Core.Export;

/// <summary>
/// 共有ビューの形（<see cref="GanttOutline"/>）を Mermaid の gantt 記法に書き出す（要件 F-UI-GT-10）。
/// 日付は明示し、Mermaid の終了日は含まないため 1 日足す。
/// </summary>
public static class MermaidGantt
{
    /// <param name="calendar">休日の網掛け（excludes）に使う稼働日の決め方。休む曜日は曜日名で書く。</param>
    public static string ToCode(GanttOutline outline, WorkCalendarRules calendar)
    {
        ArgumentNullException.ThrowIfNull(outline);
        ArgumentNullException.ThrowIfNull(calendar);

        // 改行は環境によらず LF にする（Markdown やクリップボードへそのまま渡すため）
        var code = new StringBuilder();
        void Line(string text) => code.Append(text).Append('\n');

        Line("gantt");
        if (Clean(outline.Title) is { Length: > 0 } title)
        {
            Line($"    title {title}");
        }

        Line("    dateFormat YYYY-MM-DD");
        Line(outline.Span is { } span && span.End.DayNumber - span.Start.DayNumber > 180 ? "    axisFormat %Y/%m" : "    axisFormat %m/%d");
        if (outline.Holidays.Count > 0)
        {
            Line($"    excludes {Excludes(calendar, outline.Holidays)}");
        }

        if (!outline.TodayMarker)
        {
            Line("    todayMarker off");
        }

        int number = 0;
        foreach (var section in outline.Sections)
        {
            if (outline.ShowSections)
            {
                Line($"    section {(Clean(section.Name) is { Length: > 0 } n ? n : PlanOutline.OtherSection)}");
            }

            foreach (var item in section.Items)
            {
                Line(Task(item, ++number));
            }
        }

        return code.ToString();
    }

    /// <summary>Markdown に貼れるよう、コードブロック（```mermaid）で囲む。GitHub の Issue や README で図として表示される。</summary>
    public static string AsMarkdown(string code) => $"```mermaid\n{code.TrimEnd()}\n```\n";

    private static string Task(OutlineItem item, int number)
    {
        var name = Clean(item.Name) is { Length: > 0 } n ? n : "（無題）";
        var tags = new List<string>();
        if (item.Milestone)
        {
            tags.Add("milestone");
        }

        if (item.Critical)
        {
            tags.Add("crit");
        }

        if (item.Done)
        {
            tags.Add("done");
        }
        else if (item.Active)
        {
            tags.Add("active");
        }

        var prefix = tags.Count > 0 ? string.Join(", ", tags) + ", " : "";
        return item.Milestone
            ? $"    {name} :{prefix}t{number}, {Date(item.Start)}, 0d"
            : $"    {name} :{prefix}t{number}, {Date(item.Start)}, {Date(item.End.AddDays(1))}";
    }

    /// <summary>
    /// 休日を Mermaid の excludes にする。休む曜日は曜日名で、それ以外の休日（祝日と独自の休日）は日付で並べる。
    /// 終了日を明示したバーは、Mermaid でも休日によって後ろへずれない。
    /// </summary>
    private static string Excludes(WorkCalendarRules calendar, IReadOnlySet<DateOnly> holidays) =>
        string.Join(", ", calendar.DaysOff.Order().Select(d => d.ToString().ToLowerInvariant())
            .Concat(holidays.Where(d => !calendar.DaysOff.Contains(d.DayOfWeek)).Order().Select(Date)));

    /// <summary>Mermaid の記法と衝突する文字（: # ; と改行）を置き換える。</summary>
    private static string Clean(string? text) =>
        (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace(':', '：').Replace('#', '＃').Replace(';', '；').Trim();

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
