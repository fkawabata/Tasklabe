using System.Text.Json;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Settings;
using Tasklabe.GitHub.GraphQL;

namespace Tasklabe.GitHub.Tests;

public class ProjectMapperTests
{
    // GitHub GraphQL API の実際の応答の形に合わせたサンプル
    private const string Response = """
        {
          "data": {
            "node": {
              "id": "PVT_1", "number": 3, "title": "Web リニューアル", "shortDescription": "[tasklabe]",
              "url": "https://github.com/users/me/projects/3", "closed": false, "updatedAt": "2026-09-18T10:00:00Z",
              "owner": { "__typename": "User", "id": "U_1", "login": "me" },
              "repositories": { "nodes": [ { "id": "R_1", "nameWithOwner": "me/tasklabe-personal" } ] },
              "fields": { "nodes": [
                { "__typename": "ProjectV2Field", "id": "F_title", "name": "Title", "dataType": "TITLE" },
                { "__typename": "ProjectV2SingleSelectField", "id": "F_status", "name": "Status", "dataType": "SINGLE_SELECT",
                  "options": [
                    { "id": "o1", "name": "Todo", "color": "GRAY", "description": "[tasklabe:todo]" },
                    { "id": "o2", "name": "Review", "color": "BLUE", "description": "[tasklabe:doing]" },
                    { "id": "o3", "name": "Done", "color": "GREEN", "description": "This has been completed" } ] },
                { "__typename": "ProjectV2Field", "id": "F_start", "name": "Start", "dataType": "DATE" }
              ] },
              "items": {
                "pageInfo": { "hasNextPage": false, "endCursor": null },
                "nodes": [
                  { "id": "I_1", "isArchived": false, "updatedAt": "2026-09-18T09:00:00Z",
                    "fieldValues": { "nodes": [
                      { "__typename": "ProjectV2ItemFieldTextValue" },
                      { "__typename": "ProjectV2ItemFieldSingleSelectValue", "optionId": "o2", "name": "Review", "field": { "name": "Status" } },
                      { "__typename": "ProjectV2ItemFieldDateValue", "date": "2026-09-14", "field": { "name": "Start" } },
                      { "__typename": "ProjectV2ItemFieldDateValue", "date": "2026-09-25", "field": { "name": "Target" } },
                      { "__typename": "ProjectV2ItemFieldNumberValue", "number": 16, "field": { "name": "Estimate" } },
                      { "__typename": "ProjectV2ItemFieldNumberValue", "number": 40, "field": { "name": "Progress" } },
                      { "__typename": "ProjectV2ItemFieldSingleSelectValue", "optionId": "k2", "name": "Task", "field": { "name": "Kind" } }
                    ] },
                    "content": { "__typename": "Issue", "id": "ISSUE_1", "number": 12, "title": "画面設計", "state": "OPEN",
                      "url": "https://github.com/me/tasklabe-personal/issues/12", "updatedAt": "2026-09-18T11:00:00Z",
                      "repository": { "id": "R_1", "nameWithOwner": "me/tasklabe-personal" },
                      "assignees": { "nodes": [ { "login": "me" } ] },
                      "parent": { "id": "ISSUE_0" },
                      "blockedBy": { "nodes": [ { "id": "ISSUE_9" }, { "id": "ISSUE_2" } ] } } },
                  { "id": "I_2", "isArchived": false, "updatedAt": "2026-09-18T09:00:00Z",
                    "fieldValues": { "nodes": [
                      { "__typename": "ProjectV2ItemFieldSingleSelectValue", "optionId": "o1", "name": "Todo", "field": { "name": "Status" } } ] },
                    "content": { "__typename": "Issue", "id": "ISSUE_2", "number": 13, "title": "閉じた Issue", "state": "CLOSED",
                      "updatedAt": "2026-09-18T08:00:00Z", "repository": { "id": "R_1", "nameWithOwner": "me/tasklabe-personal" },
                      "assignees": { "nodes": [] }, "parent": null } },
                  { "id": "I_3", "isArchived": false, "updatedAt": "2026-09-18T09:00:00Z",
                    "fieldValues": { "nodes": [] },
                    "content": { "__typename": "DraftIssue" } },
                  { "id": "I_4", "isArchived": true, "updatedAt": "2026-09-18T09:00:00Z",
                    "fieldValues": { "nodes": [] },
                    "content": { "__typename": "Issue", "id": "ISSUE_4", "number": 14, "title": "archived", "state": "OPEN" } }
                ]
              }
            }
          }
        }
        """;

