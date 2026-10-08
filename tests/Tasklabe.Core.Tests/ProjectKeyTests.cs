using Tasklabe.Core.Domain;
using Tasklabe.Core.Settings;

namespace Tasklabe.Core.Tests;

/// <summary>タスクの番号のキー（要件 F-TSK-14）。</summary>
public class ProjectKeyTests
{
    private static Project P(string id, string title, string? key = null, ProjectKind kind = ProjectKind.Team, string? repository = null) => new()
    {
        Id = id,
        Number = 1,
        Title = title,
        Kind = kind,
        OwnerLogin = "org",
        RepositoryNameWithOwner = repository,
        Settings = new ProjectSettings { Key = key },
    };

    [Theory]
    [InlineData("Tasklabe", null, "TAS")]
    [InlineData("Web Renewal", null, "WR")]
    [InlineData("my-cool-app", null, "MCA")]
    [InlineData("TaskLabe", null, "TL")]
    [InlineData("Go", null, "GO")]
    [InlineData("2026 Spring Release", null, "SR")]
    [InlineData("社内ポータル刷新", "acme/portal-renewal", "PR")]
    [InlineData("社内ポータル刷新", null, ProjectKey.Fallback)]
    public void Suggests_a_key_from_the_name_or_the_repository(string title, string? repository, string expected)
    {
        Assert.Equal(expected, ProjectKey.Suggest(title, repository, []));
    }

    [Fact]
    public void Suggestion_takes_more_of_the_name_before_adding_a_digit()
    {
        Assert.Equal("TASK", ProjectKey.Suggest("Tasklabe", null, ["TAS"]));
        Assert.Equal("TASKL", ProjectKey.Suggest("Tasklabe", null, ["TAS", "TASK"]));
        Assert.Equal("WER", ProjectKey.Suggest("Web Renewal", null, ["WR"]));
        Assert.Equal("GO2", ProjectKey.Suggest("Go", null, ["GO"]));
        Assert.Equal("TAS2", ProjectKey.Suggest("Tasklabe", null, ["TAS", "TASK", "TASKL", "TASKLA", "TASKLAB", "TASKLABE"]));
    }

    [Fact]
    public void Suggestion_never_uses_the_inbox_key()
    {
        Assert.Equal("MYA", ProjectKey.Suggest("My Yard App", null, []));
        Assert.Equal("MYY", ProjectKey.Suggest("My Yard", null, []));
        Assert.Equal("MYP", ProjectKey.Suggest("Myproject", null, []));
    }

    [Theory]
    [InlineData("TL", true)]
    [InlineData("WEB2", true)]
    [InlineData("ABCDEFGHIJ", true)]
    [InlineData("T", false)]
    [InlineData("ABCDEFGHIJK", false)]
    [InlineData("2WEB", false)]
    [InlineData("WEB-2", false)]
    [InlineData("tl", false)]
    public void Key_is_two_to_ten_upper_letters_and_digits_starting_with_a_letter(string key, bool valid)
    {
        Assert.Equal(valid, ProjectKey.IsValid(key));
    }

    [Fact]
    public void Problem_explains_an_invalid_or_taken_key()
    {
        Assert.Null(ProjectKey.Problem("WEB", ["TAS"]));
        Assert.NotNull(ProjectKey.Problem("W", []));
        Assert.Contains("ほかのプロジェクト", ProjectKey.Problem("TAS", ["TAS"]));
        Assert.Contains("未分類", ProjectKey.Problem(ProjectKey.InboxKey, []));
        Assert.Contains("自動リンク", ProjectKey.Problem("JIRA", [], ["JIRA"]));
    }

    [Fact]
    public void Saved_keys_are_kept_and_unsaved_projects_get_suggestions_that_do_not_collide()
    {
        var keys = ProjectKey.Resolve([
            P("b", "Tasklabe"),
            P("a", "Other", key: "TAS"),
            P("c", "Inbox", kind: ProjectKind.Inbox),
        ]);

        Assert.Equal("TAS", keys["a"]);
        Assert.Equal("TASK", keys["b"]);
        Assert.Equal(ProjectKey.InboxKey, keys["c"]);
    }

    [Fact]
    public void Inbox_key_belongs_only_to_the_inbox()
    {
        var keys = ProjectKey.Resolve([
            P("a", "My Yard", key: ProjectKey.InboxKey),
            P("b", "Inbox", key: "BOX", kind: ProjectKind.Inbox),
        ]);

        Assert.Equal("MYY", keys["a"]);
        Assert.Equal(ProjectKey.InboxKey, keys["b"]);
        Assert.Null(ProjectKey.SavedKey(P("a", "My Yard", key: ProjectKey.InboxKey)));
    }

    [Fact]
    public void Key_is_saved_with_the_project_settings_in_upper_case()
    {
        var parsed = ProjectSettings.Parse(new ProjectSettings { Key = "TLB" }.ToJson());

        Assert.Equal("TLB", parsed.Key);
        Assert.Equal("WEB", ProjectSettings.Parse("""{"key":" web "}""").Key);
        Assert.Null(ProjectSettings.Parse("""{"key":"1x"}""").Key);
        Assert.False(new ProjectSettings { Key = "TLB" }.IsEmpty);
    }

    [Fact]
    public void Number_is_the_key_and_the_issue_number()
    {
        Assert.Equal("TLB-123", ProjectKey.Format("TLB", 123));
    }

    [Fact]
    public void Outline_number_fills_the_prepared_levels_with_zero_and_adds_deeper_levels()
    {
        Assert.Equal("TLB_1", ProjectKey.FormatOutline("TLB", [1], 1));
        Assert.Equal("TLB_1_0_0", ProjectKey.FormatOutline("TLB", [1], 3));
        Assert.Equal("TLB_1_2_0", ProjectKey.FormatOutline("TLB", [1, 2], 3));
        Assert.Equal("TLB_1_2_3_4", ProjectKey.FormatOutline("TLB", [1, 2, 3, 4], 3));
    }

    [Fact]
    public void Outline_levels_are_saved_with_the_project_settings()
    {
        Assert.Equal(3, ProjectSettings.Parse(new ProjectSettings { OutlineLevels = 3 }.ToJson()).OutlineLevels);
        Assert.Null(ProjectSettings.Parse("""{"outlineLevels":0}""").OutlineLevels);
        Assert.Null(ProjectSettings.Parse("""{"outlineLevels":9}""").OutlineLevels);
        Assert.False(new ProjectSettings { OutlineLevels = 1 }.IsEmpty);
    }
}
