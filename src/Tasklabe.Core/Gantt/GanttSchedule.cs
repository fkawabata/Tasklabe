using Tasklabe.Core.Calendar;
using Tasklabe.Core.Domain;

namespace Tasklabe.Core.Gantt;

/// <summary>ガントチャートの算出値（要件定義書 6.3 節）。</summary>
public static class GanttSchedule
{
    /// <summary>見込み終了日を求めるときの残りの稼働日数の上限。進捗がごくわずかなタスクで日付が発散しないようにする。</summary>
    public const int MaxForecastWorkingDays = 500;

    /// <summary>
    /// 実績バーの終端。完了していれば実績終了日、進行中なら見込み終了日。実績開始日がなければ null。
    /// </summary>
    public static DateOnly? ActualOrForecastEnd(TaskItem task, DateOnly today)
    {
        if (task.ActualStart is not { } started)
        {
            return task.IsDone ? task.ActualEnd : null;
        }

        if (task.IsDone)
        {
            return task.ActualEnd is { } end && end >= started ? end : started;
        }

        return ForecastEnd(started, task.EffectiveProgress, PlannedWorkingDays(task), today);
    }

    /// <summary>
    /// 見込み終了日。実績開始日から今日までの稼働日数と進捗率から、残りの稼働日数を按分して求める。
    /// 進捗率が 0 % のときは、実績開始日に予定期間の稼働日数を加えた日とする。今日より前にはしない。
    /// </summary>
    public static DateOnly ForecastEnd(DateOnly actualStart, double progressPercent, int plannedWorkingDays, DateOnly today)
    {
        DateOnly forecast;
        if (progressPercent <= 0)
        {
            forecast = WorkCalendar.NthWorkingDay(actualStart, Math.Max(plannedWorkingDays, 1));
        }
        else
        {
            int elapsed = WorkCalendar.CountWorkingDays(actualStart, today);
            double remaining = elapsed * (100 - Math.Min(progressPercent, 100)) / progressPercent;
            int days = (int)Math.Min(Math.Ceiling(remaining - 1e-9), MaxForecastWorkingDays);
            forecast = days <= 0 ? today : WorkCalendar.NthWorkingDay(today.AddDays(1), days);
        }

        return forecast < today ? today : forecast;
    }

    /// <summary>予定期間の稼働日数（少なくとも 1）。予定がなければ 1。</summary>
    public static int PlannedWorkingDays(TaskItem task) =>
        task.Start is { } start && task.Target is { } target && target >= start
            ? Math.Max(WorkCalendar.CountWorkingDays(start, target), 1)
            : 1;

    /// <summary>
    /// イナズマ線の点（日単位の位置。日付 d の始まりを d.DayNumber とする）。
    /// 予定開始日から予定期間 × 進捗率だけ進んだ位置とする。ただし、完了したタスクと、
    /// 予定の開始前で未着手のタスクは遅れていないため今日の位置とする（完了が予定より早ければ予定の終端）。
    /// 予定がなければ null。
    /// </summary>
    public static double? InazumaPoint(DateOnly planStart, DateOnly planEnd, double progressPercent, bool isDone, DateOnly today)
    {
        double start = planStart.DayNumber;
        double end = planEnd.DayNumber + 1;
        double now = today.DayNumber;

        if (isDone)
        {
            return Math.Max(now, end);
        }

        double position = start + (end - start) * Math.Clamp(progressPercent, 0, 100) / 100.0;
        return progressPercent <= 0 && start >= now ? now : position;
    }

    /// <summary>予定終了日を過ぎた稼働日数（遅れがなければ 0）。</summary>
    public static int DelayWorkingDays(DateOnly planEnd, DateOnly actualEnd) =>
        actualEnd > planEnd ? WorkCalendar.CountWorkingDays(planEnd.AddDays(1), actualEnd) : 0;
}
