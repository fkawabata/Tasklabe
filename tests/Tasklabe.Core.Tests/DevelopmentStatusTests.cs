using Tasklabe.Core.Domain;

namespace Tasklabe.Core.Tests;

/// <summary>ブランチと PR から求める開発の段階（要件 F-UI-GT-12）。</summary>
public class DevelopmentStatusTests
{
    private static readonly BranchInfo Branch = new("acme/web", "TAS-33-login", "https://github.com/acme/web/tree/TAS-33-login");

    private static PullRequestInfo Pr(int number, PullRequestState state = PullRequestState.Open, bool draft = false, bool approved = false, int unresolved = 0) =>
        new($"pr{number}", "acme/web", number, $"PR {number}", $"https://github.com/acme/web/pull/{number}", state, draft, approved, unresolved);

    [Fact]
    public void Nothing_means_no_stage()
    {
        Assert.Equal(DevelopmentStage.None, DevelopmentStatus.Of([], []).Stage);
    }

    [Fact]
    public void A_branch_without_a_pull_request_is_working()
    {
        Assert.Equal(DevelopmentStage.Working, DevelopmentStatus.Of([], [Branch]).Stage);
    }

    [Theory]
    [InlineData(PullRequestState.Merged, false, false, 0, DevelopmentStage.Merged)]
    [InlineData(PullRequestState.Open, false, false, 0, DevelopmentStage.InReview)]
    [InlineData(PullRequestState.Open, true, false, 0, DevelopmentStage.Draft)]
    [InlineData(PullRequestState.Open, false, true, 0, DevelopmentStage.Approved)]
    [InlineData(PullRequestState.Open, false, true, 2, DevelopmentStage.Commented)]
    [InlineData(PullRequestState.Open, true, false, 1, DevelopmentStage.Commented)]
    public void Stage_of_a_single_pull_request(PullRequestState state, bool draft, bool approved, int unresolved, DevelopmentStage expected)
    {
        Assert.Equal(expected, DevelopmentStatus.Of([Pr(1, state, draft, approved, unresolved)], [Branch]).Stage);
    }

    [Fact]
    public void Closed_pull_requests_are_not_counted()
    {
        var status = DevelopmentStatus.Of([Pr(1, PullRequestState.Closed)], [Branch]);

        Assert.Equal(DevelopmentStage.Working, status.Stage);
        Assert.Empty(status.PullRequests);
        Assert.Equal(DevelopmentStage.None, DevelopmentStatus.Of([Pr(1, PullRequestState.Closed)], []).Stage);
    }

    [Fact]
    public void The_pull_request_that_needs_hands_leads()
    {
        var status = DevelopmentStatus.Of([Pr(1, PullRequestState.Merged), Pr(2, approved: true), Pr(3), Pr(4, unresolved: 3)], []);

        Assert.Equal(DevelopmentStage.Commented, status.Stage);
        Assert.Equal(4, status.Lead!.Number);
        Assert.Equal([4, 3, 2, 1], status.PullRequests.Select(p => p.Number));
        Assert.Equal(3, status.UnresolvedThreads);
    }

    [Fact]
    public void Approved_is_after_working_and_before_merged()
    {
        Assert.Equal(DevelopmentStage.Approved, DevelopmentStatus.Of([Pr(1, approved: true), Pr(2, PullRequestState.Merged)], []).Stage);
        Assert.True(DevelopmentStage.Working > DevelopmentStage.Approved);
    }

    [Fact]
    public void The_same_pull_request_found_twice_is_counted_once()
    {
        var status = DevelopmentStatus.Of([Pr(1, unresolved: 2), Pr(1, unresolved: 2)], [Branch, Branch with { RepositoryNameWithOwner = "ACME/web" }]);

        Assert.Single(status.PullRequests);
        Assert.Single(status.Branches);
        Assert.Equal(2, status.UnresolvedThreads);
    }
}
