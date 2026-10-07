using System.Text.Json.Nodes;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Settings;
using Tasklabe.GitHub.GraphQL;
using F = Tasklabe.Core.Domain.ProjectConventions.Fields;

namespace Tasklabe.GitHub;

/// <summary>アプリが必要とする Project のフィールド構成（要件 F-PRJ-03、要件定義書 6.2 節）。</summary>
internal static class ProjectSchema
{
    public sealed record FieldSpec(string Name, string DataType, JsonArray? Options = null);

    public static IReadOnlyList<FieldSpec> RequiredFields =>
    [
        new(F.Start, "DATE"),
        new(F.Target, "DATE"),
        new(F.ActualStart, "DATE"),
        new(F.ActualEnd, "DATE"),
        new(F.Estimate, "NUMBER"),
        new(F.Progress, "NUMBER"),
        new(F.Kind, "SINGLE_SELECT",
        [
            Option(null, ProjectConventions.KindOptions.Issue, "ORANGE", ""),
            Option(null, ProjectConventions.KindOptions.Task, "GRAY", ""),
        ]),
        new(F.Schedule, "SINGLE_SELECT",
        [
            Option(null, ProjectConventions.ScheduleOptions.NonBlocking, "GRAY", "後続の開始を待たせない（Tasklabe の依存関係の計算から外す）"),
        ]),
        new(F.Repositories, "TEXT"),
        new(F.Branches, "TEXT"),
    ];

    /// <summary>不足しているフィールド。</summary>
    public static IReadOnlyList<FieldSpec> MissingFields(ProjectDto project)
    {
        var existing = project.Fields?.Nodes?.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        return RequiredFields.Where(f => !existing.Contains(f.Name)).ToList();
    }

    /// <summary>
    /// Status の選択肢にカテゴリの記号を補った一覧。補う必要がなければ null。
    /// 選択肢の ID を指定して更新するため、既存アイテムのステータスは保たれる。
    /// </summary>
    public static JsonArray? StatusOptionsNeedingMarkers(ProjectDto project)
    {
        var field = ProjectMapper.StatusField(project);
        if (field?.Options is not { Count: > 0 } options)
        {
            return null;
        }

        bool changed = false;
        var result = new JsonArray();
        foreach (var o in options)
        {
            var category = ProjectConventions.ParseCategory(o.Description, o.Name, out bool hasMarker);
            var description = o.Description ?? "";
            if (!hasMarker)
            {
                description = $"{ProjectConventions.CategoryMarker(category)} {description}".Trim();
                changed = true;
            }

            result.Add((JsonNode)Option(o.Id, o.Name, o.Color ?? "GRAY", description));
        }

        return changed ? result : null;
    }

    /// <summary>
    /// 新しい Project の Status を、指定した選択肢の並び（既定または引き継ぎ元のプロジェクト）に置き換える一覧。
    /// GitHub が作る既定の選択肢（Todo、In Progress、Done）は、名前が同じものの ID を引き継ぐ。
    /// </summary>
    public static JsonArray? InitialStatusOptions(ProjectDto project, IReadOnlyList<StatusTemplate> statuses)
    {
        var field = ProjectMapper.StatusField(project);
        if (field is null)
        {
            return null;
        }

        var existing = field.Options ?? [];
        var result = new JsonArray();
        foreach (var (name, color, category) in statuses)
        {
            var id = existing.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))?.Id;
            result.Add((JsonNode)Option(id, name, color, ProjectConventions.CategoryMarker(category)));
        }

        return result;
    }

    /// <summary>
    /// 区分の選択肢に「課題」を補った一覧。補う必要がなければ null。
    /// 既存の選択肢は ID を指定して残すため、アイテムの区分は保たれる。
    /// </summary>
    public static JsonArray? KindOptionsNeedingIssue(ProjectDto project)
    {
        if (KindField(project)?.Options is not { Count: > 0 } options
            || options.Any(o => string.Equals(o.Name, ProjectConventions.KindOptions.Issue, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var result = new JsonArray();
        result.Add((JsonNode)Option(null, ProjectConventions.KindOptions.Issue, "ORANGE", ""));
        foreach (var o in options)
        {
            result.Add((JsonNode)Option(o.Id, o.Name, o.Color ?? "GRAY", o.Description ?? ""));
        }

        return result;
    }

    /// <summary>
    /// Status の選択肢を編集した結果の一覧（要件 F-SET-03）。ID のある選択肢は ID を指定して、アイテムのステータスを保つ。
    /// 説明欄はカテゴリの記号を付け直し、利用者が書いた説明は残す。
    /// </summary>
    public static JsonArray StatusOptions(ProjectDto project, IReadOnlyList<StatusOptionEdit> edits)
    {
        var existing = ProjectMapper.StatusField(project)?.Options ?? [];
        var result = new JsonArray();
        foreach (var edit in edits)
        {
            var current = edit.Id is null ? null : existing.FirstOrDefault(o => o.Id == edit.Id);
            var note = ProjectConventions.StripCategoryMarker(current?.Description);
            var description = $"{ProjectConventions.CategoryMarker(edit.Category)} {note}".Trim();
            result.Add((JsonNode)Option(current?.Id, edit.Name.Trim(), edit.Color, description));
        }

        return result;
    }

    public static FieldDto? KindField(ProjectDto project) =>
        project.Fields?.Nodes?.FirstOrDefault(f => string.Equals(f.Name, F.Kind, StringComparison.OrdinalIgnoreCase));

    private static JsonObject Option(string? id, string name, string color, string description)
    {
        var option = new JsonObject { ["name"] = name, ["color"] = color, ["description"] = description };
        if (id is not null)
        {
            option["id"] = id;
        }

        return option;
    }
}
