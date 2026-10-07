using Tasklabe.Core.Calendar;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Export;
using Tasklabe.Core.Wbs;

namespace Tasklabe.Core.Tests;

/// <summary>共有ビューの形と、Mermaid への書き出し（要件 F-UI-GT-10）。</summary>
public class PlanOutlineTests
{
    private static DateOnly D(int m, int d) => new(2026, m, d);

    private static readonly DateOnly Today = D(9, 16);

    private static TaskItem T(string id, string? parent = null, DateOnly? start = null, DateOnly? target = null,
        StatusCategory category = StatusCategory.Todo, TaskKind kind = TaskKind.Task, string? title = null, params string[] assignees) => new()
    {
        ItemId = id,
        ProjectId = "P1",
        IssueId = "I-" + id,
        RepositoryNameWithOwner = "org/repo",
        Number = 1,
        Title = title ?? id,
        ParentIssueId = parent is null ? null : "I-" + parent,
        Start = start,
        Target = target,
        Category = category,
        Kind = kind,
        Assignees = assignees,
    };

    /// <summary>プロジェクトのマイルストーン（期限）。</summary>
    private static readonly Milestone[] Milestones = [new("M1", 1, "β", D(10, 1))];

    private static GanttOutline Outline(OutlineOptions options, params TaskItem[] tasks) =>
        PlanOutline.Build(TaskTree.Build(tasks), options, Today, WorkCalendarRules.Standard, milestones: Milestones);

    private static string Build(OutlineOptions options, params TaskItem[] tasks) =>
        MermaidGantt.ToCode(Outline(options, tasks), WorkCalendarRules.Standard);

    private static readonly TaskItem[] Plan =
    [
        T("設計", start: D(9, 1), target: D(9, 30)),
        T("画面", parent: "設計", start: D(9, 14), target: D(9, 18), category: StatusCategory.InProgress),
        T("DB", parent: "設計", start: D(9, 1), target: D(9, 10), category: StatusCategory.Done),
        T("レビュー", parent: "設計", start: D(9, 2), target: D(9, 9)),
        T("メモ"),
    ];

    [Fact]
    public void Tasks_become_bars_with_exclusive_end_dates_under_their_phase()
    {
        var code = Build(new OutlineOptions { Title = "モバイルアプリ", Holidays = false }, Plan);

        Assert.StartsWith("gantt\n    title モバイルアプリ\n    dateFormat YYYY-MM-DD\n", code, StringComparison.Ordinal);
        Assert.Contains("    section 設計", code, StringComparison.Ordinal);

        // Mermaid の終了日は含まないため、9/18 までのタスクは 9/19 で終える
        Assert.Contains("    画面 :active, ", code, StringComparison.Ordinal);
        Assert.Contains("2026-09-14, 2026-09-19", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_is_shown_with_done_active_and_crit()
    {
        var code = Build(new OutlineOptions(), Plan);

        Assert.Contains("    DB :done, ", code, StringComparison.Ordinal);

        // 予定終了日を過ぎた未完了のタスクは crit
        Assert.Contains("    レビュー :crit, ", code, StringComparison.Ordinal);
        Assert.Contains("    β :milestone, ", code, StringComparison.Ordinal);
        Assert.Contains("2026-10-01, 0d", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Parents_milestones_and_completed_tasks_can_be_left_out()
    {
        var options = new OutlineOptions { Milestones = false, Completed = false, StatusColors = false };
        var outline = Outline(options, Plan);
        var code = Build(options, Plan);

        Assert.DoesNotContain("β", code, StringComparison.Ordinal);
        Assert.DoesNotContain("DB", code, StringComparison.Ordinal);

        // 親タスクは既定では区切りにだけ使い、バーにしない
        Assert.DoesNotContain("    設計 :", code, StringComparison.Ordinal);
        Assert.Equal(2, outline.ItemCount);
        Assert.Equal(1, outline.Unscheduled);
    }

    [Fact]
    public void Parent_bars_are_written_when_asked()
    {
        var code = Build(new OutlineOptions { ParentBars = true }, Plan);

        Assert.Contains("    設計 :", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Date_range_keeps_tasks_that_overlap_it()
    {
        var code = Build(new OutlineOptions { Range = OutlineRange.Custom, From = D(9, 12), To = D(9, 20) }, Plan);

        Assert.Contains("画面", code, StringComparison.Ordinal);
        Assert.DoesNotContain("DB", code, StringComparison.Ordinal);
        Assert.DoesNotContain("β", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Sections_can_follow_assignees()
    {
        var code = Build(new OutlineOptions { Sections = OutlineSections.Assignee },
            T("a", start: D(9, 1), target: D(9, 2), assignees: "suzuki"),
            T("b", start: D(9, 1), target: D(9, 2)),
            T("c", start: D(9, 1), target: D(9, 2), assignees: "sato"));

        int sato = code.IndexOf("section @sato", StringComparison.Ordinal);
        int suzuki = code.IndexOf("section @suzuki", StringComparison.Ordinal);
        int none = code.IndexOf("section 未割り当て", StringComparison.Ordinal);
        Assert.True(sato >= 0 && sato < suzuki && suzuki < none);
    }

    [Fact]
    public void Characters_that_break_mermaid_are_replaced()
    {
        var code = Build(new OutlineOptions(), T("x", start: D(9, 1), target: D(9, 2), title: "API: 認証 #12; 対応"));

        Assert.Contains("API： 認証 ＃12； 対応 :", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Holidays_become_excludes_within_the_span()
    {
        // 9/21〜23 は祝日（敬老の日、国民の休日、秋分の日）
        var code = Build(new OutlineOptions(), T("x", start: D(9, 18), target: D(9, 24)));

        Assert.Contains("    excludes sunday, saturday, 2026-09-21, 2026-09-22, 2026-09-23\n", code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_view_keeps_the_original_names_and_knows_the_holidays()
    {
        // 共有ビューは Mermaid の記法の制約を受けないため、名前をそのまま持つ
        var outline = Outline(new OutlineOptions(), T("x", start: D(9, 18), target: D(9, 24), title: "API: 認証"));

        Assert.Equal("API: 認証", outline.Sections[0].Items[0].Name);
        Assert.Contains(D(9, 22), outline.Holidays);
        Assert.Contains(D(9, 19), outline.Holidays);
        Assert.DoesNotContain(D(9, 24), outline.Holidays);
    }

    [Fact]
    public void Markdown_wraps_the_code_in_a_mermaid_block()
    {
        Assert.Equal("```mermaid\ngantt\n```\n", MermaidGantt.AsMarkdown("gantt\n"));
    }
}
