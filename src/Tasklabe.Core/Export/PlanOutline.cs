using System.Globalization;
using Tasklabe.Core.Calendar;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Gantt;
using Tasklabe.Core.Wbs;

namespace Tasklabe.Core.Export;

/// <summary>対象にする期間（要件 F-UI-GT-10）。</summary>
public enum OutlineRange
{
    /// <summary>予定のあるすべてのタスク。</summary>
    All,

    /// <summary>今日の 1 週間前から 4 週間後まで。</summary>
    AroundToday,

    ThisMonth,

    NextMonth,

    /// <summary>開始日と終了日を指定する。</summary>
    Custom,
}

/// <summary>区切りの分け方。</summary>
public enum OutlineSections
{
    /// <summary>最上位の親タスク（フェーズ）ごと。</summary>
    TopLevel,

    /// <summary>担当者ごと。</summary>
    Assignee,

    None,
}

/// <summary>共有ビューの範囲と表示（UI デザイン設計書 3.3.6 節）。ビューと Mermaid の書き出しの両方に使う。</summary>
public sealed record OutlineOptions
{
    public string Title { get; init; } = "";

    public OutlineRange Range { get; init; } = OutlineRange.All;

    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }

    public OutlineSections Sections { get; init; } = OutlineSections.TopLevel;

    public bool Milestones { get; init; } = true;

    /// <summary>親タスク（フェーズ）もバーとして出す。</summary>
    public bool ParentBars { get; init; }

    public bool Completed { get; init; } = true;

    public bool Canceled { get; init; }

    /// <summary>状態を色で示す（完了・進行中・遅れ）。</summary>
    public bool StatusColors { get; init; } = true;

    public bool AssigneeInName { get; init; }

    public bool ProgressInName { get; init; }

    /// <summary>休日（プロジェクトの稼働日の設定）を網掛けする。</summary>
    public bool Holidays { get; init; } = true;

    public bool TodayMarker { get; init; } = true;
}

/// <summary>バーまたはマイルストーン 1 つ。</summary>
/// <param name="Name">表示する名前（担当者・進捗率を添えたもの）。</param>
/// <param name="Active">進行中（状態の色を使うときだけ true になる。以下同じ）。</param>
/// <param name="Critical">未完了のまま予定終了日を過ぎている（マイルストーンは期限の目印のため、過ぎても立てない）。</param>
public sealed record OutlineItem(string Name, DateOnly Start, DateOnly End, bool Milestone, bool Done, bool Active, bool Critical);

/// <summary>区切り 1 つ。区切らないときは名前が空の 1 つだけになる。</summary>
public sealed record OutlineSection(string Name, IReadOnlyList<OutlineItem> Items);

/// <summary>
/// 共有ビューに描き、Mermaid に書き出す計画の形（要件 F-UI-GT-10）。ビューと書き出しを同じ形から作り、食い違わないようにする。
/// </summary>
/// <param name="Unscheduled">予定がないため含めなかったタスクの数。</param>
/// <param name="Span">含めた予定が及ぶ期間。何も含めなかったときは null。</param>
/// <param name="Holidays">期間の中の休日（休日を網掛けしないときは空）。</param>
public sealed record GanttOutline(
    string Title,
    IReadOnlyList<OutlineSection> Sections,
    int Unscheduled,
    (DateOnly Start, DateOnly End)? Span,
    IReadOnlySet<DateOnly> Holidays,
    bool ShowSections,
    bool TodayMarker)
{
    public int ItemCount => Sections.Sum(s => s.Items.Count);
}

/// <summary>ガントチャートの行から、共有ビューの形を作る。</summary>
public static class PlanOutline
{
    public const string OtherSection = "その他";
    public const string Unassigned = "未割り当て";
    public const string MilestoneSection = "マイルストーン";

