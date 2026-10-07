using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Tasklabe.App.Controls;
using Tasklabe.App.Services;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Editing;
using Tasklabe.Core.Wbs;

namespace Tasklabe.App.Views.Dialogs;

/// <summary>
/// 課題を計画へ移すときに、置き場所と日程を決める（要件 F-TSK-09、F-UI-IS-02）。
/// 項目はピッカーで選び（UX 規約 UX-02）、移した後は下端の知らせから元に戻せる（UX-26）。
/// 複数の課題をまとめて移すときは、置き場所だけを決める。
/// </summary>
public static class PromoteToPlanDialog
{
    public static async Task ShowAsync(XamlRoot root, IReadOnlyList<TaskItem> tasks, TaskTree plan)
    {
        if (tasks.Count == 0)
        {
            return;
        }

        var style = AppResources.Style("Text.Caption");
        bool single = tasks.Count == 1;
        var first = tasks[0];

        TaskNode? parent = null;
        DateOnly? start = single ? first.Start : null;
        DateOnly? target = single ? first.Target : null;
        double? estimate = single ? first.EstimateHours : null;

        var parentButton = new PickerButton("置き場所");
        var startButton = new PickerButton("予定開始日");
        var targetButton = new PickerButton("期日（予定終了日）");
        var estimateButton = new PickerButton("想定工数");
        var chainBox = new CheckBox { IsChecked = false };

        TaskNode? Previous()
        {
            var siblings = parent is null ? plan.Roots : parent.ChildNodes;
            return siblings.LastOrDefault();
        }

        void Refresh()
        {
            parentButton.SetValue(parent?.Task.Title ?? "（最上位に置く）", "");
            startButton.SetValue(start is { } s ? DateText.Short(s) : null, "");
            targetButton.SetValue(target is { } t ? DateText.Short(t) : null, "");
            estimateButton.SetValue(EffortText.Of(estimate), "");

            // 置き場所の直前（末尾）のタスクの後につなぐ（要件 F-DEP-09。既定はつながない）
            var previous = Previous();
            chainBox.Content = previous is null ? "直前のタスクの後につなぐ（置き場所にタスクがありません）" : $"直前のタスク「{previous.Task.Title}」の後につなぐ";
            chainBox.IsEnabled = previous is not null;
            if (previous is null)
            {
                chainBox.IsChecked = false;
            }
        }

        parentButton.Pick = async b =>
        {
            if (await ValuePickers.ParentAsync(b, null, plan, parent?.Task.IssueId) is { } picked)
            {
                parent = picked.Value;
                Refresh();
            }
        };
        startButton.Pick = async b =>
        {
            if (await ValuePickers.DateAsync(b, null, "予定開始日", [start]) is { } picked)
            {
                start = picked.Value;
                Refresh();
            }
        };
        targetButton.Pick = async b =>
        {
            if (await ValuePickers.DateAsync(b, null, "期日（予定終了日）", [target]) is { } picked)
            {
                target = picked.Value;
                Refresh();
            }
        };
        estimateButton.Pick = async b =>
        {
            if (await ValuePickers.EstimateAsync(b, null, [estimate]) is { } picked)
            {
                estimate = picked.Value;
                Refresh();
            }
        };
        Refresh();

        var panel = new StackPanel { Spacing = 12, Width = 420 };
        panel.Children.Add(new TextBlock
        {
            Text = single ? first.Title : $"{tasks.Count} 件の課題（{string.Join("、", tasks.Take(3).Select(t => t.Title))}{(tasks.Count > 3 ? " ほか" : "")}）",
            Style = AppResources.Style("Text.BodyStrong"),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(PickerButton.Labeled("置き場所", parentButton));
        if (single)
        {
            var dates = new Grid { ColumnSpacing = 8 };
            dates.ColumnDefinitions.Add(new ColumnDefinition());
            dates.ColumnDefinitions.Add(new ColumnDefinition());
            var targetPanel = PickerButton.Labeled("期日（予定終了日）", targetButton);
            Grid.SetColumn(targetPanel, 1);
            dates.Children.Add(PickerButton.Labeled("予定開始日", startButton));
            dates.Children.Add(targetPanel);
            panel.Children.Add(dates);
            panel.Children.Add(PickerButton.Labeled("想定工数", estimateButton));
            panel.Children.Add(chainBox);
            panel.Children.Add(new TextBlock
            {
                Text = "置き場所の親に張った依存関係は、つながなくても受け継ぎます。",
                Style = style,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = single
                ? "同じ Issue のまま、計画の中へ移します。日程は後から計画の表やガントでも変えられます。"
                : "同じ Issue のまま、置き場所の末尾へ移します。日程は後から計画の表やガントで決めます。",
            Style = style,
            TextWrapping = TextWrapping.Wrap,
        });

        var dialog = AppDialog.Create(root, "計画に移す", panel, "移す");
        dialog.Opened += (_, _) => parentButton.Focus(FocusState.Programmatic);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        // 指定した置き場所の末尾に、選んだ順に並べる
        double order = parent switch
        {
            null => WbsOperations.PositionAfter(plan, null).SortOrder,
            { ChildNodes: [.., var last] } => WbsOperations.PositionAfter(plan, last).SortOrder,
            _ => WbsOperations.Spacing,
        };

        var previous = single && chainBox.IsChecked == true ? Previous() : null;
        var edits = new List<(TaskItem, IReadOnlyList<TaskChange>)>();
        foreach (var task in tasks)
        {
            var changes = TaskRules.PromoteToPlan(task, parent?.Task.IssueId, order,
                single ? start : task.Start, single ? target : task.Target, single ? estimate : task.EstimateHours).ToList();
            if (previous is not null && !task.BlockedBy.Contains(previous.Task.IssueId))
            {
                changes.AddRange(TaskRules.Set(task, TaskField.BlockedBy, TaskValues.IssueIds(task.BlockedBy.Append(previous.Task.IssueId))));
            }

            edits.Add((task, changes));
            order += WbsOperations.Spacing;
        }

        await TaskCommands.ApplyAsync(edits, single ? $"「{first.Title}」を計画に移しました" : $"{tasks.Count} 件を計画に移しました");
    }
}
