using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Tasklabe.App.Services;
using Tasklabe.Core.Calendar;
using Tasklabe.Core.Dashboard;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Wbs;
using Windows.UI;

namespace Tasklabe.App.Controls;

/// <summary>
/// プロジェクトの状況（UI デザイン設計書 3.2.3 節）。
/// 計画の進み具合、手当てが要るタスク、担当者の負荷を一画面で示す。
/// </summary>
public sealed partial class ProjectStatusView : UserControl
{
    private DashboardView _view = DashboardView.Empty;

    public ProjectStatusView()
    {
        InitializeComponent();

        // 色はテーマごとに異なるため、テーマが変わったら作り直す
        ActualThemeChanged += (_, _) => Render();
    }

    /// <param name="issueCount">計画に入っていないタスクの件数（要件 F-UI-DB-04）。</param>
    public void Load(Project project, TaskTree tree, int issueCount)
    {
        ArgumentNullException.ThrowIfNull(project);

        _view = DashboardModel.Build(
            [(project, tree)],
            AppClock.Today,
            new Dictionary<string, int> { [project.Id] = issueCount },
            ProjectPreferences.Resolve(project).HoursPerDay);
        CapacityText.Text = $"1 日 {ProjectPreferences.Resolve(project).HoursPerDay:0.#}h を超える日を強調します";
        _milestones = project.IsTeam ? Tasklabe.Core.Milestones.MilestonePlan.Summarize(tree, project.Milestones) : [];
        Render();
        RenderMilestones();
    }

    private IReadOnlyList<Tasklabe.Core.Milestones.MilestoneSummary> _milestones = [];

    /// <summary>
    /// マイルストーンごとに、期限までのタスクの進捗率と件数を並べる（要件 F-UI-DB-05、F-MS-04）。期日を過ぎたことでは色を変えず、
    /// 入っているタスクが期日より後に終わる予定のとき（期日超え）だけ、件数と遅れの日数を遅れの色で添える。
    /// </summary>
    private void RenderMilestones()
    {
        MilestoneRows.Children.Clear();
        MilestoneCard.Visibility = _milestones.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var caption = AppResources.Style("Text.Caption");
        foreach (var s in _milestones)
        {
            var grid = new Grid { ColumnSpacing = 12 };
            foreach (var width in (ReadOnlySpan<GridLength>)[new(1, GridUnitType.Star), new(120), new(2, GridUnitType.Star), new(56), new(200)])
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            }

            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(Services.MilestoneVisuals.Icon(12));
            name.Children.Add(new TextBlock { Text = s.Milestone.Title, TextTrimming = TextTrimming.CharacterEllipsis });
            grid.Children.Add(name);
            Add(grid, new TextBlock { Text = s.Milestone.Due is { } d ? Services.DateText.Short(d) : "期日なし", Style = caption, VerticalAlignment = VerticalAlignment.Center }, 1);
            Add(grid, new ProgressBar { Value = s.Progress.ProgressPercent, Maximum = 100, VerticalAlignment = VerticalAlignment.Center }, 2);
            Add(grid, new TextBlock { Text = s.Total == 0 ? "—" : $"{s.Progress.ProgressPercent:0}%", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right }, 3);
            var counts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            counts.Children.Add(new TextBlock { Text = s.Total == 0 ? "タスクなし" : $"完了 {s.Done} / {s.Total} 件", Style = caption });
            string? late = s.Late > 0 && s.LateEnd is { } lateEnd ? $"⚠ 期日超え {s.Late} 件（{Services.DateText.Short(lateEnd)}・▲{s.LateDays}日）" : null;
            if (late is not null)
            {
                counts.Children.Add(new TextBlock { Text = late, Style = caption, Foreground = Services.ThemeResources.Brush("Viz.Delay"), TextTrimming = TextTrimming.CharacterEllipsis });
            }

            Add(grid, counts, 4);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(grid,
                $"{s.Milestone.Title}、期日 {(s.Milestone.Due is { } due ? Services.DateText.Short(due) : "なし")}、進捗 {s.Progress.ProgressPercent:0}%、完了 {s.Done} / {s.Total} 件"
                + (late is not null ? "、" + late.TrimStart('⚠', ' ') : ""));
            MilestoneRows.Children.Add(grid);
        }

