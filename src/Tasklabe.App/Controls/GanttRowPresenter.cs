using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Tasklabe.App.Services;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Gantt;

namespace Tasklabe.App.Controls;

/// <summary>ガントチャートの左側の 1 行（タスク名、担当、工数、進捗）。表示範囲の行だけを使い回す。</summary>
internal sealed partial class GanttRowPresenter : Grid
{
    private const double IndentWidth = 16;

    private readonly Border _selectionPill;
    private readonly FontIcon _chevron;
    private readonly TextBlock _number;
    private readonly FontIcon _milestoneIcon;
    private readonly TextBlock _title;
    private readonly TextBlock _assignee;
    private readonly TextBlock _estimate;
    private readonly TextBlock _progress;
    private readonly StackPanel _titlePanel;

    public GanttRowPresenter()
    {
        Height = GanttView.RowHeight;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });

        var body = AppResources.Style("Text.Body");
        var caption = AppResources.Style("Text.Caption");

        _selectionPill = new Border
        {
            Width = 3,
            Height = 16,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
            Background = ThemeResources.Brush("Brand.Accent"),
            Visibility = Visibility.Collapsed,
        };
        Pill.SetIsEnabled(_selectionPill, true);

        _chevron = new FontIcon { FontSize = 12, Width = 16, Glyph = "\uE70D" };
        var chevronHost = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = AppResources.CornerRadius("Radius.Control"),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Child = _chevron,
        };

        _number = new TextBlock
        {
            Style = caption,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Foreground = ThemeResources.Brush("TextFillColorTertiaryBrush"),
        };
        _milestoneIcon = MilestoneVisuals.Icon(11);
        _milestoneIcon.Margin = new Thickness(0, 0, 6, 0);
        _milestoneIcon.Foreground = ThemeResources.Brush("Viz.Milestone");
        _milestoneIcon.Visibility = Visibility.Collapsed;
        _title = new TextBlock { Style = body, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        _titlePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 8, 0) };
        _titlePanel.Children.Add(chevronHost);
        _titlePanel.Children.Add(_milestoneIcon);
        _titlePanel.Children.Add(_number);
        _titlePanel.Children.Add(_title);

        _assignee = Cell(caption, HorizontalAlignment.Left);
        _estimate = Cell(caption, HorizontalAlignment.Right);
        _progress = Cell(caption, HorizontalAlignment.Right);

        Children.Add(_titlePanel);
        Children.Add(_selectionPill);
        Add(_assignee, 1);
        Add(_estimate, 2);
        Add(_progress, 3);
    }

    public GanttRow? Row { get; private set; }

    /// <summary>タスク名の列の左端（開閉の矢印の位置）。</summary>
    public static double TitleLeft(int depth) => 6 + depth * IndentWidth;

    /// <summary>担当・工数・進捗の列の幅（0 は隠す）。</summary>
    public void SetColumnWidths(GanttView.SideColumns widths)
    {
        ColumnDefinitions[1].Width = new GridLength(widths.Assignee);
        ColumnDefinitions[2].Width = new GridLength(widths.Estimate);
        ColumnDefinitions[3].Width = new GridLength(widths.Progress);
        _assignee.Visibility = widths.Assignee > 0 ? Visibility.Visible : Visibility.Collapsed;
        _estimate.Visibility = widths.Estimate > 0 ? Visibility.Visible : Visibility.Collapsed;
        _progress.Visibility = widths.Progress > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Bind(GanttRow row, bool expanded, bool selected)
    {
        Row = row;
        if (row.Milestone is { } milestone)
        {
            BindMilestone(milestone, selected);
            return;
        }

        var node = row.Node!;
        _milestoneIcon.Visibility = Visibility.Collapsed;

        _titlePanel.Margin = new Thickness(TitleLeft(node.Depth), 0, 8, 0);
        _chevron.Visibility = node.HasChildren ? Visibility.Visible : Visibility.Collapsed;
        _chevron.Glyph = expanded ? "\uE70D" : "\uE76C";

        // 番号は設定で出すときだけ、タイトルの前に添える（要件 F-SET-06）
        _number.Text = App.Current.Services.CurrentSettings.ShowNumberInGantt ? TaskKeys.Of(node.Task) : "";
        _number.Visibility = _number.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _title.Text = node.Task.Title;
        _title.FontWeight = node.HasChildren ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
        _title.Opacity = row.IsDone && !node.HasChildren ? 0.6 : 1;
        _title.TextDecorations = node.Task.IsCanceled && !node.HasChildren
            ? Windows.UI.Text.TextDecorations.Strikethrough
            : Windows.UI.Text.TextDecorations.None;

        _assignee.Text = node.Task.Assignees.Count switch
        {
            0 => "",
            1 => People.Name(node.Task.Assignees[0]),
            var n => $"{People.Name(node.Task.Assignees[0])} +{n - 1}",
        };

        var summary = node.Summary;
        _estimate.Text = summary.EstimateHours > 0 ? EffortText.Of(summary.EstimateHours)
            : "—";
        _progress.Text = $"{summary.ProgressPercent:0}%";

        SetSelected(selected);
        AutomationProperties.SetName(this, GanttView.Describe(row));
    }

    /// <summary>マイルストーンの行。最上位に置き、工数は入っているタスクの合計、進捗は集計の進捗率を示す（UI デザイン設計書 3.3.7 節）。</summary>
    private void BindMilestone(Tasklabe.Core.Milestones.MilestoneSummary milestone, bool selected)
    {
        _titlePanel.Margin = new Thickness(TitleLeft(0), 0, 8, 0);
        _chevron.Visibility = Visibility.Collapsed;
        _milestoneIcon.Visibility = Visibility.Visible;
        _number.Visibility = Visibility.Collapsed;
        _title.Text = milestone.Milestone.Title;
        _title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _title.Opacity = 1;
        _assignee.Text = "";
        _estimate.Text = milestone.Progress.EstimateHours > 0 ? EffortText.Of(milestone.Progress.EstimateHours) : "—";
        _progress.Text = $"{milestone.Progress.ProgressPercent:0}%";
        SetSelected(selected);
        AutomationProperties.SetName(this, GanttView.Describe(Row!));
    }

    public void SetSelected(bool selected)
    {
        _selectionPill.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        Background = selected ? ThemeResources.Brush("SubtleFillColorSecondaryBrush")
            : Row?.IsMilestone == true ? MilestoneTint()
            : null;
    }

    /// <summary>マイルストーンの行の淡い塗り（チャート側と同じ濃さ）。</summary>
    private static SolidColorBrush MilestoneTint() =>
        new(((SolidColorBrush)ThemeResources.Brush("Viz.Milestone")).Color) { Opacity = 0.07 };

    private void Add(FrameworkElement element, int column)
    {
        SetColumn(element, column);
        Children.Add(element);
    }

    private static TextBlock Cell(Style style, HorizontalAlignment alignment) => new()
    {
        Style = style,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = alignment,
        Margin = new Thickness(8, 0, 8, 0),
        TextTrimming = TextTrimming.CharacterEllipsis,
    };
}
