using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Tasklabe.App.Controls;
using Tasklabe.App.Services;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Milestones;
using Tasklabe.Core.Wbs;
using Tasklabe.Data;
using Tasklabe.GitHub;

namespace Tasklabe.App.Views.Dialogs;

/// <summary>
/// プロジェクトのマイルストーン（期限）を足す・変える・消す（要件 F-MS-01、F-MS-03、UI デザイン設計書 3.3.7 節）。
/// プロジェクトの「…」、ガントの日付の軸の名前、日付の軸の右クリックから開く。保存すると、期日から自動で決まっていたタスクの所属を付け替える。
/// </summary>
public static class MilestonesDialog
{
    /// <summary>1 行の入力。Source が null なら新しく足す行。</summary>
    private sealed class Row
    {
        public Milestone? Source { get; init; }

        public required TextBox Title { get; init; }

        public required PickerButton Due { get; init; }

        public DateOnly? DueValue { get; set; }

        public bool Deleted { get; set; }
    }

    /// <param name="focus">開いたときにフォーカスを置くマイルストーン。</param>
    /// <param name="newDue">この期日の新しい行を足して開く（ガントの日付の軸の右クリック）。</param>
    public static async Task ShowAsync(XamlRoot root, Project project, TaskTree plan, string? focus = null, DateOnly? newDue = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var caption = AppResources.Style("Text.Caption");
        var summaries = MilestonePlan.Summarize(plan, project.Milestones).ToDictionary(s => s.Milestone.Id, StringComparer.Ordinal);
        var rows = new List<Row>();
        var list = new StackPanel { Spacing = 8 };
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false, IsOpen = false, Title = "保存できませんでした" };
        var today = AppClock.Today;

        Row AddRow(Milestone? source, DateOnly? due)
        {
            var row = new Row
            {
                Source = source,
                Title = new TextBox { Text = source?.Title ?? "", PlaceholderText = "名前（リリース、設計完了 など）" },
                Due = new PickerButton("期日"),
                DueValue = due,
            };
            AutomationProperties.SetName(row.Title, "マイルストーンの名前");
            void ShowDue() => row.Due.SetValue(row.DueValue is { } d ? DateText.Short(d, today) : null, "", placeholder: "期日を選ぶ…");
            row.Due.Pick = async b =>
            {
                if (await ValuePickers.DateAsync(b, null, "期日", [row.DueValue]) is { } picked)
                {
                    row.DueValue = picked.Value;
                    ShowDue();
                }
            };
            ShowDue();

            var grid = new Grid { ColumnSpacing = 8 };
            foreach (var width in (ReadOnlySpan<GridLength>)[GridLength.Auto, new(1, GridUnitType.Star), new(160), new(170), GridLength.Auto])
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            }

            var icon = MilestoneVisuals.Icon(14);
            icon.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(icon);
            Place(grid, row.Title, 1);
            Place(grid, row.Due, 2);

            var count = new TextBlock { Style = caption, VerticalAlignment = VerticalAlignment.Center };
            if (source is not null && summaries.GetValueOrDefault(source.Id) is { } s)
            {
                count.Text = s.Total == 0 ? "タスクなし" : $"進捗 {s.Progress.ProgressPercent:0}%・完了 {s.Done} / {s.Total} 件";
            }

            Place(grid, count, 3);

