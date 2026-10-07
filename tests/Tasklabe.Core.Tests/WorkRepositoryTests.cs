using Tasklabe.Core.Domain;
using Tasklabe.Core.Editing;

namespace Tasklabe.Core.Tests;

/// <summary>作業するリポジトリとブランチ名（要件 F-TSK-18、19）。</summary>
public class WorkRepositoryTests
{
    private static TaskItem T(params string[] repositories) => new()
    {
        ItemId = "i1",
        ProjectId = "p1",
        IssueId = "issue1",
        RepositoryNameWithOwner = "acme/tasks",
        Number = 33,
        Title = "ログイン",
        Repositories = repositories,
    };

    [Fact]
    public void Repositories_are_compared_regardless_of_order_and_case()
    {
        Assert.Equal("acme/api,acme/Web", TaskValues.Repositories(["acme/Web", "acme/api", "ACME/web"]));
        Assert.Null(TaskValues.Repositories([]));
        Assert.Null(TaskValues.Repositories(["not-a-repository"]));
    }

    [Fact]
    public void Repositories_written_by_hand_on_GitHub_are_read()
    {
        Assert.Equal(["acme/api", "acme/web"], TaskValues.ParseRepositories("acme/web, acme/api\nacme/web"));
        Assert.Empty(TaskValues.ParseRepositories(null));
    }

    [Fact]
    public void Repositories_change_round_trips_through_the_task()
    {
        var task = T("acme/web");
        var updated = TaskValues.Apply(task, new TaskChange(TaskField.Repositories, TaskValues.Get(task, TaskField.Repositories), "acme/api,acme/web"), []);

        Assert.Equal(["acme/api", "acme/web"], updated.Repositories);
        Assert.Equal("acme/web", TaskValues.Get(task, TaskField.Repositories));
    }

    [Fact]
    public void Chosen_branches_keep_branch_names_with_slashes_and_commas()
    {
        var entries = new[] { TaskValues.BranchEntry("acme/web", "feature/TAS-33,login"), TaskValues.BranchEntry("acme/api", "TAS-33") };

        var value = TaskValues.Branches(entries);

        Assert.Equal("acme/api:TAS-33 acme/web:feature/TAS-33,login", value);
        Assert.Equal(["acme/api:TAS-33", "acme/web:feature/TAS-33,login"], TaskValues.ParseBranches(value));
        Assert.Equal(("acme/web", "feature/TAS-33,login"), TaskValues.SplitBranchEntry(TaskValues.ParseBranches(value)[1]));
    }

    [Fact]
    public void Malformed_branch_entries_are_ignored()
    {
        Assert.Empty(TaskValues.ParseBranches("no-repository acme/web: :main"));
        Assert.Null(TaskValues.Branches([]));
    }

    [Theory]
    [InlineData("TAS-33", "Login form", "TAS-33-login-form")]
    [InlineData("TAS-33", "ログイン画面の Bug 修正", "TAS-33-bug")]
    [InlineData("TAS-33", "ログイン", "TAS-33")]
    [InlineData("", "Add OAuth2 support!", "add-oauth2-support")]
    [InlineData("", "ログイン", "task")]
    public void Branch_name_starts_with_the_task_key(string key, string title, string expected)
    {
        Assert.Equal(expected, BranchName.Suggest(key, title));
    }

    [Fact]
    public void Long_titles_are_cut_without_a_trailing_hyphen()
    {
        var name = BranchName.Suggest("TAS-1", string.Join(' ', Enumerable.Repeat("word", 20)));

        Assert.True(name.Length <= "TAS-1-".Length + 40);
        Assert.False(name.EndsWith('-'));
    }

    [Theory]
    [InlineData("TAS-33-login", true)]
    [InlineData("feature/TAS-33", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("-start", false)]
    [InlineData("a..b", false)]
    [InlineData("end.lock", false)]
    [InlineData("end/", false)]
    [InlineData("a/.hidden", false)]
    [InlineData("what?", false)]
    public void Branch_name_follows_git_rules(string name, bool valid)
    {
        Assert.Equal(valid, BranchName.Problem(name) is null);
    }
}
