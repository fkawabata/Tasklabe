using System.Globalization;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Editing;
using Tasklabe.Core.Settings;
using Tasklabe.GitHub.GraphQL;
using F = Tasklabe.Core.Domain.ProjectConventions.Fields;

namespace Tasklabe.GitHub;

/// <summary>GraphQL の応答をドメインモデルへ変換する。</summary>
internal static class ProjectMapper
{
    public static Project ToProject(ProjectDto dto)
    {
        var repository = dto.Repositories?.Nodes?.FirstOrDefault();
        var kind = ProjectConventions.IsInbox(dto.ShortDescription) ? ProjectKind.Inbox
            : dto.Owner?.TypeName == "Organization" ? ProjectKind.Team
            : ProjectKind.Personal;

        return new Project
        {
            Id = dto.Id,
            Number = dto.Number,
            Title = dto.Title,
            Kind = kind,
            OwnerLogin = dto.Owner?.Login ?? "",
            Url = dto.Url,
            RepositoryId = repository?.Id,
            RepositoryNameWithOwner = repository?.NameWithOwner,
            Closed = dto.Closed,
            UpdatedAt = dto.UpdatedAt,
            Settings = ProjectReadme.Read(dto.Readme),
            StatusOptions = StatusField(dto)?.Options?
                .Select(o => new StatusOption(o.Id, o.Name, o.Color ?? "GRAY",
                    ProjectConventions.ParseCategory(o.Description, o.Name, out _)))
                .ToList() ?? [],
            FieldIds = dto.Fields?.Nodes?
                .Where(f => f.Id is not null && f.Name is not null)
                .GroupBy(f => f.Name!)
                .ToDictionary(g => g.Key, g => g.First().Id!) ?? [],
            KindOptionIds = dto.Fields?.Nodes?
                .FirstOrDefault(f => f.DataType == "SINGLE_SELECT" && f.Name == F.Kind)?.Options?
                .ToDictionary(o => o.Name, o => o.Id) ?? [],
        };
    }

    /// <summary>区分として「課題」が明示されているか。</summary>
    public static bool HasExplicitIssueKind(ItemDto item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return item.FieldValues?.Nodes?.Any(v =>
            string.Equals(v.Field?.Name, F.Kind, StringComparison.OrdinalIgnoreCase)
            && v.Name == ProjectConventions.KindOptions.Issue) == true;
    }

    /// <summary>GitHub 上でアイテムに設定されている Status の選択肢の ID。</summary>
    public static string? StatusOptionId(ItemDto item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return item.FieldValues?.Nodes?.FirstOrDefault(v => v.Field?.Name == F.Status)?.OptionId;
    }

    public static FieldDto? StatusField(ProjectDto dto) =>
        dto.Fields?.Nodes?.FirstOrDefault(f => f.DataType == "SINGLE_SELECT" && f.Name == F.Status);

    /// <summary>Issue のアイテムのみを変換する。下書き・プルリクエスト・アーカイブ済みは除く。</summary>
    public static TaskItem? ToTask(ItemDto item, Project project, int position = 0)
    {
        if (item.IsArchived || item.Content is not { TypeName: "Issue", Id: { } issueId } issue)
        {
            return null;
        }

        var values = item.FieldValues?.Nodes?
            .Where(v => v.Field?.Name is not null)
            .GroupBy(v => v.Field!.Name!)
            .ToDictionary(g => g.Key, g => g.First()) ?? [];

        var status = values.GetValueOrDefault(F.Status);
        var statusOption = project.StatusOptions.FirstOrDefault(o => o.Id == status?.OptionId);
        bool closed = issue.State == "CLOSED";

        // GitHub の既定のワークフロー（Item closed）は、Close した Issue の Status を Done にしてしまう。
        // 「対応しない」で閉じた Issue は中止として読み、中止のステータスがあればそれに置き換える（同期で GitHub にも書き戻す）
        if (closed && issue.StateReason == "NOT_PLANNED" && statusOption?.Category != StatusCategory.Canceled
            && ProjectConventions.DefaultStatus(project.StatusOptions, StatusCategory.Canceled) is { } canceled)
        {
            statusOption = canceled;
        }

        return new TaskItem
        {
            ItemId = item.Id,
            ProjectId = project.Id,
            IssueId = issueId,
            RepositoryNameWithOwner = issue.Repository?.NameWithOwner ?? "",
            Number = issue.Number,
            Title = issue.Title ?? "",
            Url = issue.Url,
            Body = issue.Body,
            IsClosed = closed,
            StatusOptionId = statusOption?.Id,
            StatusName = statusOption?.Name,
            Category = TaskValues.CategoryOf(closed, statusOption),
            NonBlocking = values.GetValueOrDefault(F.Schedule)?.Name == ProjectConventions.ScheduleOptions.NonBlocking,
            Kind = ProjectConventions.ParseKind(values.GetValueOrDefault(F.Kind)?.Name, project.Kind,
                looksPlanned: Date(values, F.Start) is not null || Date(values, F.Target) is not null || issue.Parent is not null),
            Start = Date(values, F.Start),
            Target = Date(values, F.Target),
            ActualStart = Date(values, F.ActualStart),
            ActualEnd = Date(values, F.ActualEnd),
            EstimateHours = values.GetValueOrDefault(F.Estimate)?.Number,
            ProgressPercent = values.GetValueOrDefault(F.Progress)?.Number ?? 0,
            ParentIssueId = issue.Parent?.Id,
            SortOrder = (position + 1) * Tasklabe.Core.Wbs.WbsOperations.Spacing,
            Repositories = TaskValues.ParseRepositories(values.GetValueOrDefault(F.Repositories)?.Text),
            Branches = TaskValues.ParseBranches(values.GetValueOrDefault(F.Branches)?.Text),
            MilestoneId = issue.Milestone?.Id,
            Assignees = issue.Assignees?.Nodes?.Select(a => a.Login ?? "").Where(l => l.Length > 0).ToList() ?? [],
            BlockedBy = TaskValues.ParseIssueIds(TaskValues.IssueIds(issue.BlockedBy?.Nodes?.Select(n => n.Id ?? "") ?? [])),
            UpdatedAt = issue.UpdatedAt > item.UpdatedAt ? issue.UpdatedAt : item.UpdatedAt,
        };
    }

    private static DateOnly? Date(Dictionary<string, FieldValueDto> values, string field) =>
        values.GetValueOrDefault(field)?.Date is { } s
        && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;
}
