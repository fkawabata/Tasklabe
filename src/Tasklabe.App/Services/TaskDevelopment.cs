using Tasklabe.Core.Domain;
using Tasklabe.Core.Editing;
using Tasklabe.GitHub;

namespace Tasklabe.App.Services;

/// <summary>
/// タスクの開発の段階（要件 F-UI-GT-12、技術設計書 3.2.3.2 節）。Issue ごとにアプリの実行中だけ覚え、SQLite には持たない。
/// </summary>
public static class TaskDevelopment
{
    private static readonly Dictionary<string, DevelopmentStatus> s_byIssue = new(StringComparer.Ordinal);

    /// <summary>覚えている段階。まだ取っていなければ null。</summary>
    public static DevelopmentStatus? Cached(string issueId) => s_byIssue.GetValueOrDefault(issueId);

    /// <summary>
    /// タスクのブランチと PR を GitHub から取り直して覚える。取れなければ覚えている結果を残し、false を返す。
    /// </summary>
    public static async Task<bool> FetchAsync(IReadOnlyList<TaskItem> tasks)
    {
        var remote = tasks.Where(t => !t.IsLocal).ToList();
        if (remote.Count == 0)
        {
            return true;
        }

        try
        {
            var api = App.Current.Services.Api;
            var issues = await api.GetIssueDevelopmentAsync([.. remote.Select(t => t.IssueId)]);
            var chosen = remote.SelectMany(t => t.Branches).Select(TaskValues.SplitBranchEntry).ToList();
            var byBranch = chosen.Count > 0 ? await api.GetBranchPullRequestsAsync(chosen) : new Dictionary<(string, string), IReadOnlyList<PullRequestInfo>>();

            foreach (var task in remote)
            {
                var development = issues.GetValueOrDefault(task.IssueId);
                if (development is not null)
                {
                    LinkedBranches.Store(task.IssueId, development.Branches);
                }

                var branches = (development?.Branches ?? []).Select(b => new BranchInfo(b.RepositoryNameWithOwner, b.Name, b.Url ?? "")).ToList();
                var pullRequests = (development?.PullRequests ?? []).ToList();
                foreach (var (repository, branch) in task.Branches.Select(TaskValues.SplitBranchEntry))
                {
                    branches.Add(new BranchInfo(repository, branch, api.BranchUrl(repository, branch)));
                    pullRequests.AddRange(byBranch.GetValueOrDefault((repository, branch)) ?? []);
                }

                s_byIssue[task.IssueId] = DevelopmentStatus.Of(pullRequests, branches);
            }

            return true;
        }
        catch (Exception ex) when (ex is GitHubException or HttpRequestException)
        {
            AppLog.Error("タスクの開発の段階の取得", ex);
            return false;
        }
    }
}