    private static ProjectDto Parse() =>
        JsonSerializer.Deserialize(Response, GitHubJsonContext.Default.GraphQLResponseProjectNodeData)!.Data!.Node!;

    [Fact]
    public void Project_is_mapped_with_status_categories()
    {
        var project = ProjectMapper.ToProject(Parse());

        Assert.Equal("PVT_1", project.Id);
        Assert.Equal(ProjectKind.Personal, project.Kind);
        Assert.Equal("me/tasklabe-personal", project.RepositoryNameWithOwner);
        Assert.Equal(
            [StatusCategory.Todo, StatusCategory.InProgress, StatusCategory.Done],
            project.StatusOptions.Select(o => o.Category));
    }

    [Fact]
    public void Issue_item_is_mapped_with_field_values()
    {
        var dto = Parse();
        var project = ProjectMapper.ToProject(dto);

        var task = ProjectMapper.ToTask(dto.Items!.Nodes![0], project)!;

        Assert.Equal("画面設計", task.Title);
        Assert.Equal(12, task.Number);
        Assert.Equal("Review", task.StatusName);
        Assert.Equal(StatusCategory.InProgress, task.Category);
        Assert.Equal(TaskKind.Task, task.Kind);
        Assert.Equal(new DateOnly(2026, 9, 14), task.Start);
        Assert.Equal(new DateOnly(2026, 9, 25), task.Target);
        Assert.Equal(16, task.EstimateHours);
        Assert.Equal(40, task.ProgressPercent);
        Assert.Equal("ISSUE_0", task.ParentIssueId);
        Assert.Equal(["me"], task.Assignees);
        Assert.Equal(DateTimeOffset.Parse("2026-09-18T11:00:00Z"), task.UpdatedAt);
    }

    [Fact]
    public void Closed_issue_is_done_regardless_of_status()
    {
        var dto = Parse();
        var task = ProjectMapper.ToTask(dto.Items!.Nodes![1], ProjectMapper.ToProject(dto))!;

        Assert.True(task.IsClosed);
        Assert.Equal(StatusCategory.Done, task.Category);
        Assert.Null(task.EstimateHours);
    }

    [Fact]
    public void Issue_closed_as_not_planned_is_canceled_even_if_a_workflow_set_done()
    {
        var dto = Parse();
        var item = dto.Items!.Nodes![1];
        item.Content!.StateReason = "NOT_PLANNED";
        var project = ProjectMapper.ToProject(dto);

        // 中止のステータスがなければ、これまでどおり完了とする
        Assert.Equal(StatusCategory.Done, ProjectMapper.ToTask(item, project)!.Category);

        var withCanceled = project with
        {
            StatusOptions = [.. project.StatusOptions, new StatusOption("o4", "Canceled", "RED", StatusCategory.Canceled)],
        };
        var task = ProjectMapper.ToTask(item, withCanceled)!;

        Assert.Equal(StatusCategory.Canceled, task.Category);
        Assert.Equal("o4", task.StatusOptionId);
        Assert.Equal("o1", ProjectMapper.StatusOptionId(item));
    }

    [Fact]
    public void New_projects_get_default_statuses_keeping_matching_ids()
    {
        var options = ProjectSchema.InitialStatusOptions(Parse(), DefaultSettings.StandardStatuses)!;

        Assert.Equal(["Backlog", "Todo", "In Progress", "Done", "Pending", "Canceled"], options.Select(o => (string?)o!["name"]));
        Assert.Equal("o1", (string?)options[1]!["id"]);
        Assert.Equal("o3", (string?)options[3]!["id"]);
        Assert.Null(options[0]!["id"]);
        Assert.Equal("[tasklabe:backlog]", (string?)options[0]!["description"]);
        Assert.Equal("[tasklabe:pending]", (string?)options[4]!["description"]);
        Assert.Equal("[tasklabe:canceled]", (string?)options[5]!["description"]);
    }

