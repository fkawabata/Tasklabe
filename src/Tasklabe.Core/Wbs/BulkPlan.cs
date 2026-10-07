using Tasklabe.Core.Calendar;

namespace Tasklabe.Core.Wbs;

/// <summary>まとめて入力する行の区分（要件 F-UI-WBS-05）。</summary>
public enum BulkKind
{
    /// <summary>親タスク（フェーズ）。開始と終了を持つ。</summary>
    Parent,

    /// <summary>マイルストーン。期日だけを持つ。</summary>
    Milestone,
}

/// <summary>開始日の決め方。</summary>
public enum BulkStart
{
    /// <summary>前の行の終わりの翌稼働日にする。</summary>
    AfterPrevious,

    /// <summary>空けておく（利用者が選ぶ）。</summary>
    Blank,
}

/// <summary>予定のピッカーの候補（開始からの期間）。</summary>
public enum BulkSpan
{
    OneWeek,
    TwoWeeks,
    OneMonth,

    /// <summary>前の行と同じ稼働日数。</summary>
    SameAsPrevious,
}

/// <summary>
/// 利用者が入力した 1 行。開始は利用者が選んだときだけ持つ（自動で決めた値は持たない）。
/// </summary>
/// <param name="Unlinked">先行の自動のつなぎを ✕ で外した。</param>
public sealed record BulkRow(BulkKind Kind, string Title, DateOnly? Start = null, DateOnly? End = null, bool Unlinked = false)
{
    /// <summary>名前のない行は作らない（末尾の入力中の行など）。</summary>
    public bool IsBlank => string.IsNullOrWhiteSpace(Title);
}

/// <summary>自動の値を補った 1 行。</summary>
/// <param name="Index">入力の行の番号（0 から）。</param>
/// <param name="StartIsAuto">開始を自動で決めたか（画面では薄く示す）。</param>
/// <param name="EndIsAuto">終了（マイルストーンの期日）を自動で決めたか。</param>
/// <param name="WorkingDays">開始から終了までの稼働日数（親タスクで、両方あるときだけ）。</param>
/// <param name="Predecessor">先行としてつなぐ行の番号（いつも親タスク）。つながなければ null。マイルストーンはつながない。</param>
/// <param name="CanLink">前の親タスクとつなげる親タスクの行か（✕ で外したものを含む）。</param>
/// <param name="Problem">この行を作れない理由。作れるなら null。</param>
public sealed record BulkResolvedRow(
    int Index, BulkKind Kind, string Title, DateOnly? Start, bool StartIsAuto, DateOnly? End, bool EndIsAuto, int? WorkingDays,
    int? Predecessor, bool CanLink, string? Problem);

/// <summary>
/// 計画をまとめて入力するときの、自動で入れる値の決まり（要件 F-UI-WBS-05、UI デザイン設計書 3.4.6 節）。
/// 稼働日はいまの暦（<see cref="WorkCalendar.Current"/>）で数える。
/// </summary>
public static class BulkPlan
{
    /// <summary>
    /// 行に自動の値を補う。名前のない行は除く。
    /// マイルストーンは作業ではなく節目のため、親タスクのつながりには入れない。基準はいつも前の親タスクとし、
    /// 親タスクの開始は、利用者が選んでいなければ前の親タスクの終わりの翌稼働日にする。
    /// マイルストーンの期日は、選んでいなければ前の親タスクの終わりにする。
    /// 先行は、つなぐ設定のとき前の親タスクにする（✕ で外した行を除く）。マイルストーンは期限のため、依存関係を持たない。
    /// </summary>
    public static IReadOnlyList<BulkResolvedRow> Resolve(IReadOnlyList<BulkRow> rows, BulkStart start, bool link)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var result = new List<BulkResolvedRow>();
        BulkResolvedRow? previous = null;  // 前の親タスク
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.IsBlank)
            {
                continue;
            }

            DateOnly? begin = null;
            bool auto = false;
            DateOnly? end = row.End;
            bool endAuto = false;
            if (row.Kind == BulkKind.Milestone && end is null && previous?.End is { } phaseEnd)
            {
                end = phaseEnd;
                endAuto = true;
            }

            if (row.Kind == BulkKind.Parent)
            {
                if (row.Start is { } chosen)
                {
                    begin = chosen;
                }
                else if (start == BulkStart.AfterPrevious && previous?.End is { } previousEnd)
                {
                    begin = NextWorkingDay(previousEnd);
                    auto = true;
                }
            }

            int? days = begin is { } s && end is { } e && e >= s ? WorkCalendar.CountWorkingDays(s, e) : null;
            string? problem = begin is { } s2 && end is { } e2 && e2 < s2 ? "終了が開始より前です" : null;
            // マイルストーンは期限（GitHub の Milestone）で、依存関係を持たない
            bool canLink = previous is not null && row.Kind == BulkKind.Parent;
            int? predecessor = link && canLink && !row.Unlinked ? previous!.Index : null;

            var resolved = new BulkResolvedRow(i, row.Kind, row.Title.Trim(), begin, auto, end, endAuto, days, predecessor, canLink, problem);
            result.Add(resolved);
            if (row.Kind == BulkKind.Parent)
            {
                previous = resolved;
            }
        }

        return result;
    }

    /// <summary>前の行（名前のある行）の稼働日数。前の行がないか日程が決まっていなければ null。</summary>
    public static int? PreviousWorkingDays(IReadOnlyList<BulkResolvedRow> resolved, int index)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        return resolved.LastOrDefault(r => r.Index < index && r.Kind == BulkKind.Parent)?.WorkingDays;
    }

    /// <summary>
    /// 開始からの期間で決める終了日。週と月は暦で数え（10/5 からの 1 週間は 10/11 まで）、休みに当たるときは前の稼働日に寄せる。
    /// 前の行と同じ期間は、同じ稼働日数を数える。
    /// </summary>
    public static DateOnly? EndAfter(DateOnly start, BulkSpan span, int? previousWorkingDays = null) => span switch
    {
        BulkSpan.OneWeek => LastWorkingDayOnOrBefore(start.AddDays(6), start),
        BulkSpan.TwoWeeks => LastWorkingDayOnOrBefore(start.AddDays(13), start),
        BulkSpan.OneMonth => LastWorkingDayOnOrBefore(start.AddMonths(1).AddDays(-1), start),
        BulkSpan.SameAsPrevious => previousWorkingDays is int n and > 0 ? WorkCalendar.NthWorkingDay(start, n) : null,
        _ => null,
    };

    /// <summary>date の翌稼働日。</summary>
    public static DateOnly NextWorkingDay(DateOnly date) => WorkCalendar.NthWorkingDay(date.AddDays(1), 1);

    private static DateOnly LastWorkingDayOnOrBefore(DateOnly date, DateOnly notBefore)
    {
        for (var d = date; d >= notBefore; d = d.AddDays(-1))
        {
            if (WorkCalendar.IsWorkingDay(d))
            {
                return d;
            }
        }

        return date;
    }
}
