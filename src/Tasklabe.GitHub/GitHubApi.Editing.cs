using System.Globalization;
using System.Text.Json.Nodes;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Editing;
using Tasklabe.Core.Settings;
using Tasklabe.GitHub.GraphQL;
using F = Tasklabe.Core.Domain.ProjectConventions.Fields;

namespace Tasklabe.GitHub;

public sealed record CreatedIssue(string Id, int Number, string? Url);

public sealed partial class GitHubApi
{
    /// <summary>タスクの項目に対応する Project のフィールド名。Issue 本体の項目は null。</summary>
    public static string? FieldName(TaskField field) => field switch
    {
        TaskField.Status => F.Status,
        TaskField.Start => F.Start,
        TaskField.Target => F.Target,
        TaskField.ActualStart => F.ActualStart,
        TaskField.ActualEnd => F.ActualEnd,
        TaskField.Estimate => F.Estimate,
        TaskField.Progress => F.Progress,
        TaskField.Kind => F.Kind,
        TaskField.Schedule => F.Schedule,
        TaskField.Repositories => F.Repositories,
        TaskField.Branches => F.Branches,
        _ => null,
    };

    public async Task<CreatedIssue> CreateIssueAsync(string repositoryId, string title, string? body, CancellationToken ct = default)
    {
        var data = await _client.SendAsync(Queries.CreateIssue,
            new JsonObject { ["repositoryId"] = repositoryId, ["title"] = title, ["body"] = body },
            GitHubJsonContext.Default.GraphQLResponseCreateIssueData, ct).ConfigureAwait(false);
        var issue = data.CreateIssue?.Issue ?? throw new GitHubException("Issue を作成できませんでした。");
        return new CreatedIssue(issue.Id!, issue.Number, issue.Url);
    }

    public async Task<string> AddProjectItemAsync(string projectId, string contentId, CancellationToken ct = default)
    {
        var data = await _client.SendAsync(Queries.AddProjectItem,
            new JsonObject { ["projectId"] = projectId, ["contentId"] = contentId },
            GitHubJsonContext.Default.GraphQLResponseAddProjectItemData, ct).ConfigureAwait(false);
        return data.AddProjectV2ItemById?.Item?.Id ?? throw new GitHubException("プロジェクトにタスクを追加できませんでした。");
    }

    public Task DeleteProjectItemAsync(string projectId, string itemId, CancellationToken ct = default) =>
        MutateAsync(Queries.DeleteProjectItem, new JsonObject { ["projectId"] = projectId, ["itemId"] = itemId }, ct);

    /// <summary>Project のフィールドの値を設定する。値が null の場合は消去する。</summary>
    public Task SetFieldAsync(Project project, string itemId, TaskField field, string? value, CancellationToken ct = default)
    {
        if (field == TaskField.Schedule)
        {
            return SetScheduleAsync(project, itemId, value, ct);
        }

        if (field is TaskField.Repositories or TaskField.Branches)
        {
            return SetAddedTextAsync(project, itemId, FieldName(field)!, value, ct);
        }

        var name = FieldName(field) ?? throw new ArgumentException($"{field} は Project のフィールドではありません。", nameof(field));
        var fieldId = project.FieldIds.GetValueOrDefault(name)
            ?? throw new GitHubException($"プロジェクトにフィールド「{name}」がありません。");

        if (value is null)
        {
            return MutateAsync(Queries.ClearItemField,
                new JsonObject { ["projectId"] = project.Id, ["itemId"] = itemId, ["fieldId"] = fieldId }, ct);
        }

        JsonObject fieldValue = field switch
        {
            TaskField.Status => new JsonObject { ["singleSelectOptionId"] = value },
            TaskField.Kind => new JsonObject
            {
                ["singleSelectOptionId"] = project.KindOptionIds.GetValueOrDefault(value)
                    ?? throw new GitHubException($"種別「{value}」の選択肢がありません。"),
            },
            TaskField.Estimate or TaskField.Progress => new JsonObject
            {
                ["number"] = double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture),
            },
            _ => new JsonObject { ["date"] = value },
        };

