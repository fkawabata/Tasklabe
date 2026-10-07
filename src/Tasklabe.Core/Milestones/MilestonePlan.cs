using Tasklabe.Core.Calendar;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Progress;
using Tasklabe.Core.Wbs;

namespace Tasklabe.Core.Milestones;

/// <summary>マイルストーンまでの計画の集計（要件 F-MS-04）。</summary>
/// <param name="Progress">子を持たない計画のタスクの、想定工数 × 進捗率の積み上げ。</param>
/// <param name="Done">完了したタスクの数（中止を除く）。</param>
/// <param name="Total">タスクの数（中止を除く）。</param>
/// <param name="Late">予定終了日が期日より後の、終わっていないタスクの数（期日超え）。</param>
/// <param name="LateEnd">期日超えのタスクの、最も遅い予定終了日。期日超えがなければ null。</param>
public sealed record MilestoneSummary(Milestone Milestone, ProgressSummary Progress, int Done, int Total, int Late = 0, DateOnly? LateEnd = null)
{
    /// <summary>期日超えの最も遅い予定終了日が、期日から何稼働日後か。期日超えがなければ 0。</summary>
    public int LateDays => LateEnd is { } end && Milestone.Due is { } due && end > due ? WorkCalendar.CountWorkingDays(due.AddDays(1), end) : 0;
}

/// <summary>
/// マイルストーンとタスクの関係の決まり（要件 F-MS-02〜04、技術設計書 3.2.4 節）。
/// マイルストーンへの所属は「この期日までに終える」という約束とし、日程がずれても動かさない。ずれは期日超えとして集計に示す。
/// 期日（予定終了日）から自動で入り先を決めるのは、タスクに初めて期日が入ったときと、マイルストーンを足したときだけとする。
/// </summary>
public static class MilestonePlan
{
    private const string OwnerPrefix = "[tasklabe:project:";

    /// <summary>
    /// マイルストーンを持つプロジェクトの記号。Milestone はリポジトリごとのため、同じリポジトリを使うプロジェクトどうしで
    /// 混ざらないよう、作ったプロジェクトを Milestone の説明欄に記す。
    /// </summary>
    public static string OwnerMarker(string projectId) => OwnerPrefix + projectId + "]";

    /// <summary>説明欄に、プロジェクトの記号を書き足す。既にあればそのまま返す。</summary>
    public static string WithOwner(string? description, string projectId)
    {
        var marker = OwnerMarker(projectId);
        if (description?.Contains(marker, StringComparison.Ordinal) == true)
        {
            return description;
        }

        return string.IsNullOrWhiteSpace(description) ? marker : description.TrimEnd() + "\n\n" + marker;
    }

    /// <summary>このプロジェクトが記号を付けていないマイルストーンか（記号を足して自分のものにする必要があるか）。</summary>
    public static bool NeedsOwner(Milestone milestone, string projectId)
    {
        ArgumentNullException.ThrowIfNull(milestone);
        return milestone.Description?.Contains(OwnerMarker(projectId), StringComparison.Ordinal) != true;
    }

