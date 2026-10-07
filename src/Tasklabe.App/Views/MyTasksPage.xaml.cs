using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Tasklabe.App.Controls;
using Tasklabe.App.Services;
using Tasklabe.App.ViewModels;
using Tasklabe.Core.Kanban;

namespace Tasklabe.App.Views;

public sealed partial class MyTasksPage : Page, IKeyboardContent, ITaskSequence, INewTaskContextSource, IFilterHost, IViewOptionsHost, ICommandSource
{
    private const string Screen = "mytasks";

    private readonly ViewOptionButton _orderingButton = new("並び順", ViewOptions.OrderingGlyph, "view.sort");
    private readonly ViewOptionButton _groupingButton = new("グループ", ViewOptions.GroupingGlyph, "view.group");

    public MyTasksPage()
    {
        ViewModel = new MyTasksViewModel(App.Current.Services);
        InitializeComponent();
        TaskList.SetGroups(ViewModel.Groups);
        Kanban.DisplayChanged += (_, _) =>
        {
            UpdateFilterBar();
            UpdateViewTools();
        };

        _orderingButton.Pick = async b => await PickOrderingAsync(b);
        _groupingButton.Pick = async b => await Kanban.PickGroupingAsync(b);
        ViewTools.Children.Add(_orderingButton);
        ViewTools.Children.Add(_groupingButton);

        // 狭いときは、ボタンをアイコンだけにする（UX 規約 UX-25）
        SizeChanged += (_, _) => UpdateViewTools();
    }

    /// <summary>リストの並び順（画面 × ビューで覚える。UX 規約 UX-23）。</summary>
    private static TaskOrdering ListOrdering
    {
        get => Enum.TryParse<TaskOrdering>(ViewState.Get($"sort:{Screen}:list"), out var o) && Enum.IsDefined(o) ? o : TaskOrdering.Default;
        set => ViewState.Set($"sort:{Screen}:list", value == TaskOrdering.Default ? null : value.ToString());
    }

    public MyTasksViewModel ViewModel { get; }

    private string CurrentView => ViewSelector.SelectedItem?.Tag as string ?? "list";

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // 最後に選んだビューを戻す（UX 規約 UX-23）。読み込み前は項目側の状態を変える
        KanbanItem.IsSelected = ViewState.Get($"view:{Screen}") == "kanban";
        ListItem.IsSelected = !KanbanItem.IsSelected;