            var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, Style = AppResources.Style("SubtleButtonStyle") };
            AutomationProperties.SetName(remove, "このマイルストーンを消す");
            ToolTipService.SetToolTip(remove, "消す（タスクは残り、期日から次のマイルストーンに入ります）");
            remove.Click += (_, _) =>
            {
                row.Deleted = !row.Deleted;
                grid.Opacity = row.Deleted ? 0.4 : 1;
                row.Title.IsEnabled = row.Due.IsEnabled = !row.Deleted;
                remove.Content = new FontIcon { Glyph = row.Deleted ? "" : "", FontSize = 14 };
                AutomationProperties.SetName(remove, row.Deleted ? "消すのを取りやめる" : "このマイルストーンを消す");
            };
            Place(grid, remove, 4);

            rows.Add(row);
            list.Children.Add(grid);
            return row;
        }

        foreach (var m in MilestonePlan.Ordered(project.Milestones).Concat(project.Milestones.Where(m => m.Due is null)))
        {
            AddRow(m, m.Due);
        }

        var focusRow = newDue is { } nd ? AddRow(null, nd) : rows.FirstOrDefault(r => r.Source?.Id == focus);

        var addText = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        addText.Children.Add(new FontIcon { Glyph = "", FontSize = 12 });
        addText.Children.Add(new TextBlock { Text = "マイルストーンを追加" });
        var add = new Button { Content = addText, Style = AppResources.Style("SubtleButtonStyle") };
        add.Click += (_, _) => AddRow(null, null).Title.Focus(FocusState.Keyboard);

        var panel = new StackPanel { Spacing = 12, Width = 720 };
        panel.Children.Add(new TextBlock
        {
            Text = "プロジェクトの期限です。期日までのタスクが自動で入り、ガントの区切りと集計に使います。GitHub ではリポジトリの Milestone になります。",
            Style = caption,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(error);
        panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 12, 0) });
        panel.Children.Add(add);

        var dialog = AppDialog.Create(root, "マイルストーン", panel, "保存");
        dialog.Resources["ContentDialogMaxWidth"] = 800.0;
        dialog.Opened += (_, _) => (focusRow?.Title ?? rows.FirstOrDefault()?.Title)?.Focus(FocusState.Programmatic);
        dialog.PrimaryButtonClick += async (d, args) =>
        {
            if (rows.FirstOrDefault(r => !r.Deleted && r.Source is not null && string.IsNullOrWhiteSpace(r.Title.Text)) is { } blank)
            {
                error.Title = "名前を入れてください";
                error.Message = "名前のないマイルストーンは保存できません。消すときは、ごみ箱のボタンを押します。";
                error.IsOpen = true;
                args.Cancel = true;
                blank.Title.Focus(FocusState.Keyboard);
                return;
            }

            var deferral = args.GetDeferral();
            d.IsPrimaryButtonEnabled = false;
            try
            {
                await SaveAsync(project, rows);
            }
            catch (Exception ex) when (ex is GitHubException or HttpRequestException or InvalidOperationException)
            {
                error.Title = "保存できませんでした";
                error.Message = ex is InvalidOperationException ? ex.Message : SyncEngine.Describe(ex);
                error.IsOpen = true;
                args.Cancel = true;
            }
            finally
            {
                d.IsPrimaryButtonEnabled = true;
                deferral.Complete();
            }
        };

        await dialog.ShowAsync();
    }

    /// <summary>変えたものだけを GitHub に反映し、タスクの所属を付け替える。</summary>
    private static async Task SaveAsync(Project project, IReadOnlyList<Row> rows)
    {
        var services = App.Current.Services;
        var tasksBefore = await services.Store.GetTasksAsync(project.Id);
        var before = project.Milestones;
        bool changed = false;
        foreach (var row in rows)
        {
            var title = row.Title.Text.Trim();
            if (row.Source is { } source)
            {
                if (row.Deleted)
                {
                    await services.Workspace.DeleteMilestoneAsync(project, source);
                    changed = true;
                }
                else if (title != source.Title || row.DueValue != source.Due)
                {
                    await services.Workspace.UpdateMilestoneAsync(project, source, title, row.DueValue);
                    changed = true;
                }
            }
            else if (!row.Deleted && title.Length > 0)
            {
                await services.Workspace.CreateMilestoneAsync(project, title, row.DueValue);
                changed = true;
            }
        }

        if (changed)
        {
            await services.Edits.ReassignMilestonesAsync(project.Id, tasksBefore, before);
            App.Current.Shell?.NotifyDataChanged();
            App.Current.Shell?.ShowToast("マイルストーンを保存しました");
        }
    }

    private static void Place(Grid grid, FrameworkElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }
}
