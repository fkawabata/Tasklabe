using Tasklabe.Core.Calendar;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Settings;

namespace Tasklabe.Core.Tests;

/// <summary>全体の既定と、プロジェクトごとの上書き（要件 F-SET-01〜04）。</summary>
public class ProjectSettingsTests
{
    [Fact]
    public void Unset_items_follow_the_defaults()
    {
        var defaults = DefaultSettings.Team with { HoursPerDay = 7.5 };
        var overrides = new ProjectSettings { EffortUnit = EffortUnit.Days };

        var resolved = overrides.Over(defaults);

        Assert.Equal(EffortUnit.Days, resolved.EffortUnit);
        Assert.Equal(7.5, resolved.HoursPerDay);
        Assert.Equal(TaskKind.Issue, resolved.NewTaskKind);
        Assert.True(resolved.Calendar.IsStandard);
    }

    [Fact]
    public void Settings_round_trip_through_json()
    {
        var calendar = new WorkCalendarRules(new HashSet<DayOfWeek> { DayOfWeek.Sunday }, false, new HashSet<DateOnly> { new(2026, 12, 29) });
        var settings = new ProjectSettings { EffortUnit = EffortUnit.Days, HoursPerDay = 7, Calendar = calendar, NewTaskKind = TaskKind.Task };

        var parsed = ProjectSettings.Parse(settings.ToJson());

        Assert.Equal(EffortUnit.Days, parsed.EffortUnit);
        Assert.Equal(7, parsed.HoursPerDay);
        Assert.Equal(TaskKind.Task, parsed.NewTaskKind);
        Assert.Equal(calendar.ToString(), parsed.Calendar!.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ broken")]
    [InlineData("[1, 2]")]
    public void Unreadable_settings_are_empty(string? json)
    {
        Assert.True(ProjectSettings.Parse(json).IsEmpty);
    }

    [Fact]
    public void Readme_keeps_the_users_text_and_replaces_the_note()
    {
        var readme = "# チームの README\n\n説明です。";

        var first = ProjectReadme.Write(readme, new ProjectSettings { HoursPerDay = 7 });
        var second = ProjectReadme.Write(first, new ProjectSettings { HoursPerDay = 6 });

        Assert.StartsWith(readme, second, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(second, "tasklabe:settings"));
        Assert.Equal(6, ProjectReadme.Read(second).HoursPerDay);
    }

    [Fact]
    public void Empty_settings_remove_the_note()
    {
        var readme = ProjectReadme.Write("本文", new ProjectSettings { HoursPerDay = 7 });

        Assert.Equal("本文", ProjectReadme.Write(readme, ProjectSettings.None));
        Assert.True(ProjectReadme.Read("本文").IsEmpty);
    }

    [Fact]
    public void Clearing_the_only_note_leaves_an_empty_note()
    {
        // GitHub は空の README への更新を受け付けないため、空の注記を残す
        var readme = ProjectReadme.Write(null, new ProjectSettings { HoursPerDay = 7 });

        var cleared = ProjectReadme.Write(readme, ProjectSettings.None);

        Assert.NotEqual("", cleared);
        Assert.True(ProjectReadme.Read(cleared).IsEmpty);
    }

    [Fact]
    public void Defaults_round_trip_with_statuses()
    {
        var defaults = DefaultSettings.Personal with
        {
            EffortUnit = EffortUnit.Days,
            Statuses = [new("未着手", "GRAY", StatusCategory.Todo), new("作業中", "YELLOW", StatusCategory.InProgress), new("完了", "GREEN", StatusCategory.Done)],
        };

        var parsed = DefaultSettings.Parse(defaults.ToJson(), DefaultSettings.Personal);

        Assert.Equal(EffortUnit.Days, parsed.EffortUnit);
        Assert.Equal(["未着手", "作業中", "完了"], parsed.Statuses.Select(s => s.Name));
        Assert.Equal(StatusCategory.InProgress, parsed.Statuses[1].Category);
    }

    [Fact]
    public void Saved_four_category_templates_split_backlog_and_pending_by_name()
    {
        var defaults = DefaultSettings.Personal with
        {
            Statuses = [new("Backlog", "GRAY", StatusCategory.Backlog), new("Todo", "BLUE", StatusCategory.Todo),
                new("Pending", "ORANGE", StatusCategory.Pending), new("Review", "PURPLE", StatusCategory.InProgress), new("Done", "GREEN", StatusCategory.Done)],
        };

        // カテゴリが 4 つだったときは、Backlog を todo、Pending を doing として保存していた
        var json = defaults.ToJson().Replace("\"backlog\"", "\"todo\"").Replace("\"pending\"", "\"doing\"").Replace("\"inprogress\"", "\"doing\"");
        var parsed = DefaultSettings.Parse(json, DefaultSettings.Personal);

        Assert.Equal([StatusCategory.Backlog, StatusCategory.Todo, StatusCategory.Pending, StatusCategory.InProgress, StatusCategory.Done],
            parsed.Statuses.Select(s => s.Category));
    }

    [Fact]
    public void Unusable_status_templates_fall_back_to_the_standard()
    {
        // 完了のない並びは使えない
        var json = DefaultSettings.Personal with { Statuses = [new("Todo", "GRAY", StatusCategory.Todo)] };

        var parsed = DefaultSettings.Parse(json.ToJson(), DefaultSettings.Personal);

        Assert.Equal(DefaultSettings.StandardStatuses, parsed.Statuses);
    }

    [Theory]
    [InlineData("", "名前が空")]
    [InlineData("Todo", "同じ名前")]
    public void Status_templates_report_why_they_cannot_be_used(string secondName, string reason)
    {
        IReadOnlyList<StatusTemplate> statuses = [new("Todo", "GRAY", StatusCategory.Todo), new(secondName, "GRAY", StatusCategory.Todo), new("Done", "GREEN", StatusCategory.Done)];

        Assert.Contains(reason, StatusTemplates.Problem(statuses), StringComparison.Ordinal);
    }

    [Fact]
    public void Effort_is_formatted_in_the_projects_unit()
    {
        var days = new ResolvedSettings(EffortUnit.Days, 7.5, WorkCalendarRules.Standard, TaskKind.Task);

        Assert.Equal("2 人日", days.FormatEffort(15));
        Assert.Equal("12.5h", (days with { EffortUnit = EffortUnit.Hours }).FormatEffort(12.5));
    }

    [Fact]
    public void Category_marker_is_removed_but_the_users_note_is_kept()
    {
        Assert.Equal("レビュー待ち", ProjectConventions.StripCategoryMarker("[tasklabe:doing] レビュー待ち"));
        Assert.Equal("", ProjectConventions.StripCategoryMarker(null));
    }

    private static int CountOf(string text, string part) =>
        (text.Length - text.Replace(part, "", StringComparison.Ordinal).Length) / part.Length;
}
