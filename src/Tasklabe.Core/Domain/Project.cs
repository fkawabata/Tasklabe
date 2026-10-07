namespace Tasklabe.Core.Domain;

public enum ProjectKind
{
    Personal,
    Team,
    Inbox,
}

/// <summary>アプリのプロジェクト。GitHub Project と 1 対 1 に対応する。</summary>
public sealed record Project
{
    /// <summary>GitHub Project の node ID。</summary>
    public required string Id { get; init; }

    public required int Number { get; init; }

    public required string Title { get; init; }

    public required ProjectKind Kind { get; init; }

    public required string OwnerLogin { get; init; }

    public string? Url { get; init; }

    /// <summary>タスクを作成するリポジトリ（Project にリンクしたリポジトリ）。</summary>
    public string? RepositoryId { get; init; }

    public string? RepositoryNameWithOwner { get; init; }

    public bool Closed { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public IReadOnlyList<StatusOption> StatusOptions { get; init; } = [];

    /// <summary>マイルストーン（つないだリポジトリの Milestone。要件 F-MS-01）。</summary>
    public IReadOnlyList<Milestone> Milestones { get; init; } = [];

    /// <summary>このプロジェクトで既定を上書きした設定（要件 F-SET-02）。Project の README に保存する。</summary>
    public Tasklabe.Core.Settings.ProjectSettings Settings { get; init; } = Tasklabe.Core.Settings.ProjectSettings.None;

    /// <summary>フィールド名から GitHub のフィールド ID への対応。</summary>
    public IReadOnlyDictionary<string, string> FieldIds { get; init; } = new Dictionary<string, string>();

    /// <summary>種別（Kind）の選択肢名から選択肢 ID への対応。</summary>
    public IReadOnlyDictionary<string, string> KindOptionIds { get; init; } = new Dictionary<string, string>();

    public bool IsTeam => Kind == ProjectKind.Team;
}

public sealed record StatusOption(string Id, string Name, string Color, StatusCategory Category);