    [Fact]
    public void Edited_statuses_keep_ids_and_move_the_category_marker()
    {
        // 名前と区分を変え、並びを入れ替え、1 つ足す（要件 F-SET-03）
        IReadOnlyList<StatusOptionEdit> edits =
        [
            new("o3", "完了", "GREEN", StatusCategory.Done),
            new("o1", "未着手", "GRAY", StatusCategory.Todo),
            new(null, "保留", "ORANGE", StatusCategory.Pending),
        ];

        var options = ProjectSchema.StatusOptions(Parse(), edits);

        Assert.Equal(["完了", "未着手", "保留"], options.Select(o => (string?)o!["name"]));
        Assert.Equal("o3", (string?)options[0]!["id"]);
        Assert.Equal("o1", (string?)options[1]!["id"]);
        Assert.Null(options[2]!["id"]);
        Assert.StartsWith("[tasklabe:pending]", (string?)options[2]!["description"], StringComparison.Ordinal);
    }

    [Fact]
    public void Project_settings_are_read_from_the_readme()
    {
        var dto = Parse();
        dto.Readme = "# README" + Environment.NewLine + ProjectReadme.Write(null, new ProjectSettings { HoursPerDay = 7.5 });

        Assert.Equal(7.5, ProjectMapper.ToProject(dto).Settings.HoursPerDay);
    }

    [Fact]
    public void Drafts_and_archived_items_are_skipped()
    {
        var dto = Parse();
        var project = ProjectMapper.ToProject(dto);

        Assert.Null(ProjectMapper.ToTask(dto.Items!.Nodes![2], project));
        Assert.Null(ProjectMapper.ToTask(dto.Items!.Nodes![3], project));
    }

    [Fact]
    public void Missing_fields_are_detected()
    {
        var missing = ProjectSchema.MissingFields(Parse()).Select(f => f.Name);

        Assert.Equal(["Target", "Actual Start", "Actual End", "Estimate", "Progress", "Kind", "Schedule", "Repositories", "Branches"], missing);
    }

    [Fact]
    public void Status_markers_are_added_keeping_option_ids()
    {
        var options = ProjectSchema.StatusOptionsNeedingMarkers(Parse())!;

        Assert.Equal(3, options.Count);
        Assert.Equal("o3", (string?)options[2]!["id"]);
        Assert.Equal("[tasklabe:done] This has been completed", (string?)options[2]!["description"]);
        Assert.Equal("[tasklabe:todo]", (string?)options[0]!["description"]);
    }

    [Fact]
    public void Status_markers_are_not_rewritten_when_present()
    {
        var dto = Parse();
        dto.Fields!.Nodes![1].Options![2].Description = "[tasklabe:done]";

        Assert.Null(ProjectSchema.StatusOptionsNeedingMarkers(dto));
    }

    [Theory]
    [InlineData("github.com", "https://api.github.com/graphql")]
    [InlineData("acme.ghe.com", "https://api.acme.ghe.com/graphql")]
    [InlineData("git.example.co.jp", "https://git.example.co.jp/api/graphql")]
    public void GraphQL_endpoint_depends_on_host(string host, string expected)
    {
        Assert.Equal(new Uri(expected), new GitHubHost(host, "id").GraphQLEndpoint);
    }

    [Fact]
    public void Blocking_issues_are_mapped_in_a_stable_order()
    {
        var dto = Parse();
        var task = ProjectMapper.ToTask(dto.Items!.Nodes![0], ProjectMapper.ToProject(dto))!;

        Assert.Equal(["ISSUE_2", "ISSUE_9"], task.BlockedBy);
    }
}
