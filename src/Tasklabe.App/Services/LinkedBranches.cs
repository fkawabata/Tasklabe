using Tasklabe.GitHub;

namespace Tasklabe.App.Services;

/// <summary>
/// Issue に紐づくブランチ（要件 F-TSK-19）。詳細パネルを開くたびに取らないよう、Issue ごとに覚える。
/// アプリで作ったブランチは、取り直さずに足す。
/// </summary>
public static class LinkedBranches
{
    private static readonly Dictionary<string, IReadOnlyList<LinkedBranch>> s_byIssue = new(StringComparer.Ordinal);

    /// <summary>覚えているブランチ。まだ取っていなければ null。</summary>
    public static IReadOnlyList<LinkedBranch>? Cached(string issueId) => s_byIssue.GetValueOrDefault(issueId);

    /// <summary>GitHub から取り直して覚える。取れなければ null。</summary>
    public static async Task<IReadOnlyList<LinkedBranch>?> FetchAsync(string issueId)
    {
        try
        {
            return s_byIssue[issueId] = await App.Current.Services.Api.GetLinkedBranchesAsync(issueId);
        }
        catch (Exception ex) when (ex is GitHubException or HttpRequestException)
        {
            AppLog.Error("Issue に紐づくブランチの取得", ex);
            return null;
        }
    }

    /// <summary>アプリで作ったブランチを足す。</summary>
    public static void Add(string issueId, LinkedBranch branch) =>
        s_byIssue[issueId] = [.. s_byIssue.GetValueOrDefault(issueId) ?? [], branch];
}
