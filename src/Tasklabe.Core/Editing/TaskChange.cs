using System.Globalization;
using Tasklabe.Core.Domain;

namespace Tasklabe.Core.Editing;

/// <summary>編集できるタスクの項目。</summary>
public enum TaskField
{
    Title,
    Body,
    Status,
    Start,
    Target,
    ActualStart,
    ActualEnd,
    Estimate,
    Progress,
    Kind,

    /// <summary>Issue の開閉状態（OPEN / CLOSED）。</summary>
    State,

    /// <summary>担当者（ログイン名をカンマで区切り、昇順に並べたもの）。</summary>
    Assignees,

    /// <summary>親タスクの Issue の node ID（null は最上位）。</summary>
    Parent,

    /// <summary>兄弟の間での並び順（数値）。</summary>
    SortOrder,

    /// <summary>先行タスクの Issue の node ID（カンマで区切り、昇順に並べたもの）。</summary>
    BlockedBy,

    /// <summary>日程の扱い（"Non-blocking" または null）。</summary>
    Schedule,

    /// <summary>作業するリポジトリ（owner/name をカンマで区切り、昇順に並べたもの）。</summary>
    Repositories,

    /// <summary>選んだ既存のブランチ（owner/name:ブランチ名 を空白で区切り、昇順に並べたもの）。</summary>
    Branches,

    /// <summary>所属するマイルストーンの ID（null はどこにも入っていない）。</summary>
    Milestone,
}

/// <summary>
/// タスクの 1 項目の変更。値は文字列で表す（日付は yyyy-MM-dd、数値は不変カルチャ、
/// ステータスは選択肢の ID、種別は Task / Milestone、開閉状態は OPEN / CLOSED、
/// 担当者はログイン名のカンマ区切り、先行タスクは Issue の node ID のカンマ区切り、null は未設定）。
/// </summary>
public sealed record TaskChange(TaskField Field, string? OldValue, string? NewValue);

public static class TaskValues
{
    public const string Open = "OPEN";
    public const string Closed = "CLOSED";

    /// <summary>「対応しない」として Close した状態（中止）。</summary>
    public const string ClosedNotPlanned = "CLOSED_NOT_PLANNED";

