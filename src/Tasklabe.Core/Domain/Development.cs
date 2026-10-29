namespace Tasklabe.Core.Domain;

/// <summary>
/// タスクの開発の段階（要件 F-UI-GT-12）。値の大きいものほど手が要り、PR が複数あるときは最も大きいものをタスクの段階とする。
/// </summary>
public enum DevelopmentStage
{
    /// <summary>ブランチも、数える PR もない。</summary>
    None,

    Merged,
    Approved,

    /// <summary>ブランチだけがあり、PR がない。</summary>
    Working,

    Draft,
    InReview,

    /// <summary>未解決のレビューの指摘がある。</summary>
    Commented,
}

public enum PullRequestState
{
    Open,
    Closed,
    Merged,
}

/// <summary>タスクに紐づく PR。</summary>
/// <param name="UnresolvedThreads">未解決のレビューの指摘の件数。</param>
public sealed record PullRequestInfo(string Id, string RepositoryNameWithOwner, int Number, string Title, string Url,
    PullRequestState State, bool IsDraft, bool IsApproved, int UnresolvedThreads)
{
    /// <summary>この PR の段階。閉じただけでマージしていない PR は数えないため None。</summary>
    public DevelopmentStage Stage => State switch
    {
        PullRequestState.Merged => DevelopmentStage.Merged,
        PullRequestState.Closed => DevelopmentStage.None,
        _ when UnresolvedThreads > 0 => DevelopmentStage.Commented,
        _ when IsDraft => DevelopmentStage.Draft,
        _ when IsApproved => DevelopmentStage.Approved,
        _ => DevelopmentStage.InReview,
    };
}

/// <summary>タスクのブランチ（Issue の Development に紐づくものと、選んだもの）。</summary>
public sealed record BranchInfo(string RepositoryNameWithOwner, string Name, string Url);

/// <summary>タスクのブランチと PR から求めた、開発の進み具合（要件 F-UI-GT-12）。</summary>
public sealed record DevelopmentStatus(DevelopmentStage Stage, IReadOnlyList<PullRequestInfo> PullRequests, IReadOnlyList<BranchInfo> Branches)
{
    public static readonly DevelopmentStatus Empty = new(DevelopmentStage.None, [], []);

    /// <summary>示す段階の PR。段階が作業中・なしなら null。</summary>
    public PullRequestInfo? Lead => PullRequests.FirstOrDefault(p => p.Stage == Stage);

    /// <summary>未解決のレビューの指摘の件数（指摘ありの PR を合わせたもの）。</summary>
    public int UnresolvedThreads => PullRequests.Where(p => p.Stage == DevelopmentStage.Commented).Sum(p => p.UnresolvedThreads);

    /// <summary>
    /// PR を手が要る順に並べ、最初の段階をタスクの段階とする。閉じただけの PR は除き、同じ PR は 1 つにまとめる。
    /// 数える PR がなく、ブランチがあれば作業中とする。
    /// </summary>
    public static DevelopmentStatus Of(IEnumerable<PullRequestInfo> pullRequests, IEnumerable<BranchInfo> branches)
    {
        var counted = pullRequests
            .Where(p => p.Stage != DevelopmentStage.None)
            .DistinctBy(p => p.Id)
            .OrderByDescending(p => p.Stage)
            .ThenByDescending(p => p.UnresolvedThreads)
            .ThenBy(p => p.Number)
            .ToList();
        var branchList = branches.DistinctBy(b => (b.RepositoryNameWithOwner.ToUpperInvariant(), b.Name)).ToList();
        var stage = counted.Count > 0 ? counted[0].Stage
            : branchList.Count > 0 ? DevelopmentStage.Working
            : DevelopmentStage.None;
        return stage == DevelopmentStage.None ? Empty : new DevelopmentStatus(stage, counted, branchList);
    }
}
