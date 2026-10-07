using Tasklabe.Core.Domain;

namespace Tasklabe.App.Controls;

/// <summary>
/// タスクを作り始めた場所の文脈（UX 規約 UX-12）。追加のダイアログは、ここから分かる値を既定値として入れておく。
/// </summary>
public sealed record NewTaskContext
{
    /// <summary>追加先（開いている画面のプロジェクト）。null なら既定の個人プロジェクト（要件 F-TSK-02、F-UI-PR-02）。</summary>
    public string? ProjectId { get; init; }

    /// <summary>追加先のプロジェクトでのステータス（カンバンの列から追加したとき）。</summary>
    public Func<Project, StatusOption?>? Status { get; init; }

    /// <summary>区分（課題の一覧から追加したときは課題）。</summary>
    public TaskKind? Kind { get; init; }

    /// <summary>担当者（担当者で絞り込んでいる画面や、担当者ごとの区切りから追加したとき）。</summary>
    public string? Assignee { get; init; }

    /// <summary>計画の中の置き場所（親タスクごとの区切りから追加したとき）。</summary>
    public string? ParentIssueId { get; init; }

    /// <summary>期日。</summary>
    public DateOnly? Due { get; init; }

    /// <summary>作ったタスクが、いまの表示の条件に合うか。合わなければ、作成の後に知らせる。</summary>
    public Func<TaskItem, bool>? IsShown { get; init; }

    /// <summary>表示の条件を解く（「表示する」を押したとき）。</summary>
    public Action? ShowAll { get; init; }
}