    public static string? Date(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateOnly? ParseDate(string? value) =>
        value is not null && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    public static string? Number(double? value) => value?.ToString("G", CultureInfo.InvariantCulture);

    public static double? ParseNumber(string? value) =>
        value is not null && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>担当者の一覧を、比較できる文字列表現にする（未割り当ては null）。</summary>
    public static string? Logins(IEnumerable<string> logins)
    {
        var sorted = logins.Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        return sorted.Count == 0 ? null : string.Join(',', sorted);
    }

    public static IReadOnlyList<string> ParseLogins(string? value) =>
        value is null ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Issue の node ID の一覧を、比較できる文字列表現にする（空は null）。</summary>
    public static string? IssueIds(IEnumerable<string> ids)
    {
        var sorted = ids.Where(i => i.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return sorted.Count == 0 ? null : string.Join(',', sorted);
    }

    public static IReadOnlyList<string> ParseIssueIds(string? value) =>
        value is null ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>リポジトリ（owner/name）の一覧を、比較できる文字列表現にする（空は null）。大文字と小文字は区別しない。</summary>
    public static string? Repositories(IEnumerable<string> repositories)
    {
        var sorted = repositories.Select(r => r.Trim()).Where(r => r.Contains('/', StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return sorted.Count == 0 ? null : string.Join(',', sorted);
    }

    /// <summary>
    /// ブランチ（owner/name:ブランチ名）の一覧を、比較できる文字列表現にする（空は null）。
    /// 区切りには、Git のブランチ名に使えない空白とコロンを使う。
    /// </summary>
    public static string? Branches(IEnumerable<string> branches)
    {
        var sorted = branches.Select(b => b.Trim()).Where(IsBranchEntry)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return sorted.Count == 0 ? null : string.Join(' ', sorted);
    }

    public static IReadOnlyList<string> ParseBranches(string? value) =>
        value is null ? [] : Branches(value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))?.Split(' ') ?? [];

    /// <summary>リポジトリとブランチ名から、保存する形（owner/name:ブランチ名）を作る。</summary>
    public static string BranchEntry(string repository, string branch) => $"{repository}:{branch}";

    /// <summary>保存した形を、リポジトリとブランチ名に分ける。</summary>
    public static (string Repository, string Branch) SplitBranchEntry(string entry)
    {
        int i = entry.IndexOf(':', StringComparison.Ordinal);
        return (entry[..i], entry[(i + 1)..]);
    }

    private static bool IsBranchEntry(string entry) =>
        entry.IndexOf(':', StringComparison.Ordinal) is var i and > 0 && i < entry.Length - 1 && entry[..i].Contains('/', StringComparison.Ordinal);

    /// <summary>保存したテキストからリポジトリの一覧を読む。GitHub で手で書いた空白や改行の区切りも受け付ける。</summary>
    public static IReadOnlyList<string> ParseRepositories(string? value) =>
        value is null ? [] : Repositories(value.Split([',', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))?.Split(',') ?? [];

    /// <summary>タスクの現在値を文字列で取り出す。</summary>
    public static string? Get(TaskItem task, TaskField field) => field switch
    {
        TaskField.Title => task.Title,
        TaskField.Body => task.Body,
        TaskField.Status => task.StatusOptionId,
        TaskField.Start => Date(task.Start),
        TaskField.Target => Date(task.Target),
        TaskField.ActualStart => Date(task.ActualStart),
        TaskField.ActualEnd => Date(task.ActualEnd),
        TaskField.Estimate => Number(task.EstimateHours),
        TaskField.Progress => Number(task.ProgressPercent),
        TaskField.Kind => ProjectConventions.KindOption(task.Kind),
        TaskField.State => !task.IsClosed ? Open : task.IsCanceled ? ClosedNotPlanned : Closed,
        TaskField.Assignees => Logins(task.Assignees),
        TaskField.Parent => task.ParentIssueId,
        TaskField.SortOrder => Number(task.SortOrder),
        TaskField.BlockedBy => IssueIds(task.BlockedBy),
        TaskField.Schedule => task.NonBlocking ? ProjectConventions.ScheduleOptions.NonBlocking : null,
        TaskField.Repositories => Repositories(task.Repositories),
        TaskField.Branches => Branches(task.Branches),
        TaskField.Milestone => task.MilestoneId,
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    /// <summary>変更をタスクに適用した結果を返す。</summary>
    public static TaskItem Apply(TaskItem task, TaskChange change, IReadOnlyList<StatusOption> statusOptions)
    {
        var v = change.NewValue;
        var updated = change.Field switch
        {
            TaskField.Title => task with { Title = v ?? "" },
            TaskField.Body => task with { Body = v },
            TaskField.Status => ApplyStatus(task, v, statusOptions),
            TaskField.Start => task with { Start = ParseDate(v) },
            TaskField.Target => task with { Target = ParseDate(v) },
            TaskField.ActualStart => task with { ActualStart = ParseDate(v) },
            TaskField.ActualEnd => task with { ActualEnd = ParseDate(v) },
            TaskField.Estimate => task with { EstimateHours = ParseNumber(v) },
            TaskField.Progress => task with { ProgressPercent = ParseNumber(v) ?? 0 },
            TaskField.Kind => task with { Kind = ProjectConventions.ParseKind(v, ProjectKind.Personal) },
            TaskField.State => task with { IsClosed = v is Closed or ClosedNotPlanned },
            TaskField.Assignees => task with { Assignees = ParseLogins(v) },
            TaskField.Parent => task with { ParentIssueId = v },
            TaskField.SortOrder => task with { SortOrder = ParseNumber(v) ?? task.SortOrder },
            TaskField.BlockedBy => task with { BlockedBy = ParseIssueIds(v) },
            TaskField.Schedule => task with { NonBlocking = v == ProjectConventions.ScheduleOptions.NonBlocking },
            TaskField.Repositories => task with { Repositories = ParseRepositories(v) },
            TaskField.Branches => task with { Branches = ParseBranches(v) },
            TaskField.Milestone => task with { MilestoneId = v },
            _ => task,
        };

        // 開閉状態とステータスの両方からカテゴリを決める（Close された Issue は、中止でなければ完了）
        var option = statusOptions.FirstOrDefault(o => o.Id == updated.StatusOptionId);
        return updated with { Category = CategoryOf(updated.IsClosed, option) };
    }

    /// <summary>開閉状態とステータスからカテゴリを決める。Close された Issue は、中止のステータスでなければ完了とする。</summary>
    public static StatusCategory CategoryOf(bool closed, StatusOption? option) =>
        closed && option?.Category != StatusCategory.Canceled ? StatusCategory.Done : option?.Category ?? StatusCategory.Todo;

    private static TaskItem ApplyStatus(TaskItem task, string? optionId, IReadOnlyList<StatusOption> options)
    {
        var option = options.FirstOrDefault(o => o.Id == optionId);
        return task with { StatusOptionId = option?.Id, StatusName = option?.Name };
    }
}
