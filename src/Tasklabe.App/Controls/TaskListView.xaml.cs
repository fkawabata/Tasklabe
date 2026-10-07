using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Tasklabe.App.Services;
using Tasklabe.App.ViewModels;
using Tasklabe.Core.Domain;
using Windows.Foundation;
using Windows.System;

namespace Tasklabe.App.Controls;

/// <summary>
/// グループ分けしたタスクの一覧。行の選択で詳細パネルを開き、値のセルからその場で値を変える（要件 F-UI-MY-03）。
/// キーボード: Enter で詳細、Space で完了の切り替え、Shift + ↑↓ で複数を選ぶ（UX 規約 UX-07）。
/// そのほかの操作はウィンドウのショートカットで行う。
/// </summary>
public sealed partial class TaskListView : UserControl
{
    /// <summary>タスク名の列に残す最小の幅。これを割り込むときは、決まった順に列を隠す（UX 規約 UX-25）。</summary>
    private const double MinTitleWidth = 200;

    /// <summary>これより狭いときは、状態・タイトル・期日だけを表示する。</summary>
    private const double MinimalWidth = 600;

    /// <summary>列の既定の幅（マイタスク）: 状態、タスク、送信待ち、担当、プロジェクト、工数、進捗、期日。</summary>
    private static readonly double[] TaskColumns = [28, 0, 0, 128, 160, 64, 56, 104];

    /// <summary>隠す順（マイタスク）: プロジェクト → 進捗 → 工数 → 担当。状態・タイトル・期日は隠さない。</summary>
    private static readonly int[] TaskHideOrder = [4, 6, 5, 3];

    /// <summary>列の既定の幅（課題）: 状態、課題、送信待ち、担当、ステータス、期日、更新。</summary>
    private static readonly double[] IssueColumns = [28, 0, 0, 160, 136, 104, 96];

    /// <summary>隠す順（課題）: 更新 → 担当 → ステータス。</summary>
    private static readonly int[] IssueHideOrder = [6, 3, 4];

    private double[] _widths = [.. TaskColumns];

    public TaskListView()
    {
        InitializeComponent();
        List.ContainerContentChanging += (_, e) => ApplyWidths(e.ItemContainer.ContentTemplateRoot as Grid);
    }

    public void SetGroups(ObservableCollection<TaskGroupViewModel> groups) => GroupedTasks.Source = groups;

    /// <summary>課題の一覧として表示する（列は課題、担当、ステータス、期日、更新。要件 F-UI-IS-01）。</summary>
    public bool ShowsIssues
    {
        get => IssueHeader.Visibility == Visibility.Visible;
        set
        {
            IssueHeader.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            TaskHeader.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            List.ItemTemplate = (DataTemplate)Resources[value ? "IssueRow" : "TaskRow"];
            AutomationProperties.SetName(List, value ? "課題の一覧" : "タスクの一覧");
            UpdateColumns();
        }
    }

    /// <summary>選んでいるタスク。</summary>
    public IReadOnlyList<TaskRowViewModel> SelectedRows => [.. List.SelectedItems.OfType<TaskRowViewModel>()];

    // ---------------------------------------------------------------- キーボードのフォーカス（UX 規約 UX-15）

    /// <summary>一覧を作り直す前に、キーボードのフォーカスがあった行のタスクを覚える。</summary>
    public string? CaptureFocus()
    {
        foreach (var e in VisualTree.FocusedAncestors(XamlRoot))
        {
            if (e is ListViewItem { Content: TaskRowViewModel row })
            {
                _focusedIndex = List.Items.OfType<TaskRowViewModel>().ToList().IndexOf(row);
                return row.Task.ItemId;
            }

            if (ReferenceEquals(e, this))
            {
                break;
            }
        }

        return null;
    }

    /// <summary>作り直す前にフォーカスがあった行の位置（その行が消えたときに隣の行へ移すため）。</summary>
    private int _focusedIndex = -1;