        static void Add(Grid grid, FrameworkElement element, int column)
        {
            Grid.SetColumn(element, column);
            grid.Children.Add(element);
        }
    }

    private void Render()
    {
        var summary = _view.Summary;
        double schedule = _view.Projects.Count > 0 ? _view.Projects[0].ScheduleDays : 0;

        ProgressMetric.Text = $"{summary.ProgressPercent:0}%";
        ProgressCaption.Text = summary.EstimateHours > 0
            ? $"想定 {Services.EffortText.Of(summary.EstimateHours)}"
            : "計画のタスクがありません";

        ScheduleMetric.Text = ScheduleText(schedule);
        ScheduleMetric.Foreground = Brush(schedule <= -1 ? "Viz.Delay" : "TextFillColorPrimaryBrush");
        ScheduleCaption.Text = schedule switch
        {
            <= -1 => "予定より遅れています",
            >= 1 => "予定より進んでいます",
            _ => "予定どおりです",
        };

        DelayedMetric.Text = $"{_view.DelayedCount}";
        DelayedMetric.Foreground = Brush(_view.DelayedCount > 0 ? "Viz.Delay" : "TextFillColorPrimaryBrush");
        var worst = _view.Attention.FirstOrDefault(a => a.Attention == TaskAttention.Delayed);
        DelayedCaption.Text = worst is null ? "遅れているタスクはありません" : $"最大 {worst.DelayDays} 日の遅れ";

        RemainingMetric.Text = Services.EffortText.Of(summary.RemainingHours);
        RemainingCaption.Text = _view.UnestimatedCount > 0
            ? $"未見積り {_view.UnestimatedCount} 件を除く"
            : $"今週が期日 {_view.DueThisWeekCount} 件";

        IssueMetric.Text = $"{_view.IssueCount}";
        IssueCaption.Text = _view.IssueCount > 0 ? "計画に入っていない" : "すべて計画に入っています";

        BuildAttention();
        BuildWorkload();
    }

    /// <summary>
    /// 予定比の数値。遅れは、ほかの遅れの表示（遅延タスク、期日超え）と同じ ▲ で示す。
    /// 進んでいるときに三角を使うと遅れの印に見えるため、「+」で示す。
    /// </summary>
    private static string ScheduleText(double days) => days switch
    {
        <= -1 => $"▲{Math.Abs(days):0.#}日",
        >= 1 => $"+{days:0.#}日",
        _ => "±0日",
    };

    // ---------------------------------------------------------------- 注意が必要なタスク

    private void OnAttentionTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => BuildAttention();

    private void BuildAttention()
    {
        AttentionRows.Children.Clear();
        var kind = AttentionSelector.SelectedItem switch
        {
            var i when i == SoonTab => TaskAttention.DueSoon,
            var i when i == UnestimatedTab => TaskAttention.Unestimated,
            _ => TaskAttention.Delayed,
        };

        var tasks = _view.Attention.Where(a => a.Attention == kind).Take(20).ToList();
        AttentionEmpty.Visibility = tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var item in tasks)
        {
            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var detail = new List<string>();
            if (item.Task.Assignees.Count > 0)
            {
                detail.Add(string.Join("、", item.Task.Assignees.Select(People.Display)));
            }

            if (item.Task.Target is { } target)
            {
                detail.Add(DateText.Short(target));
            }

            var lines = new StackPanel();
            lines.Children.Add(Text(item.Task.Title, "Text.Body"));
            if (detail.Count > 0)
            {
                lines.Children.Add(Text(string.Join("　", detail), "Text.Caption"));
            }

            Grid.SetColumn(lines, 0);
            grid.Children.Add(lines);

            string badge = item.Attention switch
            {
                TaskAttention.Delayed => $"▲{item.DelayDays}日",
                TaskAttention.DueSoon => "期日が近い",
                _ => "未見積り",
            };
            Add(grid, Right(badge, "Text.Caption", item.Attention == TaskAttention.Delayed ? "Viz.Delay" : null), 1);

            var button = new Button
            {
                Content = grid,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(8, 4, 8, 4),
                Style = AppResources.Style("SubtleButtonStyle"),
            };
            AutomationProperties.SetName(button, $"{item.Task.Title}、{badge}");
            var task = item.Task;
            button.Click += (_, _) => App.Current.Shell?.SelectTask(task);
            AttentionRows.Children.Add(button);
        }
    }

    // ---------------------------------------------------------------- 担当者別の負荷

    private void BuildWorkload()
    {
        WorkloadGrid.Children.Clear();
        WorkloadGrid.ColumnDefinitions.Clear();
        WorkloadGrid.RowDefinitions.Clear();

        WorkloadEmpty.Visibility = _view.Workload.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_view.Workload.Count == 0)
        {
            return;
        }

        WorkloadGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        foreach (var _ in _view.WorkloadDates)
        {
            WorkloadGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        }

        WorkloadGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < _view.WorkloadDates.Count; i++)
        {
            var date = _view.WorkloadDates[i];
            var label = Center($"{date.Month}/{date.Day}\n{date.ToString("ddd", DateText.Japanese)}", "Text.Caption");
            label.Opacity = WorkCalendar.IsHoliday(date) ? 0.5 : 1;
            Add(WorkloadGrid, label, i + 1, 0);
        }

        for (int r = 0; r < _view.Workload.Count; r++)
        {
            var row = _view.Workload[r];
            WorkloadGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });

            var name = Text(row.Login.Length == 0 ? "担当者なし" : People.Name(row.Login), "Text.Body");
            name.VerticalAlignment = VerticalAlignment.Center;
            Add(WorkloadGrid, name, 0, r + 1);

            for (int c = 0; c < row.Cells.Count; c++)
            {
                Add(WorkloadGrid, WorkloadCellView(row.Cells[c], _view.WorkloadDates[c], row.Login), c + 1, r + 1);
            }
        }
    }

    private static Border WorkloadCellView(WorkloadCell cell, DateOnly date, string login)
    {
        double ratio = Math.Min(cell.Hours / cell.Capacity, 1);
        var border = new Border
        {
            CornerRadius = AppResources.CornerRadius("Radius.Control"),
            Background = cell.Hours <= 0
                ? Brush(WorkCalendar.IsHoliday(date) ? "SubtleFillColorTertiaryBrush" : "SubtleFillColorSecondaryBrush")
                : new SolidColorBrush(Tint(cell.IsOverloaded ? "Viz.Delay" : "Viz.Progress", 0.25 + ratio * 0.75)),
        };

        if (cell.Hours > 0)
        {
            var text = Center($"{cell.Hours:0.#}", "Text.Caption");
            text.Foreground = Brush(ratio > 0.55 ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorPrimaryBrush");
            if (cell.IsOverloaded)
            {
                text.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
            }

            border.Child = text;

            // 内訳（その日のタスクと工数）
            var lines = cell.Entries.OrderByDescending(e => e.Hours)
                .Select(e => $"{e.Task.Title}　{EffortText.Of(e.Hours)}");
            ToolTipService.SetToolTip(border, new ToolTip
            {
                Content = $"{DateText.Short(date)}　{EffortText.Of(cell.Hours)} / {EffortText.Of(cell.Capacity)}\n{string.Join('\n', lines)}",
            });
        }

        AutomationProperties.SetName(border,
            $"{(login.Length == 0 ? "担当者なし" : login)} {DateText.Short(date)} {EffortText.Of(cell.Hours)}{(cell.IsOverloaded ? "、過負荷" : "")}");
        return border;
    }

    private static Color Tint(string key, double alpha)
    {
        var color = ((SolidColorBrush)ThemeResources.Brush(key)).Color;
        return Color.FromArgb((byte)(255 * Math.Clamp(alpha, 0, 1)), color.R, color.G, color.B);
    }

    // ---------------------------------------------------------------- 小さな部品

    private static Brush Brush(string key) => ThemeResources.Brush(key);

    private static TextBlock Text(string text, string style) => new()
    {
        Text = text,
        Style = AppResources.Style(style),
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private static TextBlock Right(string text, string style, string? foreground = null)
    {
        var block = Text(text, style);
        block.HorizontalAlignment = HorizontalAlignment.Right;
        if (foreground is not null)
        {
            block.Foreground = Brush(foreground);
        }

        return block;
    }

    private static TextBlock Center(string text, string style)
    {
        var block = Text(text, style);
        block.HorizontalAlignment = HorizontalAlignment.Center;
        block.TextAlignment = TextAlignment.Center;
        block.TextTrimming = TextTrimming.None;
        return block;
    }

    private static void Add(Grid grid, FrameworkElement element, int column, int row = 0)
    {
        Grid.SetColumn(element, column);
        Grid.SetRow(element, row);
        grid.Children.Add(element);
    }
}
