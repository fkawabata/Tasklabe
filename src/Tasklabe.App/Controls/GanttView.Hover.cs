using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Tasklabe.App.Services;
using Tasklabe.Core.Calendar;
using Tasklabe.Core.Gantt;
using Tasklabe.Core.Milestones;
using Tasklabe.Core.Wbs;
using Windows.Foundation;

namespace Tasklabe.App.Controls;

/// <summary>
/// ガントチャートのホバーの説明。タスクとマイルストーンの予定・実績・状態を添える。
/// </summary>
public sealed partial class GanttView
{
    private void ShowHover(GanttRow row, Point position)
    {
        HoverTitle.Text = row.Task.Title;
        HoverDetail.Text = Details(row);
        HoverDelay.Text = row.DelayDays > 0 ? $"遅れ {row.DelayDays} 日" : "";
        HoverDelay.Visibility = row.DelayDays > 0 ? Visibility.Visible : Visibility.Collapsed;
        HoverCard.Visibility = Visibility.Visible;
        HoverCard.Measure(new Size(320, 400));
        var size = HoverCard.DesiredSize;
        Canvas.SetLeft(HoverCard, Math.Clamp(position.X + 16, 0, Math.Max(Chart.ActualWidth - size.Width - 4, 0)));
        double top = position.Y + 20;
        Canvas.SetTop(HoverCard, top + size.Height > Chart.ActualHeight ? position.Y - size.Height - 8 : top);
    }

    private void HideHover() => HoverCard.Visibility = Visibility.Collapsed;

    /// <summary>マイルストーンの期限までの集計（要件 F-MS-04）。</summary>
    private void ShowMilestoneHover(MilestoneSummary s, Point position)
    {
        HoverTitle.Text = "◆ " + s.Milestone.Title;
        HoverDetail.Text = $"期日 {DateText.Long(s.Milestone.Due!.Value)}\n"
            + (s.Total == 0 ? "このマイルストーンのタスクはありません" : $"進捗 {s.Progress.ProgressPercent:0}%　完了 {s.Done} / {s.Total} 件")
            + (s.Late > 0 && s.LateEnd is { } lateEnd ? $"\n⚠ 期日超え {s.Late} 件（最も遅い終了 {DateText.Short(lateEnd)}・▲{s.LateDays}日）" : "");
        HoverDelay.Visibility = Visibility.Collapsed;
        HoverCard.Visibility = Visibility.Visible;
        HoverCard.Measure(new Size(320, 400));
        Canvas.SetLeft(HoverCard, Math.Clamp(position.X + 16, 0, Math.Max(Chart.ActualWidth - HoverCard.DesiredSize.Width - 4, 0)));
        Canvas.SetTop(HoverCard, position.Y + 20);
    }

    private string Details(GanttRow row)
    {
        var lines = new List<string>();
        if (row.Task.NonBlocking)
        {
            lines.Add("後続を待たせない");
        }

        if (IsPreviewing && _originalPlans.TryGetValue(row.Task.ItemId, out var original) && PlanOf(row) is { } moved && moved != original)
        {
            lines.Add($"調整前　{FormatDate(original.Start)} 〜 {FormatDate(original.End)}");
        }

        lines.Add(row.HasPlan
            ? $"予定　{FormatDate(row.PlanStart!.Value)} 〜 {FormatDate(row.PlanEnd!.Value)}（{WorkCalendar.CountWorkingDays(row.PlanStart.Value, row.PlanEnd.Value)} 稼働日）"
            : "予定　—");
        if (row.FrameStart is { } frameStart && row.Node is { } early)
        {
            lines.Add($"枠超え　子の予定が枠（{FormatDate(frameStart)}〜）より {PlanFrame.EarlyDays(early)} 稼働日前に始まります");
        }

        if (row.FrameEnd is { } frame && row.Node is { } node)
        {
            lines.Add($"枠超え　子の予定が枠（〜{FormatDate(frame)}）を {PlanFrame.OverrunDays(node)} 稼働日超えています");
        }
        if (row.ActualStart is { } a)
        {
            lines.Add(row.IsForecast
                ? $"実績　{FormatDate(a)} 〜（見込み終了 {FormatDate(row.ActualEnd!.Value)}）"
                : $"実績　{FormatDate(a)} 〜 {FormatDate(row.ActualEnd!.Value)}");
        }

        var summary = row.Node!.Summary;
        lines.Add($"進捗　{summary.ProgressPercent:0}%　工数　{(summary.EstimateHours > 0 ? EffortText.Of(summary.EstimateHours) : "未見積り")}");
        lines.AddRange(PhaseLines(row));
        return string.Join('\n', lines);
    }

    /// <summary>
    /// 親タスクの、イナズマ線（仕事の量の進み具合）とバーの色（最も遅い作業の見込み）がそれぞれ何を示しているか。
    /// 両者が食い違っても、量が足りないのか、1 つの作業が終わりを延ばしているのかを読み分けられるようにする。
    /// </summary>
    private static IEnumerable<string> PhaseLines(GanttRow row)
    {
        if (GanttModel.ReadPhase(row, Today) is not { } reading)
        {
            yield break;
        }

        int days = (int)Math.Round(Math.Abs(reading.PaceDays));
        yield return "仕事の量　" + (days < 1 ? "予定どおり" : reading.PaceDays > 0 ? $"予定より約 {days} 日分先行" : $"予定より約 {days} 日分遅れ") + "（イナズマ線）";
        yield return reading is { Bottleneck: { } task, BottleneckEnd: { } end }
            ? $"終わりの見込み　「{task.Title}」が {FormatDate(end)} まで延び、予定より遅れる見込み（バーの色）"
            : "終わりの見込み　予定内（バーの色）";
    }

    /// <summary>読み上げ用の説明（タスク名、予定期間、進捗率、遅れの日数）。</summary>
    internal static string Describe(GanttRow row)
    {
        if (row.Milestone is { } m)
        {
            return $"マイルストーン {m.Milestone.Title}、期日 {FormatDate(m.Milestone.Due!.Value)}、進捗 {row.ProgressPercent:0}%";
        }

        var parts = new List<string> { row.Task.Title };
        parts.Add(row.HasPlan ? $"予定 {FormatDate(row.PlanStart!.Value)} から {FormatDate(row.PlanEnd!.Value)}" : "予定 なし");
        parts.Add($"進捗 {row.ProgressPercent:0}%");

        if (row.DelayDays > 0)
        {
            parts.Add($"遅れ {row.DelayDays} 日");
        }

        parts.AddRange(PhaseLines(row));
        return string.Join("、", parts);
    }

    private static string FormatDate(DateOnly date) => DateText.Short(date);
}
