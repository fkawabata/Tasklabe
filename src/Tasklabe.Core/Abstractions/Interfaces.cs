using Tasklabe.Core.Domain;

namespace Tasklabe.Core.Abstractions;

/// <summary>ローカルキャッシュ上のプロジェクトとタスク。UI はここからのみ読む。</summary>
public interface ITaskStore
{
    Task<IReadOnlyList<Project>> GetProjectsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TaskItem>> GetTasksAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TaskItem>> GetTasksAsync(string projectId, CancellationToken cancellationToken = default);
}

public enum SyncState
{
    Idle,
    Syncing,
    Offline,
    Error,
}

/// <summary>画面に常に示す同期の状態（要件 F-SYNC-05）。</summary>
/// <param name="Pending">送信待ちの変更の件数。</param>
/// <param name="Failed">送信に失敗し、利用者の対応を待つ変更の件数。</param>
public sealed record SyncStatus(SyncState State, DateTimeOffset? LastSyncedAt, string? Message = null, int Pending = 0, int Failed = 0);
