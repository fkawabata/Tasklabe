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
    public void Suggestion_avoids_keys_already_taken()
    {
        Assert.Equal("TAS2", ProjectKey.Suggest("Tasklabe", null, ["TAS"]));
        Assert.Equal("TAS3", ProjectKey.Suggest("Tasklabe", null, ["TAS", "TAS2"]));
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
        Assert.Equal("TAS2", keys["b"]);
        Assert.Equal(ProjectKey.InboxKey, keys["c"]);
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
}