    /// <summary>
    /// リポジトリの Milestone のうち、このプロジェクトのマイルストーン（要件 F-MS-01）。このプロジェクトの記号を持つものと、
    /// どのプロジェクトの記号も持たず（GitHub 上で作ったもの）、このプロジェクトのタスクが入っているものとする。
    /// </summary>
    public static IReadOnlyList<Milestone> ForProject(string projectId, IEnumerable<Milestone> milestones, IEnumerable<TaskItem> tasks)
    {
        ArgumentNullException.ThrowIfNull(milestones);
        ArgumentNullException.ThrowIfNull(tasks);
        var marker = OwnerMarker(projectId);
        var used = tasks.Select(t => t.MilestoneId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return [.. milestones.Where(m => m.Description?.Contains(marker, StringComparison.Ordinal) == true
            || (m.Description?.Contains(OwnerPrefix, StringComparison.Ordinal) != true && used.Contains(m.Id)))];
    }

    /// <summary>期日の順に並べたマイルストーン（期日のないものは区切りにならないため除く）。</summary>
    public static IReadOnlyList<Milestone> Ordered(IEnumerable<Milestone> milestones)
    {
        ArgumentNullException.ThrowIfNull(milestones);
        return [.. milestones.Where(m => m.Due is not null).OrderBy(m => m.Due).ThenBy(m => m.Number)];
    }

    /// <summary>期日から自動で決まるマイルストーン。期日がないか、最後のマイルストーンより後なら null。</summary>
    public static Milestone? Auto(DateOnly? target, IEnumerable<Milestone> milestones) =>
        target is { } t ? Ordered(milestones).FirstOrDefault(m => m.Due >= t) : null;

    /// <summary>
    /// 期日を変えたときのマイルストーン。所属は約束のため、期日を変えても動かさない（期日を過ぎるなら期日超えとして示す）。
    /// 初めて期日が入ったタスク（期日がなく、どこにも入っていなかったもの）だけ、期日から入り先を決める。
    /// </summary>
    public static string? Follow(string? current, DateOnly? oldTarget, DateOnly? newTarget, IReadOnlyList<Milestone> milestones)
    {
        ArgumentNullException.ThrowIfNull(milestones);
        return current is null && oldTarget is null ? Auto(newTarget, milestones)?.Id : current;
    }

    /// <summary>
    /// マイルストーンを足した・変えた・消したときのマイルストーン。期日を変えても所属は動かさない。
    /// 消したマイルストーンに入っていたものは、期日から入り先を決め直す。
    /// 足したマイルストーンには、期日からそこに入るもののうち、いまの所属が期日どおり（変更前に期日から決まるもの）のものだけを移す。
    /// </summary>
    public static string? Reassign(string? current, DateOnly? target, IReadOnlyList<Milestone> before, IReadOnlyList<Milestone> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (current is not null && after.All(m => m.Id != current))
        {
            return Auto(target, after)?.Id;
        }

        var auto = Auto(target, after);
        bool added = auto is not null && before.All(m => m.Id != auto.Id);
        return added && current == Auto(target, before)?.Id ? auto!.Id : current;
    }

    /// <summary>マイルストーンを持たせる対象（計画のタスク）。課題は計画に入るまで期限に数えない。</summary>
    public static bool Applies(TaskItem task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return task.Kind == TaskKind.Task;
    }

    /// <summary>マイルストーンに入っている、子を持たない計画のタスク（中止を除く）。集計（Summarize）と同じものを数える。</summary>
    public static IReadOnlyList<TaskItem> Members(TaskTree plan, string milestoneId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return [.. plan.All()
            .Where(n => !n.HasChildren && Applies(n.Task) && !n.Task.IsCanceled && n.Task.MilestoneId == milestoneId)
            .Select(n => n.Task)];
    }

    /// <summary>
    /// 今日までに進んでいるはずの進捗率（予定の出来高）。各タスクの想定工数を予定期間へ均等に割り付け、今日までの分を足す
    /// （予定比と同じ考え方。要件定義書 6.3 節）。予定開始日と予定終了日のあるタスクだけで求め、なければ null。
    /// </summary>
    public static double? ExpectedPercent(IEnumerable<TaskItem> tasks, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        double total = 0, expected = 0;
        foreach (var t in tasks)
        {
            if (t.Start is not { } start || t.Target is not { } end || end < start)
            {
                continue;
            }

            double weight = t.EstimateHours ?? ProgressCalculator.UnestimatedWeight;
            double days = end.DayNumber - start.DayNumber + 1;
            total += weight;
            expected += weight * Math.Clamp((today.DayNumber - start.DayNumber + 1) / days, 0, 1);
        }

        return total > 0 ? expected / total * 100 : null;
    }

    /// <summary>
    /// マイルストーンごとの集計。子を持たない計画のタスクを、所属するマイルストーンごとに数える（中止は除く）。
    /// 予定終了日が期日より後の、終わっていないタスクは期日超えとして数える。
    /// 期日の順に並べ、期日のないマイルストーンは末尾に置く。
    /// </summary>
    public static IReadOnlyList<MilestoneSummary> Summarize(TaskTree plan, IReadOnlyList<Milestone> milestones)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(milestones);
        var leaves = plan.All()
            .Where(n => !n.HasChildren && Applies(n.Task) && !n.Task.IsCanceled && n.Task.MilestoneId is not null)
            .GroupBy(n => n.Task.MilestoneId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        return [.. Ordered(milestones).Concat(milestones.Where(m => m.Due is null)).Select(m =>
        {
            var nodes = leaves.GetValueOrDefault(m.Id) ?? [];
            var late = nodes.Where(n => !n.Task.IsDone && n.Task.Target > m.Due).Select(n => n.Task.Target!.Value).ToList();
            return new MilestoneSummary(m, ProgressCalculator.SummarizeAll(nodes), nodes.Count(n => n.Task.IsCompleted), nodes.Count,
                late.Count, late.Count > 0 ? late.Max() : null);
        })];
    }
}