        App.Current.Shell!.DataChanged += OnDataChanged;
        ToolTipService.SetToolTip(AddButton, KeyHints.Tip("task.new", "タスクを追加"));
        Empty.ActionToolTip = KeyHints.Tip("task.new", "タスクを追加");
        _focusAfterLoad = true;
        await ReloadAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        App.Current.Shell!.DataChanged -= OnDataChanged;
        ViewState.SetSession($"focus:{Screen}:{CurrentView}", TaskList.CaptureFocus() ?? Kanban.CaptureFocus());
    }

    private async void OnDataChanged(object? sender, EventArgs e) => await ReloadAsync();

    private bool _focusAfterLoad;

    private async Task ReloadAsync()
    {
        var focused = TaskList.CaptureFocus() ?? Kanban.CaptureFocus();
        ViewModel.Ordering = ListOrdering;
        await ViewModel.LoadAsync();
        TaskList.SelectItem(App.Current.Shell?.SelectedTask?.ItemId);
        RenderView();

        // 初めての同期が済むまでは、空ではなく読み込み中として示す
        Empty.IsLoading = ViewModel.IsLoading;
        Empty.Title = ViewModel.IsLoading ? "GitHub から読み込んでいます" : "タスクはありません";
        Empty.Message = ViewModel.IsLoading
            ? "初めての同期には少し時間がかかります。"
            : "自分のタスクが、期日の近い順にここへ集まります。";

        // キーボードで続けて操作できるよう、作り直した一覧でもフォーカスを保つ
        if (_focusAfterLoad)
        {
            _focusAfterLoad = false;
            if (ViewState.GetSession($"focus:{Screen}:{CurrentView}") is { } remembered)
            {
                TaskList.RestoreFocus(remembered);
                Kanban.RestoreFocus(remembered);
            }
            else
            {
                FocusContent();
            }
        }
        else if (focused is not null)
        {
            TaskList.RestoreFocus(focused);
            Kanban.RestoreFocus(focused);
        }
    }

    Tasklabe.Core.Domain.TaskItem? ITaskSequence.Step(string itemId, int delta) =>
        Kanban.Visibility == Visibility.Visible ? Kanban.Step(itemId, delta) : TaskList.Step(itemId, delta);

    /// <summary>表示中のビューへフォーカスを移す。</summary>
    public void FocusContent()
    {
        if (Kanban.Visibility == Visibility.Visible)
        {
            Kanban.FocusContent();
        }
        else
        {
            TaskList.FocusContent();
        }
    }

    // ---------------------------------------------------------------- ビュー

    private void OnSelectList(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Select(ListItem, args);

    private void OnSelectKanban(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Select(KanbanItem, args);

    private void Select(SelectorBarItem item, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewSelector.SelectedItem = item;
        args.Handled = true;
    }

    private void OnViewSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        ViewState.Set($"view:{Screen}", CurrentView == "list" ? null : CurrentView);
        RenderView();
        if (IsLoaded)
        {
            FocusContent();
        }
    }

    /// <summary>表示中のビューだけを描き直す。空のときは一覧を隠し、空の状態だけを見せる。</summary>
    private void RenderView()
    {
        TaskList.Visibility = CurrentView == "list" && !ViewModel.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        Kanban.Visibility = CurrentView == "kanban" && !ViewModel.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        if (Kanban.Visibility == Visibility.Visible)
        {
            Kanban.LoadForMyTasks(ViewModel.BoardTasks, ViewModel.Projects, ViewModel.ParentTitleOf);
        }

        UpdateFilterBar();
        UpdateViewTools();
    }

    // ---------------------------------------------------------------- 並び順とグループ（UI デザイン設計書 4.4 節）

    private bool IsKanban => CurrentView == "kanban";

    private void UpdateViewTools()
    {
        if (IsKanban)
        {
            var (label, glyph) = Kanban.OrderingValue;
            _orderingButton.SetValue(label, glyph);
            (label, glyph) = Kanban.GroupingValue;
            _groupingButton.SetValue(label, glyph);
        }
        else
        {
            var current = ViewOptions.Orderings(hasPlanOrder: false).First(o => o.Value == ListOrdering);
            _orderingButton.SetValue(current.Label, current.Glyph);
        }

        _groupingButton.Visibility = IsKanban ? Visibility.Visible : Visibility.Collapsed;
        bool compact = XamlRoot is { } root && root.Size.Width < 1008;
        _orderingButton.IsCompact = compact;
        _groupingButton.IsCompact = compact;
    }

    public Task OpenOrderingAsync() => PickOrderingAsync(_orderingButton);

    public Task OpenGroupingAsync()
    {
        if (!IsKanban)
        {
            App.Current.Shell?.ShowToast("リストにはグループがありません。カンバン（Ctrl + 2）で選べます");
            return Task.CompletedTask;
        }

        return Kanban.PickGroupingAsync(_groupingButton);
    }

    private async Task PickOrderingAsync(FrameworkElement anchor)
    {
        if (IsKanban)
        {
            await Kanban.PickOrderingAsync(anchor);
            return;
        }

        if (await ViewOptions.PickAsync(anchor, "並び順", ViewOptions.Orderings(hasPlanOrder: false), ListOrdering) is { } picked
            && picked.Value != ListOrdering)
        {
            ListOrdering = picked.Value;
            await ReloadAsync();
        }
    }

    // ---------------------------------------------------------------- 絞り込み（UX 規約 UX-24）

    /// <summary>F キー: カンバンでは、完了・中止のカードの表示を絞り込む。</summary>
    public async Task OpenFilterAsync()
    {
        if (Kanban.Visibility != Visibility.Visible)
        {
            App.Current.Shell?.ShowToast("リストで絞り込める条件はありません。カンバン（Ctrl + 2）では完了・中止のカードを絞り込めます");
            return;
        }

        await PickCompletedAsync(ViewSelector);
    }

    private async Task PickCompletedAsync(FrameworkElement anchor)
    {
        var options = KanbanView.CompletedOptions;
        int current = options.ToList().FindIndex(o => o.Value == Kanban.Display.Completed);
        if (await ValuePickers.ChoiceAsync(anchor, "完了・中止のカード", [.. options.Select(o => o.Label)], current) is { } index)
        {
            Kanban.SetDisplay(Kanban.Display with { Completed = options[index].Value });
        }
    }

    private void UpdateFilterBar()
    {
        var chips = new List<FilterChip>();
        if (Kanban.Visibility == Visibility.Visible)
        {
            if (Kanban.Display.Completed != KanbanCompleted.All)
            {
                var label = KanbanView.CompletedOptions.First(o => o.Value == Kanban.Display.Completed).Label;
                chips.Add(new FilterChip($"完了・中止: {label}", PickCompletedAsync, () => Kanban.SetDisplay(Kanban.Display with { Completed = KanbanCompleted.All })));
            }
        }

        var (shown, total) = Kanban.Counts;
        Filters.Update(chips, shown, total, () => Kanban.SetDisplay(Kanban.Display with { Completed = KanbanCompleted.All }));
    }

    // ---------------------------------------------------------------- 追加とコマンドパレット（UX 規約 UX-12、UX-18）

    public NewTaskContext NewTaskContext() => new();

    public IEnumerable<PaletteCommand> Commands()
    {
        yield return new PaletteCommand("ビュー: リスト", "Ctrl+1", () =>
        {
            ViewSelector.SelectedItem = ListItem;
            return Task.CompletedTask;
        }, "マイタスク");
        yield return new PaletteCommand("ビュー: カンバン", "Ctrl+2", () =>
        {
            ViewSelector.SelectedItem = KanbanItem;
            return Task.CompletedTask;
        }, "マイタスク");
        var keymap = App.Current.Services.Keymap;
        yield return new PaletteCommand("並び順を選ぶ…", keymap.Display("view.sort"), OpenOrderingAsync, "マイタスク");
        if (Kanban.Visibility == Visibility.Visible)
        {
            yield return new PaletteCommand("グループを選ぶ…", keymap.Display("view.group"), OpenGroupingAsync, "マイタスク");
            yield return new PaletteCommand("絞り込む…", keymap.Display("view.filter"), OpenFilterAsync, "マイタスク");
        }
    }

    private async void OnAddTask(object sender, RoutedEventArgs e) => await App.Current.MainWindow!.AddTaskAsync();

    private async void OnEmptyAction(object? sender, EventArgs e) => await App.Current.MainWindow!.AddTaskAsync();
}
