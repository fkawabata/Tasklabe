using Tasklabe.Core.Domain;

namespace Tasklabe.App.Controls;

/// <summary>
/// 作成中のタスク（UX 規約 UX-09、UX-12）。追加のダイアログで入力を始め、「詳細を入力…」で詳細パネルへ引き継ぐ。
/// 「追加」を押すまではタスクを作らない。
/// </summary>
public sealed class TaskDraft
{
    // XAML の型情報生成に載せないため internal にする（Project は required メンバーを持つ）
    internal Project? Project { get; set; }

    public string Title { get; set; } = "";

    public string Body { get; set; } = "";

    public string? StatusOptionId { get; set; }

    public List<string> Assignees { get; } = [];

    /// <summary>期日（予定終了日）。</summary>
    public DateOnly? Target { get; set; }

    public DateOnly? Start { get; set; }

    public double? EstimateHours { get; set; }

    public TaskKind Kind { get; set; } = TaskKind.Task;

    /// <summary>計画の中の置き場所（親タスクの Issue ID）。</summary>
    public string? ParentIssueId { get; set; }

    /// <summary>引き継いだときにフォーカスを置く項目（ダイアログで最後にフォーカスがあった項目）。</summary>
    public string? FocusField { get; set; }

    /// <summary>何か入力したか（閉じるときに破棄を確かめる）。</summary>
    public bool HasContent => Title.Trim().Length > 0 || Body.Trim().Length > 0;

    public bool CanCreate => Title.Trim().Length > 0 && Project is not null;

    /// <summary>下書きからタスクを作る。</summary>
    public Task<TaskItem> CreateAsync() => App.Current.Services.Edits.CreateAsync(
        Project!,
        Title.Trim(),
        Target,
        parentIssueId: ParentIssueId,
        kind: Kind,
        body: Body.Trim().Length > 0 ? Body.Trim() : null,
        statusOptionId: StatusOptionId,
        assignees: Assignees,
        estimateHours: EstimateHours,
        start: Start);
}