        return MutateAsync(Queries.UpdateItemField, new JsonObject
        {
            ["projectId"] = project.Id,
            ["itemId"] = itemId,
            ["fieldId"] = fieldId,
            ["value"] = fieldValue,
        }, ct);
    }

    /// <summary>
    /// Status の選択肢を、編集した並びに置き換える（要件 F-SET-03）。既存の選択肢は ID を保つため、アイテムのステータスは変わらない。
    /// 並びから外した選択肢は GitHub 上から消え、その選択肢だったアイテムはステータスが未設定になる。
    /// </summary>
    public async Task UpdateStatusOptionsAsync(string projectId, IReadOnlyList<StatusOptionEdit> options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var data = await _client.SendAsync(Queries.ProjectSchema, new JsonObject { ["id"] = projectId },
            GitHubJsonContext.Default.GraphQLResponseProjectNodeData, ct).ConfigureAwait(false);
        var dto = data.Node ?? throw new GitHubException("プロジェクトが見つかりません。");
        var fieldId = ProjectMapper.StatusField(dto)?.Id ?? throw new GitHubException("プロジェクトに Status フィールドがありません。");
        await MutateAsync(Queries.UpdateSingleSelectOptions,
            new JsonObject { ["fieldId"] = fieldId, ["options"] = ProjectSchema.StatusOptions(dto, options) }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 日程の扱い（Schedule）を設定する。後から足したフィールドのため、Project になければ先に追加し、
    /// 選択肢の ID もその場で引く（要件 F-DEP-07）。
    /// </summary>
    private async Task SetScheduleAsync(Project project, string itemId, string? value, CancellationToken ct)
    {
        var field = await AddedFieldAsync(project.Id, "SINGLE_SELECT", F.Schedule, create: value is not null, ct).ConfigureAwait(false);

        if (field?.Id is not { } fieldId)
        {
            if (value is null)
            {
                return;
            }

            throw new GitHubException($"プロジェクトにフィールド「{F.Schedule}」を追加できませんでした。");
        }

        if (value is null)
        {
            await MutateAsync(Queries.ClearItemField,
                new JsonObject { ["projectId"] = project.Id, ["itemId"] = itemId, ["fieldId"] = fieldId }, ct).ConfigureAwait(false);
            return;
        }

        var optionId = field.Options?.FirstOrDefault(o => o.Name == value)?.Id
            ?? throw new GitHubException($"日程の扱い「{value}」の選択肢がありません。");
        await MutateAsync(Queries.UpdateItemField, new JsonObject
        {
            ["projectId"] = project.Id,
            ["itemId"] = itemId,
            ["fieldId"] = fieldId,
            ["value"] = new JsonObject { ["singleSelectOptionId"] = optionId },
        }, ct).ConfigureAwait(false);
    }

    /// <summary>GitHub を使わない担当者（<see cref="People.GuestNames"/> の形）を設定する。値が null の場合は消去する。</summary>
    public Task SetGuestAssigneesAsync(Project project, string itemId, string? names, CancellationToken ct = default) =>
        SetAddedTextAsync(project, itemId, F.GuestAssignees, names, ct);

    /// <summary>
    /// 後から足したテキストのフィールド（作業するリポジトリとブランチ、GitHub を使わない担当者。要件 F-TSK-18、19）を設定する。
    /// Project になければ先に追加する。
    /// </summary>
    private async Task SetAddedTextAsync(Project project, string itemId, string name, string? value, CancellationToken ct)
    {
        var fieldId = project.FieldIds.GetValueOrDefault(name)
            ?? (await AddedFieldAsync(project.Id, "TEXT", name, create: value is not null, ct).ConfigureAwait(false))?.Id;
        if (fieldId is null)
        {
            if (value is null)
            {
                return;
            }

            throw new GitHubException($"プロジェクトにフィールド「{name}」を追加できませんでした。");
        }

        if (value is null)
        {
            await MutateAsync(Queries.ClearItemField,
                new JsonObject { ["projectId"] = project.Id, ["itemId"] = itemId, ["fieldId"] = fieldId }, ct).ConfigureAwait(false);
            return;
        }

        await MutateAsync(Queries.UpdateItemField, new JsonObject
        {
            ["projectId"] = project.Id,
            ["itemId"] = itemId,
            ["fieldId"] = fieldId,
            ["value"] = new JsonObject { ["text"] = value },
        }, ct).ConfigureAwait(false);
    }

    /// <summary>後から足したフィールドを引く。create が true でまだなければ、不足するフィールドを補ってから引き直す。</summary>
    private async Task<FieldDto?> AddedFieldAsync(string projectId, string dataType, string name, bool create, CancellationToken ct)
    {
        async Task<FieldDto?> FindAsync()
        {
            var data = await _client.SendAsync(Queries.ProjectSchema, new JsonObject { ["id"] = projectId },
                GitHubJsonContext.Default.GraphQLResponseProjectNodeData, ct).ConfigureAwait(false);
            return data.Node?.Fields?.Nodes?.FirstOrDefault(f => f.DataType == dataType && f.Name == name);
        }

        var field = await FindAsync().ConfigureAwait(false);
        if (field is null && create)
        {
            await EnsureSchemaAsync(projectId, ct).ConfigureAwait(false);
            field = await FindAsync().ConfigureAwait(false);
        }

        return field;
    }

    public Task UpdateIssueTitleAsync(string issueId, string title, CancellationToken ct = default) =>
        MutateAsync(Queries.UpdateIssueTitle, new JsonObject { ["id"] = issueId, ["title"] = title }, ct);

    public Task UpdateIssueBodyAsync(string issueId, string body, CancellationToken ct = default) =>
        MutateAsync(Queries.UpdateIssueBody, new JsonObject { ["id"] = issueId, ["body"] = body }, ct);

    public Task SetIssueClosedAsync(string issueId, bool closed, bool notPlanned = false, CancellationToken ct = default) =>
        closed
            ? MutateAsync(Queries.CloseIssue, new JsonObject { ["id"] = issueId, ["reason"] = notPlanned ? "NOT_PLANNED" : "COMPLETED" }, ct)
            : MutateAsync(Queries.ReopenIssue, new JsonObject { ["id"] = issueId }, ct);

    /// <summary>Issue を削除する。削除の権限がない場合は false を返す。</summary>
    public async Task<bool> DeleteIssueAsync(string issueId, CancellationToken ct = default)
    {
        try
        {
            await MutateAsync(Queries.DeleteIssue, new JsonObject { ["id"] = issueId }, ct).ConfigureAwait(false);
            return true;
        }
        catch (GitHubGraphQLException ex) when (ex.ErrorTypes.Contains("FORBIDDEN"))
        {
            return false;
        }
    }

    /// <summary>
    /// 項目の GitHub 上の現在値を、<see cref="TaskValues"/> と同じ表現で取得する（競合の検出用）。
    /// アイテムが存在しない場合は例外。
    /// </summary>
    public async Task<string?> GetCurrentValueAsync(string itemId, TaskField field, CancellationToken ct = default)
    {
        var data = await _client.SendAsync(Queries.ItemCurrentValue,
            new JsonObject { ["itemId"] = itemId, ["field"] = field == TaskField.Assignees ? F.GuestAssignees : FieldName(field) ?? F.Status },
            GitHubJsonContext.Default.GraphQLResponseItemValueData, ct).ConfigureAwait(false);
        var node = data.Node ?? throw new GitHubGraphQLException("タスクが見つかりません。", ["NOT_FOUND"]);
        var v = node.FieldValueByName;

        return field switch
        {
            TaskField.Title => node.Content?.Title,
            TaskField.Body => node.Content?.Body,
            TaskField.State => node.Content?.State == "CLOSED" ? TaskValues.Closed : TaskValues.Open,
            TaskField.Assignees => TaskValues.Logins([.. node.Content?.Assignees?.Nodes?.Select(a => a.Login ?? "") ?? [],
                .. People.ParseGuestNames(v?.Text)]),
            TaskField.Parent => node.Content?.Parent?.Id,
            TaskField.BlockedBy => TaskValues.IssueIds(node.Content?.BlockedBy?.Nodes?.Select(n => n.Id ?? "") ?? []),
            TaskField.Milestone => node.Content?.Milestone?.Id,
            TaskField.Status => v?.OptionId,
            // 未設定は null のまま返す（既定値で埋めると、区分を初めて書き込むときに競合と誤判定される）
            TaskField.Kind => v?.Name,
            TaskField.Schedule => v?.Name,
            TaskField.Repositories => TaskValues.Repositories(TaskValues.ParseRepositories(v?.Text)),
            TaskField.Branches => TaskValues.Branches(TaskValues.ParseBranches(v?.Text)),
            TaskField.Estimate => TaskValues.Number(v?.Number),
            // 進捗率は未設定を 0 として扱う（アプリ内の表現に合わせ、誤った競合を検出しない）
            TaskField.Progress => TaskValues.Number(v?.Number ?? 0),
            _ => v?.Date,
        };
    }

    /// <summary>親子関係を設定する（既存の親からは外れる）。</summary>
    public Task AddSubIssueAsync(string parentIssueId, string childIssueId, CancellationToken ct = default) =>
        MutateAsync(Queries.AddSubIssue, new JsonObject { ["parentId"] = parentIssueId, ["childId"] = childIssueId }, ct);

    public Task RemoveSubIssueAsync(string parentIssueId, string childIssueId, CancellationToken ct = default) =>
        MutateAsync(Queries.RemoveSubIssue, new JsonObject { ["parentId"] = parentIssueId, ["childId"] = childIssueId }, ct);

    /// <summary>依存関係を追加する（blockingIssueId が issueId をブロックする）。</summary>
    public Task AddBlockedByAsync(string issueId, string blockingIssueId, CancellationToken ct = default) =>
        MutateAsync(Queries.AddBlockedBy, new JsonObject { ["issueId"] = issueId, ["blockingId"] = blockingIssueId }, ct);

    public Task RemoveBlockedByAsync(string issueId, string blockingIssueId, CancellationToken ct = default) =>
        MutateAsync(Queries.RemoveBlockedBy, new JsonObject { ["issueId"] = issueId, ["blockingId"] = blockingIssueId }, ct);

    /// <summary>Project 内のアイテムを、指定したアイテムの直後（null なら先頭）へ移す。</summary>
    public Task MoveItemAfterAsync(string projectId, string itemId, string? afterItemId, CancellationToken ct = default) =>
        MutateAsync(Queries.UpdateItemPosition, new JsonObject { ["projectId"] = projectId, ["itemId"] = itemId, ["afterId"] = afterItemId }, ct);
}
