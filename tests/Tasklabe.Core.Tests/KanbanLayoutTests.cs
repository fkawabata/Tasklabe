using Tasklabe.Core.Domain;
using Tasklabe.Core.Kanban;

namespace Tasklabe.Core.Tests;

public class KanbanLayoutTests
{
    private static readonly DateOnly Today = new(2026, 9, 23);

    private static StatusOption O(string id, string name, StatusCategory category) => new(id, name, "GRAY", category);

    private static Project P(string id, params StatusOption[] options) => new()
    {
        Id = id,
        Title = id,
        Number = 1,
        OwnerLogin = "me",
        Kind = ProjectKind.Personal,
        StatusOptions = options,
    };

    private static TaskItem T(string id, string? status = null, StatusCategory category = StatusCategory.Todo,
        DateOnly? target = null, int number = 1, string project = "p1", DateOnly? actualEnd = null, double? estimate = null) => new()
    {
        ItemId = id,
        ProjectId = project,
        IssueId = "I-" + id,
        RepositoryNameWithOwner = "me/repo",
        Number = number,
        Title = id,
        StatusOptionId = status,
        StatusName = status,
        Category = category,
        Target = target,
        ActualEnd = actualEnd,
        EstimateHours = estimate,
    };

    private static List<string> Ids(IEnumerable<TaskItem> tasks) => [.. tasks.Select(t => t.ItemId)];

    [Fact]
    public void Category_columns_hold_every_category_in_order()
    {
        var columns = KanbanLayout.CategoryColumns(c => c.ToString());

        Assert.Equal(["Backlog", "Todo", "InProgress", "Done", "Pending", "Canceled"], columns.Select(c => c.Name));
        Assert.All(columns, c => Assert.Equal(KanbanLayout.CategoryKey(c.Category!.Value), c.Key));
    }

    [Fact]
    public void Cards_without_a_known_status_go_to_an_unset_column_first()
    {
        var columns = KanbanLayout.ColumnsOf(P("p1", O("1", "Todo", StatusCategory.Todo)));
        var board = KanbanLayout.Build([T("a", "1"), T("b")], columns, t => t.StatusOptionId, KanbanDisplay.Default, Today);

        Assert.Equal([KanbanLayout.UnsetKey, "1"], board.Columns.Select(c => c.Key));
        Assert.Equal(["b"], Ids(board.CellsOf(0)));
        Assert.Equal(["a"], Ids(board.CellsOf(1)));
    }

    [Fact]
    public void Empty_columns_can_be_hidden()
    {
        var columns = KanbanLayout.ColumnsOf(P("p1", O("1", "Todo", StatusCategory.Todo), O("2", "Done", StatusCategory.Done)));
        var board = KanbanLayout.Build([T("a", "1")], columns, t => t.StatusOptionId,
            KanbanDisplay.Default with { ShowEmptyColumns = false }, Today);

        Assert.Equal(["1"], board.Columns.Select(c => c.Key));
    }

    [Fact]
    public void Completed_cards_are_limited_by_when_they_finished()
    {
        TaskItem[] tasks =
        [
            T("open", "1"),
            T("recent", "2", StatusCategory.Done, actualEnd: Today.AddDays(-2)),
            T("old", "2", StatusCategory.Done, actualEnd: Today.AddDays(-20)),
            T("ancient", "2", StatusCategory.Done, actualEnd: Today.AddDays(-60)),
        ];
        var columns = KanbanLayout.ColumnsOf(P("p1", O("1", "Todo", StatusCategory.Todo), O("2", "Done", StatusCategory.Done)));

        List<string> Visible(KanbanCompleted completed) =>
            Ids(KanbanLayout.Build(tasks, columns, t => t.StatusOptionId, KanbanDisplay.Default with { Completed = completed }, Today)
                .Lanes.SelectMany(l => l.Cells.SelectMany(c => c)));

        Assert.Equal(["open", "recent", "old", "ancient"], Visible(KanbanCompleted.All));
        Assert.Equal(["open", "recent", "old"], Visible(KanbanCompleted.PastMonth));
        Assert.Equal(["open", "recent"], Visible(KanbanCompleted.PastWeek));
        Assert.Equal(["open"], Visible(KanbanCompleted.None));
    }

    [Theory]
    [InlineData(TaskOrdering.Default, "b,a,c")]
    [InlineData(TaskOrdering.Due, "a,c,b")]
    [InlineData(TaskOrdering.Created, "c,b,a")]
    [InlineData(TaskOrdering.Estimate, "c,a,b")]
    [InlineData(TaskOrdering.Title, "a,b,c")]
    public void Cards_follow_the_chosen_ordering(TaskOrdering ordering, string expected)
    {
        TaskItem[] tasks =
        [
            T("b", target: null, number: 2, estimate: null),
            T("a", target: Today, number: 1, estimate: 2),
            T("c", target: Today.AddDays(3), number: 3, estimate: 8),
        ];

        Assert.Equal(expected, string.Join(',', Ids(KanbanLayout.Order(tasks, ordering))));
    }

    [Fact]
    public void Lanes_split_cards_and_keep_columns_aligned()
    {
        var columns = KanbanLayout.ColumnsOf(P("p1", O("1", "Todo", StatusCategory.Todo), O("2", "Done", StatusCategory.Done)));
        TaskItem[] tasks = [T("a", "1", project: "x"), T("b", "2", StatusCategory.Done, project: "y"), T("c", "1", project: "y")];

        var board = KanbanLayout.Build(tasks, columns, t => t.StatusOptionId, KanbanDisplay.Default, Today,
            t => new KanbanLaneKey(t.ProjectId, t.ProjectId.ToUpperInvariant(), t.ProjectId == "y" ? 0 : 1));

        Assert.Equal(["Y", "X"], board.Lanes.Select(l => l.Key.Title));
        Assert.Equal(["c"], Ids(board.Lanes[0].Cells[0]));
        Assert.Equal(["b"], Ids(board.Lanes[0].Cells[1]));
        Assert.Equal(["a"], Ids(board.Lanes[1].Cells[0]));
        Assert.Empty(board.Lanes[1].Cells[1]);
    }

    [Fact]
    public void Display_round_trips_and_ignores_unknown_values()
    {
        var display = new KanbanDisplay(TaskOrdering.Due, KanbanGrouping.Project, KanbanCompleted.PastWeek);

        Assert.Equal(display, KanbanDisplay.Parse(display.ToString()));

        // 空の列はアプリの設定で決めるため、以前に保存したものは読まない
        Assert.True(KanbanDisplay.Parse("ordering=Due;empty=0").ShowEmptyColumns);
        Assert.Equal(KanbanDisplay.Default with { Ordering = TaskOrdering.Title },
            KanbanDisplay.Parse("ordering=Title;grouping=Nope;completed=42;junk"));
        Assert.Equal(KanbanDisplay.Default, KanbanDisplay.Parse(null));
    }
}