    /// <param name="visible">画面の絞り込み（担当者など）。一致しないタスクは含めない。</param>
    /// <param name="milestones">プロジェクトのマイルストーン（期限。要件 F-MS-03）。期日に ◆ として置く。</param>
    public static GanttOutline Build(TaskTree tree, OutlineOptions options, DateOnly today, WorkCalendarRules calendar,
        Func<TaskNode, bool>? visible = null, IReadOnlyList<Milestone>? milestones = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(calendar);

        var (from, to) = RangeOf(options, today);
        var rows = GanttModel.Build(tree.All().Where(n => visible?.Invoke(n) ?? true), today);

        var sections = new List<(string Name, List<OutlineItem> Items)>();
        int unscheduled = 0;
        DateOnly? min = null, max = null;
        foreach (var row in rows)
        {
            var task = row.Task;
            bool parent = row.Kind == GanttRowKind.Parent;
            if ((parent && !options.ParentBars)
                || (task.IsCanceled ? !options.Canceled : row.IsDone && !options.Completed))
            {
                continue;
            }

            if (row.PlanStart is not { } start || row.PlanEnd is not { } end)
            {
                if (!parent)
                {
                    unscheduled++;
                }

                continue;
            }

            if ((from is { } f && end < f) || (to is { } t && start > t))
            {
                continue;
            }

            var name = options.Sections switch
            {
                OutlineSections.TopLevel => TopLevelName(row.Node!),
                OutlineSections.Assignee => task.Assignees.Count == 0 ? Unassigned : string.Join("・", task.Assignees.Select(People.Display)),
                _ => "",
            };
            var items = sections.FirstOrDefault(s => s.Name == name).Items;
            if (items is null)
            {
                items = [];
                sections.Add((name, items));
            }

            items.Add(Item(row, options, start, end, today));
            min = min is { } m && m <= start ? m : start;
            max = max is { } x && x >= end ? x : end;
        }

        // 担当者ごとの区切りは、担当者の名前の順に並べる（未割り当ては末尾）
        if (options.Sections == OutlineSections.Assignee)
        {
            sections = [.. sections.OrderBy(s => s.Name == Unassigned).ThenBy(s => s.Name, StringComparer.CurrentCulture)];
        }

        // プロジェクトのマイルストーンは期限の目印で、どの区切りにも属さないため、期日の順に先頭へまとめる。
        // 期限は区切りとして示すだけで、過ぎても遅れの色にはしない
        if (options.Milestones)
        {
            var marks = new List<OutlineItem>();
            foreach (var m in (milestones ?? []).Where(m => m.Due is not null).OrderBy(m => m.Due))
            {
                var due = m.Due!.Value;
                if ((from is { } f2 && due < f2) || (to is { } t2 && due > t2))
                {
                    continue;
                }

                marks.Add(new OutlineItem(string.IsNullOrWhiteSpace(m.Title) ? "（無題）" : m.Title.Trim(), due, due, true,
                    options.StatusColors && m.IsClosed, false, false));
                min = min is { } m1 && m1 <= due ? m1 : due;
                max = max is { } x1 && x1 >= due ? x1 : due;
            }

            if (marks.Count > 0)
            {
                if (options.Sections == OutlineSections.None && sections.Count > 0)
                {
                    sections[0].Items.InsertRange(0, marks);
                }
                else
                {
                    sections.Insert(0, (options.Sections == OutlineSections.None ? "" : MilestoneSection, marks));
                }
            }
        }

        var holidays = new HashSet<DateOnly>();
        if (options.Holidays && min is { } a && max is { } b)
        {
            for (var d = a; d <= b; d = d.AddDays(1))
            {
                if (calendar.IsHoliday(d))
                {
                    holidays.Add(d);
                }
            }
        }

        return new GanttOutline(
            options.Title.Trim(),
            [.. sections.Select(s => new OutlineSection(s.Name, s.Items))],
            unscheduled,
            min is { } s0 && max is { } e0 ? (s0, e0) : null,
            holidays,
            options.Sections != OutlineSections.None,
            options.TodayMarker);
    }

    /// <summary>期間の設定を日付の範囲にする。範囲を決めないときは null。</summary>
    public static (DateOnly? From, DateOnly? To) RangeOf(OutlineOptions options, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(options);
        var month = new DateOnly(today.Year, today.Month, 1);
        return options.Range switch
        {
            OutlineRange.AroundToday => (today.AddDays(-7), today.AddDays(28)),
            OutlineRange.ThisMonth => (month, month.AddMonths(1).AddDays(-1)),
            OutlineRange.NextMonth => (month.AddMonths(1), month.AddMonths(2).AddDays(-1)),
            OutlineRange.Custom => (options.From, options.To),
            _ => (null, null),
        };
    }

    private static OutlineItem Item(GanttRow row, OutlineOptions options, DateOnly start, DateOnly end, DateOnly today)
    {
        var task = row.Task;
        var name = string.IsNullOrWhiteSpace(task.Title) ? "（無題）" : task.Title.Trim();
        if (options.AssigneeInName && task.Assignees.Count > 0)
        {
            name += " " + string.Join(" ", task.Assignees.Select(People.Display));
        }

        if (options.ProgressInName)
        {
            name += string.Create(CultureInfo.InvariantCulture, $" {row.ProgressPercent:0}%");
        }

        bool status = options.StatusColors;
        bool critical = status && !row.IsDone && end < today;
        bool active = status && !row.IsDone && (task.Category == StatusCategory.InProgress || (row.Kind == GanttRowKind.Parent && row.ProgressPercent > 0));
        return new OutlineItem(name, start, end, false, status && row.IsDone, active, critical);
    }

    private static string TopLevelName(TaskNode node)
    {
        var root = node;
        while (root.Parent is not null)
        {
            root = root.Parent;
        }

        // 親を持たない単独のタスクは「その他」にまとめる
        return root.HasChildren ? root.Task.Title : OtherSection;
    }
}
