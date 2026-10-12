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

        var parentButton = new PickerButton(Texts.Get("Picker_Parent"));
        var startButton = new PickerButton(Texts.Get("Promote_Start"));
        var targetButton = new PickerButton(Texts.Get("Picker_Due"));
        var estimateButton = new PickerButton(Texts.Get("Field_Estimate"));
        var chainBox = new CheckBox { IsChecked = false };

        TaskNode? Previous()
        {
            var siblings = parent is null ? plan.Roots : parent.ChildNodes;
            return siblings.LastOrDefault();
        }

        void Refresh()
        {
            parentButton.SetValue(parent?.Task.Title ?? Texts.Get("Picker_TopLevel"), "\uE8FD");
            startButton.SetValue(start is { } s ? DateText.Short(s) : null, "\uE787");
            targetButton.SetValue(target is { } t ? DateText.Short(t) : null, "\uE787");
            estimateButton.SetValue(EffortText.Of(estimate), "\uE916");

            // 置き場所の直前（末尾）のタスクの後につなぐ（要件 F-DEP-09。既定はつながない）
            var previous = Previous();
            chainBox.Content = previous is null ? Texts.Get("Promote_ChainNone") : Texts.Format("Promote_Chain", previous.Task.Title);
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
            if (await ValuePickers.DateAsync(b, null, Texts.Get("Promote_Start"), [start]) is { } picked)
            {
                start = picked.Value;
                Refresh();
            }
        };
        targetButton.Pick = async b =>
        {
            if (await ValuePickers.DateAsync(b, null, Texts.Get("Picker_Due"), [target]) is { } picked)
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
            Text = single ? first.Title : Texts.Format("Promote_ManyHeading", tasks.Count, string.Join(Texts.Get("Common_ListSeparator"), tasks.Take(3).Select(t => t.Title)) + (tasks.Count > 3 ? Texts.Get("Promote_AndMore") : "")),
            Style = AppResources.Style("Text.BodyStrong"),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(PickerButton.Labeled(Texts.Get("Picker_Parent"), parentButton));
        if (single)
        {
            var dates = new Grid { ColumnSpacing = 8 };
            dates.ColumnDefinitions.Add(new ColumnDefinition());
            dates.ColumnDefinitions.Add(new ColumnDefinition());
            var targetPanel = PickerButton.Labeled(Texts.Get("Picker_Due"), targetButton);
            Grid.SetColumn(targetPanel, 1);
            dates.Children.Add(PickerButton.Labeled(Texts.Get("Promote_Start"), startButton));
            dates.Children.Add(targetPanel);
            panel.Children.Add(dates);
            panel.Children.Add(PickerButton.Labeled(Texts.Get("Field_Estimate"), estimateButton));
            panel.Children.Add(chainBox);
            panel.Children.Add(new TextBlock
            {
                Text = Texts.Get("Promote_InheritNote"),
                Style = style,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = single
                ? Texts.Get("Promote_SingleNote")
                : Texts.Get("Promote_ManyNote"),
            Style = style,
            TextWrapping = TextWrapping.Wrap,
        });

        var dialog = AppDialog.Create(root, Texts.Get("Promote_Title"), panel, Texts.Get("Promote_Confirm"));
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

        await TaskCommands.ApplyAsync(edits, single ? Texts.Format("Promote_DoneOne", first.Title) : Texts.Format("Promote_DoneMany", tasks.Count));
    }
}
