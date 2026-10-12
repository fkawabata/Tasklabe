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
        HoverDelay.Text = row.DelayDays > 0 ? Texts.Count("Gantt_Delay", row.DelayDays) : "";
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
        var lines = new List<string>
        {
            Texts.Format("Card_Due", DateText.Long(s.Milestone.Due!.Value)),
            s.Total == 0 ? Texts.Get("Gantt_MilestoneEmpty") : Texts.Format("Gantt_MilestoneProgress", s.Progress.ProgressPercent, s.Done, s.Total),
        };
        if (s.Late > 0 && s.LateEnd is { } lateEnd)
        {
            lines.Add(Texts.Format("Gantt_MilestoneLate", s.Late, DateText.Short(lateEnd), s.LateDays));
        }

        HoverDetail.Text = string.Join('\n', lines);
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
            lines.Add(Texts.Get("Menu_NonBlocking"));
        }

        if (IsPreviewing && _originalPlans.TryGetValue(row.Task.ItemId, out var original) && PlanOf(row) is { } moved && moved != original)
        {
            lines.Add(Texts.Format("Gantt_Before", FormatDate(original.Start), FormatDate(original.End)));
        }

        lines.Add(row.HasPlan
            ? Texts.Count("Gantt_Plan", WorkCalendar.CountWorkingDays(row.PlanStart!.Value, row.PlanEnd!.Value), FormatDate(row.PlanStart!.Value), FormatDate(row.PlanEnd!.Value))
            : Texts.Get("Gantt_NoPlan"));
        if (row.FrameStart is { } frameStart && row.Node is { } early)
        {
            lines.Add(Texts.Count("Gantt_FrameEarly", PlanFrame.EarlyDays(early), FormatDate(frameStart)));
        }

        if (row.FrameEnd is { } frame && row.Node is { } node)
        {
            lines.Add(Texts.Count("Gantt_FrameOverrun", PlanFrame.OverrunDays(node), FormatDate(frame)));
        }
        if (row.ActualStart is { } a)
        {
            lines.Add(row.IsForecast
                ? Texts.Format("Gantt_ActualForecast", FormatDate(a), FormatDate(row.ActualEnd!.Value))
                : Texts.Format("Gantt_Actual", FormatDate(a), FormatDate(row.ActualEnd!.Value)));
        }

        var summary = row.Node!.Summary;
        lines.Add(Texts.Format("Gantt_ProgressEffort", summary.ProgressPercent, summary.EstimateHours > 0 ? EffortText.Of(summary.EstimateHours) : Texts.Get("Status_Unestimated")));
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
        yield return days < 1 ? Texts.Get("Gantt_PaceOnTrack")
            : reading.PaceDays > 0 ? Texts.Count("Gantt_PaceAhead", days)
            : Texts.Count("Gantt_PaceBehind", days);
        yield return reading is { Bottleneck: { } task, BottleneckEnd: { } end }
            ? Texts.Format("Gantt_ForecastLate", task.Title, FormatDate(end))
            : Texts.Get("Gantt_ForecastOnTrack");
    }

    /// <summary>読み上げ用の説明（タスク名、予定期間、進捗率、遅れの日数）。</summary>
    internal static string Describe(GanttRow row)
    {
        if (row.Milestone is { } m)
        {
            return string.Join(Texts.Get("Common_ListSeparator"), Texts.Format("Gantt_MilestoneName", m.Milestone.Title),
                Texts.Format("Card_Due", FormatDate(m.Milestone.Due!.Value)), Texts.Format("Card_Progress", row.ProgressPercent));
        }

        var parts = new List<string> { row.Task.Title };
        parts.Add(row.HasPlan ? Texts.Format("Gantt_PlanName", FormatDate(row.PlanStart!.Value), FormatDate(row.PlanEnd!.Value)) : Texts.Get("Gantt_NoPlanName"));
        parts.Add(Texts.Format("Card_Progress", row.ProgressPercent));

        if (row.DelayDays > 0)
        {
            parts.Add(Texts.Count("Gantt_Delay", row.DelayDays));
        }

        parts.AddRange(PhaseLines(row));
        return string.Join(Texts.Get("Common_ListSeparator"), parts);
    }

    private static string FormatDate(DateOnly date) => DateText.Short(date);
}
