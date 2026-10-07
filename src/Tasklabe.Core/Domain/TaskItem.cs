namespace Tasklabe.Core.Domain;

public enum TaskKind
{
    /// <summary>計画のタスク。</summary>
    Task = 0,

    /// <summary>まだ計画に組み込んでいない課題（要件 F-TSK-08）。</summary>
    Issue = 2,
}

/// <summary>タスク。GitHub Project のアイテム（Issue）に対応する。</summary>
public sealed record TaskItem
{
    /// <summary>Project アイテムの node ID。</summary>
    public required string ItemId { get; init; }

    public required string ProjectId { get; init; }

    /// <summary>Issue の node ID。</summary>
    public required string IssueId { get; init; }

    public required string RepositoryNameWithOwner { get; init; }

    public required int Number { get; init; }

    public required string Title { get; init; }

    public string? Url { get; init; }

    /// <summary>Issue の本文（Markdown）。</summary>
    public string? Body { get; init; }

    public bool IsClosed { get; init; }

    public string? StatusOptionId { get; init; }

    public string? StatusName { get; init; }

    public StatusCategory Category { get; init; }

    public TaskKind Kind { get; init; }

    /// <summary>予定開始日。</summary>
    public DateOnly? Start { get; init; }

    /// <summary>予定終了日。</summary>
    public DateOnly? Target { get; init; }

    public DateOnly? ActualStart { get; init; }

    public DateOnly? ActualEnd { get; init; }

    /// <summary>想定工数（h）。</summary>
    public double? EstimateHours { get; init; }

    /// <summary>申告された進捗率（0〜100）。</summary>
    public double ProgressPercent { get; init; }

    /// <summary>親タスクの Issue の node ID（Sub-issue の親）。</summary>
    public string? ParentIssueId { get; init; }

    /// <summary>兄弟の間での並び順（小さいほど前）。Project 内のアイテムの並び順から求める。</summary>
    public double SortOrder { get; init; }

    public IReadOnlyList<string> Assignees { get; init; } = [];

    /// <summary>作業するリポジトリ（owner/name。要件 F-TSK-18）。Issue を置くリポジトリとは別に、いくつでも持てる。</summary>
    public IReadOnlyList<string> Repositories { get; init; } = [];

    /// <summary>
    /// 選んだ既存のブランチ（owner/name:ブランチ名。要件 F-TSK-19）。GitHub の API は既存のブランチを Issue に紐づけられないため、
    /// Issue の Development とは別に持つ。
    /// </summary>
    public IReadOnlyList<string> Branches { get; init; } = [];

    /// <summary>所属するマイルストーンの ID（Issue の Milestone。要件 F-MS-02）。</summary>
    public string? MilestoneId { get; init; }

    /// <summary>先行タスク（このタスクをブロックしている Issue）の node ID。</summary>
    public IReadOnlyList<string> BlockedBy { get; init; } = [];

    /// <summary>後続の開始を待たせないタスクか（要件 F-DEP-07）。親の終わりの計算と、自分から後続への制約から外す。</summary>
    public bool NonBlocking { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>GitHub へ未作成（送信待ち）のタスクか。</summary>
    public bool IsLocal => ItemId.StartsWith(LocalIdPrefix, StringComparison.Ordinal);

    public const string LocalIdPrefix = "local:";

    /// <summary>もう手を動かさないタスクか（完了または中止）。一覧の「未完了」や遅れの判定から外す。</summary>
    public bool IsDone => Category is StatusCategory.Done or StatusCategory.Canceled || IsClosed;

    /// <summary>中止したタスクか。進捗・工数の集計と依存関係の制約から外す（要件 F-STS-06）。</summary>
    public bool IsCanceled => Category == StatusCategory.Canceled;

    /// <summary>やり遂げたタスクか（中止を含まない）。</summary>
    public bool IsCompleted => IsDone && !IsCanceled;

    /// <summary>未完了で、予定終了日を過ぎているか（一覧・計画の表・カンバンで期限切れとして示す）。</summary>
    public bool IsOverdueOn(DateOnly today) => !IsDone && Target < today;

    /// <summary>計画（WBS・線表・進捗）の対象か。課題は含めない。</summary>
    public bool IsPlanned => Kind != TaskKind.Issue;

    /// <summary>完了を 100 % として扱った進捗率。</summary>
    public double EffectiveProgress => IsCompleted ? 100 : Math.Clamp(ProgressPercent, 0, 100);

    /// <summary>残工数（h）。未見積りの場合は null。</summary>
    public double? RemainingHours => EstimateHours is { } h ? (IsCanceled ? 0 : h * (100 - EffectiveProgress) / 100) : null;
}
