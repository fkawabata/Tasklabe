using Tasklabe.Core.Domain;

namespace Tasklabe.Core.Tests;

public class ProjectConventionsTests
{
    [Theory]
    [InlineData("[tasklabe:done] 完了したもの", "Shipped", StatusCategory.Done, true)]
    [InlineData("[tasklabe:doing]", "Review", StatusCategory.InProgress, true)]
    [InlineData("This has been completed", "Done", StatusCategory.Done, false)]
    [InlineData(null, "In Progress", StatusCategory.InProgress, false)]
    [InlineData("", "Backlog", StatusCategory.Backlog, false)]
    [InlineData("[tasklabe:backlog]", "Someday", StatusCategory.Backlog, true)]
    [InlineData("[tasklabe:inprogress]", "Pending", StatusCategory.InProgress, true)]
    [InlineData("[tasklabe:pending]", "Waiting for review", StatusCategory.Pending, true)]
    [InlineData("[tasklabe:canceled]", "見送り", StatusCategory.Canceled, true)]
    [InlineData(null, "Cancelled", StatusCategory.Canceled, false)]
    [InlineData(null, "中止", StatusCategory.Canceled, false)]
    [InlineData(null, "Pending", StatusCategory.Pending, false)]
    [InlineData(null, "保留", StatusCategory.Pending, false)]
    // カテゴリが 4 つだったときの記号は、名前で Backlog と Pending を分ける
    [InlineData("[tasklabe:todo]", "Backlog", StatusCategory.Backlog, true)]
    [InlineData("[tasklabe:todo]", "Ready", StatusCategory.Todo, true)]
    [InlineData("[tasklabe:doing]", "Pending", StatusCategory.Pending, true)]
    public void Category_is_read_from_marker_or_inferred_from_name(string? description, string name, StatusCategory expected, bool marker)
    {
        var category = ProjectConventions.ParseCategory(description, name, out var hasMarker);

        Assert.Equal(expected, category);
        Assert.Equal(marker, hasMarker);
    }

    [Fact]
    public void Default_statuses_cover_every_category()
    {
        Assert.Equal(["Backlog", "Todo", "In Progress", "Done", "Pending", "Canceled"],
            ProjectConventions.DefaultStatuses.Select(s => s.Name));
        Assert.All(Enum.GetValues<StatusCategory>(), c => Assert.Contains(ProjectConventions.DefaultStatuses, s => s.Category == c));

        // 既定の名前は、記号がなくても同じカテゴリに読める
        Assert.All(ProjectConventions.DefaultStatuses,
            s => Assert.Equal(s.Category, ProjectConventions.ParseCategory(null, s.Name, out _)));
    }

    [Fact]
    public void Each_category_is_represented_by_its_first_status()
    {
        StatusOption[] options =
        [
            new("b", "Backlog", "GRAY", StatusCategory.Backlog),
            new("t", "Todo", "BLUE", StatusCategory.Todo),
            new("p", "In Progress", "YELLOW", StatusCategory.InProgress),
            new("r", "Review", "PURPLE", StatusCategory.InProgress),
            new("d", "Done", "GREEN", StatusCategory.Done),
            new("h", "Pending", "ORANGE", StatusCategory.Pending),
            new("c", "Canceled", "RED", StatusCategory.Canceled),
        ];

        Assert.Equal("b", ProjectConventions.DefaultStatus(options, StatusCategory.Backlog)?.Id);
        Assert.Equal("t", ProjectConventions.DefaultStatus(options, StatusCategory.Todo)?.Id);
        Assert.Equal("p", ProjectConventions.DefaultStatus(options, StatusCategory.InProgress)?.Id);
        Assert.Equal("h", ProjectConventions.DefaultStatus(options, StatusCategory.Pending)?.Id);
        Assert.Equal("c", ProjectConventions.DefaultStatus(options, StatusCategory.Canceled)?.Id);
    }

    [Fact]
    public void Missing_categories_fall_back_to_the_closest_one()
    {
        StatusOption[] options =
        [
            new("t", "Todo", "BLUE", StatusCategory.Todo),
            new("p", "In Progress", "YELLOW", StatusCategory.InProgress),
            new("d", "Done", "GREEN", StatusCategory.Done),
        ];

        Assert.Equal("t", ProjectConventions.DefaultStatus(options, StatusCategory.Backlog)?.Id);
        Assert.Equal("p", ProjectConventions.DefaultStatus(options, StatusCategory.Pending)?.Id);

        // 中止には代わりを置かない
        Assert.Null(ProjectConventions.DefaultStatus(options, StatusCategory.Canceled));
    }

    [Theory]
    [InlineData("[tasklabe]", true, false)]
    [InlineData("[tasklabe:inbox]", true, true)]
    [InlineData("社内プロジェクト [TaskLabe]", true, false)]
    [InlineData("unrelated", false, false)]
    [InlineData(null, false, false)]
    public void Managed_projects_are_identified_by_marker(string? description, bool managed, bool inbox)
    {
        Assert.Equal(managed, ProjectConventions.IsManaged(description));
        Assert.Equal(inbox, ProjectConventions.IsInbox(description));
    }
}
