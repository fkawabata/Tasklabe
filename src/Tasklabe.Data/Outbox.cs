using System.Text.Json.Serialization;
using Tasklabe.Core.Editing;

namespace Tasklabe.Data;

public enum OutboxKind
{
    /// <summary>Issue を作成し、Project に追加する。</summary>
    Create,

    /// <summary>1 項目を変更する。</summary>
    Field,

    /// <summary>Issue を削除する。</summary>
    Delete,

    /// <summary>別の Project へ移す（OldValue に移動元の Project ID）。</summary>
    Move,
}

/// <summary>送信キューの 1 件（技術設計書 3.1 節）。</summary>
public sealed record OutboxEntry(
    long Id,
    OutboxKind Kind,
    string ProjectId,
    string ItemId,
    string? IssueId,
    TaskField? Field,
    string? OldValue,
    string? NewValue,
    string? Payload,
    int Attempts,
    string? LastError,
    bool Failed,
    DateTimeOffset CreatedAt);

/// <summary>送信キューの Payload に入れる印。</summary>
public static class OutboxPayloads
{
    /// <summary>
    /// 作ったばかりの Project のアイテム（作成、別のプロジェクトへの移動）に書く初めの値。
    /// まだ誰も値を変えていないため、送信の前に GitHub 上の値が違っていても競合としない
    /// （GitHub の「Item added to project」のワークフローが、追加の直後に Status を書くことがある）。
    /// </summary>
    public const string InitialValue = "initial";
}

/// <summary>作成待ちのタスクの内容（Create の Payload）。</summary>
/// <param name="CreatedIssueId">送信の途中で作れた Issue（再送のときに作り直さないため）。</param>
public sealed record NewTaskPayload(string Title, string? Body, string RepositoryNameWithOwner,
    string? CreatedIssueId = null, int CreatedNumber = 0, string? CreatedUrl = null);

/// <summary>移動の途中経過（Move の Payload）。転送・複製まで済んだ Issue を記録し、再送では続きから行う。</summary>
public sealed record MoveProgress(string IssueId, int Number, string? Url, string? OriginalIssueId);

/// <summary>他の利用者の変更を上書きしたことの知らせ（要件 F-SYNC-04）。</summary>
public sealed record TaskConflict(
    string ItemId, string TaskTitle, TaskField Field, string? LocalValue, string? RemoteValue, DateTimeOffset DetectedAt);

[JsonSerializable(typeof(NewTaskPayload))]
[JsonSerializable(typeof(MoveProgress))]
internal sealed partial class OutboxJsonContext : JsonSerializerContext;