    /// <summary>
    /// 作り直した一覧で、同じタスクの行にフォーカスを戻す。削除などでその行がなくなったときは、同じ位置の行（末尾なら一つ上）へ移す。
    /// </summary>
    public void RestoreFocus(string? itemId)
    {
        if (itemId is null)
        {
            return;
        }

        var rows = List.Items.OfType<TaskRowViewModel>().ToList();
        var row = rows.FirstOrDefault(r => r.Task.ItemId == itemId)
            ?? (rows.Count > 0 && _focusedIndex >= 0 ? rows[Math.Min(_focusedIndex, rows.Count - 1)] : null);
        if (row is not null)
        {
            FocusRow(row);
        }
    }

    /// <summary>一覧へフォーカスを移す。選んでいる行がなければ先頭を選ぶ。</summary>
    public void FocusContent()
    {
        if ((List.SelectedItem ?? List.Items.OfType<TaskRowViewModel>().FirstOrDefault()) is TaskRowViewModel row)
        {
            FocusRow(row);
        }
    }

    private void FocusRow(TaskRowViewModel row)
    {
        if (!List.SelectedItems.Contains(row))
        {
            List.SelectedItem = row;
        }

        List.UpdateLayout();
        if (List.ContainerFromItem(row) is ListViewItem container)
        {
            container.Focus(FocusState.Programmatic);
        }
    }

    /// <summary>詳細パネルで表示中のタスクを一覧でも選択状態にする。</summary>
    public void SelectItem(string? itemId)
    {
        if (List.SelectedItems.Count > 1)
        {
            return;
        }

        var row = List.Items.OfType<TaskRowViewModel>().FirstOrDefault(r => r.Task.ItemId == itemId);
        List.SelectedItem = row;
    }

    /// <summary>指定したタスクの前後の行を選ぶ（詳細パネルの ↑↓。UX-10）。</summary>
    internal TaskItem? Step(string itemId, int delta)
    {
        var rows = List.Items.OfType<TaskRowViewModel>().ToList();
        int index = rows.FindIndex(r => r.Task.ItemId == itemId);
        if (index < 0 || index + delta < 0 || index + delta >= rows.Count)
        {
            return null;
        }

        var next = rows[index + delta];
        List.SelectedItem = next;
        List.ScrollIntoView(next);
        return next.Task;
    }

    // ---------------------------------------------------------------- 選択（UX 規約 UX-06、UX-07）

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int count = List.SelectedItems.Count;
        Bar.Update(count);

        // 複数を選んでいるあいだは、1 件の詳細を見ている状態と区別するため詳細パネルを閉じる
        if (count > 1 && App.Current.Shell is { IsDetailOpen: true } shell && !shell.IsCreating)
        {
            shell.SelectTask(null);
        }
        else if (count == 1 && List.SelectedItem is TaskRowViewModel row && App.Current.Shell is { IsDetailOpen: true, IsCreating: false } open)
        {
            // 詳細パネルを開いたまま別の行を選ぶと、パネルの内容を切り替える（UX-10）
            open.SelectTask(row.Task);
        }
    }

    /// <summary>行を対象にした操作の対象。複数を選んでいて、その行が選択に含まれていれば、選んでいるすべてを対象にする。</summary>
    private TaskTarget TargetFor(TaskRowViewModel row, FrameworkElement anchor, Point? position = null)
    {
        var selected = SelectedRows;
        if (!selected.Contains(row))
        {
            List.SelectedItem = row;
            selected = [row];
        }

        return new TaskTarget(row.Task, anchor, position, Selection: [.. selected.Select(r => r.Task)]);
    }

    private async void OnListContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not TaskRowViewModel row)
        {
            return;
        }

        e.Handled = true;
        if (e.TryGetPosition(sender, out var position))
        {
            await TaskMenu.ShowAsync(TargetFor(row, (FrameworkElement)sender, position), sender, position);
        }
        else
        {
            var anchor = (FrameworkElement)e.OriginalSource;
            await TaskMenu.ShowAsync(TargetFor(row, anchor), anchor, null);
        }
    }

    /// <summary>行を押すと選ぶだけにし、ダブルクリックで詳細を開く（計画の表・ガント・カンバンと同じ。Enter は OnListPreviewKeyDown）。</summary>
    private void OnListDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is TaskRowViewModel row)
        {
            App.Current.Shell?.SelectTask(row.Task);
        }
    }

    /// <summary>
    /// Enter で選んでいる行の詳細を開く。ListView は Enter を自分で処理済みにするため、KeyDown ではなく PreviewKeyDown で受ける。
    /// </summary>
    private void OnListPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && List.SelectedItems.Count == 1 && List.SelectedItem is TaskRowViewModel row
            && !Services.KeyInput.IsDown(VirtualKey.Control) && !Services.KeyInput.IsDown(VirtualKey.Shift))
        {
            App.Current.Shell?.SelectTask(row.Task);
            e.Handled = true;
        }
    }

    private async void OnListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape when List.SelectedItems.Count > 1:
                // 複数の選択を解き、フォーカスのある 1 件だけにする（UX-07、UX-14）
                e.Handled = true;
                var focused = CaptureFocus();
                if (List.Items.OfType<TaskRowViewModel>().FirstOrDefault(r => r.Task.ItemId == focused) is { } keep)
                {
                    List.SelectedItem = keep;
                    FocusRow(keep);
                }

                break;
            case VirtualKey.Space when !Services.KeyInput.IsDown(VirtualKey.Control):
                e.Handled = true;
                if (TaskShortcuts.FocusedTask(XamlRoot) is { } target)
                {
                    await TaskShortcuts.RunAsync("task.toggleDone", target);
                }

                break;
        }
    }

    // ---------------------------------------------------------------- 値のセル（UX 規約 UX-19）
    // 押すと、キーボードの操作と同じピッカーをそのセルに開く。複数を選んでいれば、選んでいるすべてに効く

    private void OnStatusClick(object sender, RoutedEventArgs e) => RunOnCell(sender, "task.status");

    private void OnAssigneeClick(object sender, RoutedEventArgs e) => RunOnCell(sender, "task.assign");

    private void OnDueClick(object sender, RoutedEventArgs e) => RunOnCell(sender, "task.due");

    private void OnEstimateClick(object sender, RoutedEventArgs e) => RunOnCell(sender, "task.estimate");

    private void OnProgressClick(object sender, RoutedEventArgs e) => RunOnCell(sender, "task.progress");

    private async void RunOnCell(object sender, string actionId)
    {
        if (sender is FrameworkElement { Tag: TaskRowViewModel row } anchor)
        {
            await TaskShortcuts.RunAsync(actionId, TargetFor(row, anchor));
        }
    }

    // ---------------------------------------------------------------- 幅に応じた列（UX 規約 UX-25）

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateColumns();

    /// <summary>幅が足りなければ、決まった順に列を隠す。隠した値は詳細パネルと読み上げの名前で見られる。</summary>
    private void UpdateColumns()
    {
        var defaults = ShowsIssues ? IssueColumns : TaskColumns;
        var order = ShowsIssues ? IssueHideOrder : TaskHideOrder;
        var widths = defaults.ToArray();
        double available = ActualWidth - 48;

        // ごく狭いとき（ウィンドウの幅 640 以下）は、状態・タイトル・期日だけにする（UX 規約 UX-25）
        bool minimal = ActualWidth > 0 && ActualWidth < MinimalWidth;
        foreach (var column in order)
        {
            if (!minimal && (available <= 0 || available - widths.Sum() >= MinTitleWidth))
            {
                break;
            }

            widths[column] = 0;
        }

        if (widths.SequenceEqual(_widths))
        {
            return;
        }

        _widths = widths;
        ApplyWidths(ShowsIssues ? IssueHeader : TaskHeader);
        for (int i = 0; i < List.Items.Count; i++)
        {
            if (List.ContainerFromIndex(i) is ListViewItem { ContentTemplateRoot: Grid grid })
            {
                ApplyWidths(grid);
            }
        }
    }

    private void ApplyWidths(Grid? grid)
    {
        if (grid is null || grid.ColumnDefinitions.Count != _widths.Length)
        {
            return;
        }

        for (int i = 0; i < _widths.Length; i++)
        {
            // タスク名と送信待ちの印の列（幅 0 で定義）は、XAML の指定（* と Auto）のままにする
            if (i is 1 or 2)
            {
                continue;
            }

            grid.ColumnDefinitions[i].Width = new GridLength(_widths[i]);
        }

        foreach (var child in grid.Children.OfType<FrameworkElement>())
        {
            int column = Grid.GetColumn(child);
            if (column is not (1 or 2))
            {
                child.Visibility = _widths[column] > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }
}
