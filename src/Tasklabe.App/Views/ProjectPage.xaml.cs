using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Tasklabe.App.Controls;
using Tasklabe.App.Services;
using Tasklabe.App.ViewModels;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Kanban;
using Tasklabe.Core.Scheduling;
using Tasklabe.GitHub;

namespace Tasklabe.App.Views;

public sealed partial class ProjectPage : Page, IKeyboardContent, ITaskSequence, INewTaskContextSource, IFilterHost, IViewOptionsHost, ICommandSource
{
    private static readonly GanttScale[] Scales = [GanttScale.Day, GanttScale.Week, GanttScale.Month];

    private string? _projectId;
    private readonly Segmented _scaleButton;

    /// <summary>いま開いているプロジェクトの ID（クイック追加の既定の追加先）。</summary>
    public string? ProjectId => _projectId;

    /// <summary>ガントチャートで日程の調整をプレビューしているか（そのあいだは元に戻す操作を受け付けない）。</summary>
    public bool IsGanttPreviewing => Gantt.IsPreviewing;

    public ProjectPage()
    {
        ViewModel = new ProjectViewModel(App.Current.Services);
        InitializeComponent();
        Gantt.PreviewEnded += OnGanttPreviewEnded;
        Gantt.EditingChanged += (_, _) => SyncGanttEditToggle();
        SyncGanttEditToggle();

        _scaleButton = new Segmented(Texts.Get("Project_Scale"), [Texts.Get("Project_ScaleDay"), Texts.Get("Project_ScaleWeek"), Texts.Get("Project_ScaleMonth")]);
        _scaleButton.SelectionChanged += (_, i) => SetScale(Scales[i]);
        GanttScaleHost.Content = _scaleButton;

        // Ctrl + ホイールで拡大・縮小して単位が変わったときも、切り替えの表示を合わせる
        Gantt.TimeScaleChanged += (_, _) => _scaleButton.SelectedIndex = Array.IndexOf(Scales, Gantt.TimeScale);
        Gantt.ScaleCycleRequested += (_, _) => SetScale(Scales[(Array.IndexOf(Scales, Gantt.TimeScale) + 1) % Scales.Length]);
        Kanban.DisplayChanged += (_, _) =>
        {
            UpdateFilterBar();
            UpdateKanbanTools();
        };
        _orderingButton.Pick = async b => await Kanban.PickOrderingAsync(b);
        _groupingButton.Pick = async b => await Kanban.PickGroupingAsync(b);
        KanbanTools.Children.Add(_orderingButton);
        KanbanTools.Children.Add(_groupingButton);
        SizeChanged += (_, _) => UpdateKanbanTools();

        Wbs.BulkRequested += async (_, _) => await ShowBulkPlanAsync();
        Gantt.MilestoneInvoked += async (_, milestone) => await ShowMilestonesAsync(focus: milestone.Id);
        Gantt.MilestoneRequested += async (_, due) => await ShowMilestonesAsync(newDue: due);
        ToolTipService.SetToolTip(BulkPlanButton, KeyHints.Tip("wbs.bulk"));

        // プレビューのあいだは、プレビューの帯と同じことを知らせないよう隠す
        Gantt.PreviewStarted += (_, _) => ConflictChip.Hide();
    }

    public ProjectViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is ProjectNavItem project)
        {
            _projectId = project.Id;
            KindText.Text = project.IsTeam ? Texts.Get("Common_Team") : Texts.Get("Common_Personal");
            KindIcon.Glyph = project.IsTeam ? "\uE716" : "\uE77B";
            ApplyProjectKind(project.IsTeam);
            AccessChip.Hide();
            if (project.IsTeam)
            {
                _ = CheckAccessAsync(project.Id);
            }

            // 覚えた表示の状態を戻す（UX 規約 UX-23）
            ViewModel.AssigneeFilter = project.IsTeam ? ViewState.Get($"filter-assignee:{project.Id}") switch
            {
                null => null,
                "-" => "",
                var login => login,
            } : null;
            Gantt.ShowInazuma = ViewState.Get($"gantt-inazuma:{project.Id}") != "0";
            InazumaToggle.IsChecked = Gantt.ShowInazuma;
            Gantt.StickyParents = ViewState.Get($"gantt-sticky:{project.Id}") != "0";
            GanttStickyToggle.IsChecked = Gantt.StickyParents;
            Wbs.StickyParents = ViewState.Get($"wbs-sticky:{project.Id}") != "0";
            PlanStickyToggle.IsChecked = Wbs.StickyParents;
            var scale = Enum.TryParse<GanttScale>(ViewState.Get($"gantt-scale:{project.Id}"), out var saved) ? saved : GanttScale.Day;
            Gantt.TimeScale = scale;
            _scaleButton.SelectedIndex = Array.IndexOf(Scales, scale);
        }

        App.Current.Shell!.DataChanged += OnDataChanged;
        if (_projectId is not null && ViewState.Get($"view:{_projectId}") is { } view
            && Views.Any(i => (string?)i.Tag == view && i.Visibility == Visibility.Visible))
        {
            // 読み込み前は SelectedItem を設定しても XAML の既定（リスト）に戻るため、項目側の状態を変える
            foreach (var item in Views)
            {
                item.IsSelected = (string?)item.Tag == view;
            }
        }

        UpdateView();
        var keymap = App.Current.Services.Keymap;
        ToolTipService.SetToolTip(AddButton, KeyHints.Tip("task.new"));
        ToolTipService.SetToolTip(FilterButton, KeyHints.Tip("view.filter"));
        ToolTipService.SetToolTip(GanttTodayButton, KeyHints.Tip("gantt.today"));
        ToolTipService.SetToolTip(_scaleButton, KeyHints.Tip("gantt.scale"));
        PlanEmpty.ActionToolTip = KeyHints.Tip("task.new");
        _ = keymap;
        _focusAfterLoad = true;
        await ReloadAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        App.Current.Shell!.DataChanged -= OnDataChanged;

        // 選んでいたタスクは、アプリを開いているあいだ覚えて戻す（UX 規約 UX-23）
        if (_projectId is not null)
        {
            ViewState.SetSession($"focus:{_projectId}:{CurrentView}", FocusedItemId());
        }
    }

    private string? FocusedItemId() => CurrentView switch
    {
        "list" => Wbs.FocusedItemId,
        "issues" => IssueList.CaptureFocus(),
        "kanban" => Kanban.CaptureFocus(),
        "gantt" => Gantt.SelectedTask?.ItemId,
        _ => null,
    };

    private async void OnDataChanged(object? sender, EventArgs e) => await ReloadAsync();

    private bool _focusAfterLoad;

    private async Task ReloadAsync()
    {
        if (_projectId is not null)
        {
            var focused = IssueList.CaptureFocus() ?? Kanban.CaptureFocus();
            await ViewModel.LoadAsync(_projectId);
            IssueList.SetGroups(ViewModel.Issues);
            RenderViews();
            FilterButton.Visibility = ViewModel.Project?.IsTeam == true || Kanban.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
            UpdateScheduleConflict();

            // キーボードで続けて操作できるよう、作り直した一覧でもフォーカスを保つ
            if (_focusAfterLoad)
            {
                _focusAfterLoad = false;
                if (ViewState.GetSession($"focus:{_projectId}:{CurrentView}") is { } remembered)
                {
                    RestoreFocus(remembered);
                }
                else
                {
                    FocusContent();
                }
            }
            else if (focused is not null)
            {
                IssueList.RestoreFocus(focused);
                Kanban.RestoreFocus(focused);
            }
        }
    }

    private void RestoreFocus(string itemId)
    {
        switch (CurrentView)
        {
            case "list":
                Wbs.FocusItem(itemId);
                break;
            case "issues":
                IssueList.RestoreFocus(itemId);
                break;
            case "kanban":
                Kanban.RestoreFocus(itemId);
                break;
            default:
                FocusContent();
                break;
        }
    }

    // ---------------------------------------------------------------- 絞り込み（UX 規約 UX-24）

    // ---------------------------------------------------------------- 並び順とグループ（UI デザイン設計書 4.4 節）

    private readonly ViewOptionButton _orderingButton = new(Texts.Get("View_Ordering"), ViewOptions.OrderingGlyph, "view.sort");
    private readonly ViewOptionButton _groupingButton = new(Texts.Get("View_Grouping"), ViewOptions.GroupingGlyph, "view.group");

    private void UpdateKanbanTools()
    {
        var (label, glyph) = Kanban.OrderingValue;
        _orderingButton.SetValue(label, glyph);
        (label, glyph) = Kanban.GroupingValue;
        _groupingButton.SetValue(label, glyph);

        // ビューの切り替えが 5 つ並ぶため、マイタスクより広い幅からアイコンだけにする（UX 規約 UX-25）
        bool compact = XamlRoot is { } root && root.Size.Width < 1200;
        _orderingButton.IsCompact = compact;
        _groupingButton.IsCompact = compact;
    }

    public Task OpenOrderingAsync() => OpenKanbanOptionAsync(Kanban.PickOrderingAsync, _orderingButton, Texts.Get("Project_OrderingOnlyKanban"));

    public Task OpenGroupingAsync() => OpenKanbanOptionAsync(Kanban.PickGroupingAsync, _groupingButton, Texts.Get("Project_GroupingOnlyKanban"));

    /// <param name="unavailable">カンバン以外で押したときの知らせ。</param>
    private Task OpenKanbanOptionAsync(Func<FrameworkElement, Task> pick, FrameworkElement anchor, string unavailable)
    {
        if (Kanban.Visibility != Visibility.Visible)
        {
            App.Current.Shell?.ShowToast(unavailable);
            return Task.CompletedTask;
        }

        return pick(anchor);
    }

    private async void OnFilterClick(object sender, RoutedEventArgs e) => await OpenFilterAsync();

    /// <summary>F キー: 絞り込む条件を選び、その値を選ぶ。</summary>
    public async Task OpenFilterAsync()
    {
        var kinds = new List<(string Label, Func<FrameworkElement, Task> Pick)>();
        if (ViewModel.Project?.IsTeam == true)
        {
            kinds.Add((Texts.Get("Filter_Assignee"), PickAssigneeFilterAsync));
        }

        if (Kanban.Visibility == Visibility.Visible)
        {
            kinds.Add((Texts.Get("Filter_CompletedCards"), PickCompletedAsync));
        }

        if (kinds.Count == 0)
        {
            App.Current.Shell?.ShowToast(Texts.Get("Filter_NothingToFilter"));
            return;
        }

        if (kinds.Count == 1)
        {
            await kinds[0].Pick(FilterButton);
            return;
        }

        if (await ValuePickers.ChoiceAsync(FilterButton, Texts.Get("Filter_ChooseKind"), [.. kinds.Select(k => k.Label)], -1) is { } index)
        {
            await kinds[index].Pick(FilterButton);
        }
    }

    private async Task PickAssigneeFilterAsync(FrameworkElement anchor)
    {
        var me = App.Current.Services.CurrentSettings.UserLogin;
        var values = new List<(string Label, string? Value)> { (Texts.Get("Filter_All"), null) };
        if (me is not null)
        {
            values.Add((Texts.Get("Picker_Me"), me));
        }

        values.Add((Texts.Get("Filter_NoAssignee"), ""));
        values.AddRange(ViewModel.AssigneeCandidates
            .Where(l => !string.Equals(l, me, StringComparison.OrdinalIgnoreCase))
            .Select(l => (People.Display(l), (string?)l)));
        int current = values.FindIndex(v => v.Value == ViewModel.AssigneeFilter);
        if (await ValuePickers.ChoiceAsync(anchor, Texts.Get("Filter_ByAssignee"), [.. values.Select(v => v.Label)], current) is { } index)
        {
            await SetAssigneeFilterAsync(values[index].Value);
        }
    }

    private async Task PickCompletedAsync(FrameworkElement anchor)
    {
        var options = KanbanView.CompletedOptions;
        int current = options.ToList().FindIndex(o => o.Value == Kanban.Display.Completed);
        if (await ValuePickers.ChoiceAsync(anchor, Texts.Get("Filter_CompletedCards"), [.. options.Select(o => o.Label)], current) is { } index)
        {
            Kanban.SetDisplay(Kanban.Display with { Completed = options[index].Value });
        }
    }

    private async Task SetAssigneeFilterAsync(string? value)
    {
        ViewModel.AssigneeFilter = value;
        if (_projectId is not null)
        {
            ViewState.Set($"filter-assignee:{_projectId}", value switch { null => null, "" => "-", _ => value });
            await ViewModel.LoadAsync(_projectId);
            IssueList.SetGroups(ViewModel.Issues);
            RenderViews();
        }
    }

    private string AssigneeFilterLabel => ViewModel.AssigneeFilter switch
    {
        "" => Texts.Get("Filter_NoAssignee"),
        var login when string.Equals(login, App.Current.Services.CurrentSettings.UserLogin, StringComparison.OrdinalIgnoreCase) => Texts.Get("Filter_AssigneeMe"),
        var login => Texts.Format("Filter_AssigneeChip", People.Display(login ?? "")),
    };

    /// <summary>効いている条件をチップで示す。表示するものを減らさない設定（並び順・グループ）はチップにしない。</summary>
    private void UpdateFilterBar()
    {
        var chips = new List<FilterChip>();
        if (ViewModel.AssigneeFilter is not null)
        {
            chips.Add(new FilterChip(AssigneeFilterLabel, PickAssigneeFilterAsync, () => _ = SetAssigneeFilterAsync(null)));
        }

        bool kanban = Kanban.Visibility == Visibility.Visible;
        if (kanban && Kanban.Display.Completed != KanbanCompleted.All)
        {
            var label = KanbanView.CompletedOptions.First(o => o.Value == Kanban.Display.Completed).Label;
            chips.Add(new FilterChip(Texts.Format("Filter_CompletedChip", label), PickCompletedAsync, () => Kanban.SetDisplay(Kanban.Display with { Completed = KanbanCompleted.All })));
        }

        var (shown, total) = CurrentView switch
        {
            // カンバンの総数は、絞り込む前の計画のタスク（子を持たないもの）と課題の数
            "kanban" => (Kanban.Counts.Shown, ViewModel.Tree.All().Count(n => !n.HasChildren && n.Task.Kind == TaskKind.Task) + ViewModel.IssueTotal),
            "issues" => (ViewModel.IssueCount, ViewModel.IssueTotal),
            _ => (ViewModel.PlannedShown, ViewModel.PlannedTotal),
        };
        Filters.Update(chips, shown, total, ClearFilters);
        UpdateFilteredEmpty(chips.Count > 0, shown);
    }

    /// <summary>すべての絞り込みを解く。</summary>
    private void ClearFilters()
    {
        if (Kanban.Visibility == Visibility.Visible)
        {
            Kanban.SetDisplay(Kanban.Display with { Completed = KanbanCompleted.All });
        }

        if (ViewModel.AssigneeFilter is not null)
        {
            _ = SetAssigneeFilterAsync(null);
        }
    }

    /// <summary>絞り込みの結果が 0 件のときは、空の状態と区別して「すべて解除」を示す。</summary>
    private bool _filteredEmpty;

    private void UpdateFilteredEmpty(bool filtering, int shown)
    {
        _filteredEmpty = filtering && shown == 0;
        if (!_filteredEmpty)
        {
            return;
        }

        foreach (var empty in (EmptyState[])[PlanEmpty, IssuesEmpty])
        {
            empty.Title = Texts.Get("Empty_FilteredTitle");
            empty.Message = Texts.Get("Empty_FilteredMessage");
            empty.ActionText = Texts.Get("Filter_ClearAll");
        }

        PlanEmpty.Visibility = CurrentView == "list" ? Visibility.Visible : Visibility.Collapsed;
        IssuesEmpty.Visibility = CurrentView == "issues" ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- ビュー

    private string CurrentView => ViewSelector.SelectedItem?.Tag as string ?? "list";

    private void OnViewSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        UpdateView();
        if (IsLoaded)
        {
            FocusContent();
        }
    }

    TaskItem? ITaskSequence.Step(string itemId, int delta) => CurrentView switch
    {
        "list" => Wbs.Step(itemId, delta),
        "issues" => IssueList.Step(itemId, delta),
        "kanban" => Kanban.Step(itemId, delta),
        "gantt" => Gantt.Step(itemId, delta),
        _ => null,
    };

    /// <summary>表示中のビューへフォーカスを移す（キーボードで続けて操作できるように）。</summary>
    public void FocusContent()
    {
        if (ListPanel.Visibility == Visibility.Visible)
        {
            Wbs.FocusContent();
        }
        else if (IssuesPanel.Visibility == Visibility.Visible)
        {
            IssueList.FocusContent();
        }
        else if (Kanban.Visibility == Visibility.Visible)
        {
            Kanban.FocusContent();
        }
        else if (Gantt.Visibility == Visibility.Visible)
        {
            Gantt.FocusContent();
        }
    }

    private void UpdateView()
    {
        var tag = CurrentView;
        if (_projectId is not null)
        {
            ViewState.Set($"view:{_projectId}", tag == "list" ? null : tag);
        }

        ListPanel.Visibility = tag == "list" ? Visibility.Visible : Visibility.Collapsed;
        IssuesPanel.Visibility = tag == "issues" ? Visibility.Visible : Visibility.Collapsed;
        Kanban.Visibility = tag == "kanban" ? Visibility.Visible : Visibility.Collapsed;
        Gantt.Visibility = tag == "gantt" ? Visibility.Visible : Visibility.Collapsed;
        Status.Visibility = tag == "status" ? Visibility.Visible : Visibility.Collapsed;
        GanttTools.Visibility = Gantt.Visibility;
        PlanTools.Visibility = tag == "list" ? Visibility.Visible : Visibility.Collapsed;
        BulkPlanButton.Visibility = ViewModel.Project?.IsTeam == true ? Visibility.Visible : Visibility.Collapsed;
        KanbanTools.Visibility = Kanban.Visibility;
        FilterButton.Visibility = ViewModel.Project?.IsTeam == true || tag == "kanban" ? Visibility.Visible : Visibility.Collapsed;
        RenderViews();
    }

    /// <summary>表示中のビューだけを描き直す。</summary>
    private void RenderViews()
    {
        if (ViewModel.Project is not { } project)
        {
            return;
        }

        // ガント・状況・日程の調整はこのプロジェクトの稼働日で数え、工数はこのプロジェクトの単位で表す
        ProjectPreferences.Apply(project);

        if (ListPanel.Visibility == Visibility.Visible)
        {
            Wbs.Load(project, ViewModel.Tree, ViewModel.Visible);
        }
        else if (Kanban.Visibility == Visibility.Visible)
        {
            Kanban.Load(project, ViewModel.Tree, ViewModel.Visible, ViewModel.IssueTasks.Where(ViewModel.MatchesFilter));
        }
        else if (Gantt.Visibility == Visibility.Visible)
        {
            Gantt.Load(project, ViewModel.Tree, ViewModel.Visible);
        }
        else if (Status.Visibility == Visibility.Visible)
        {
            Status.Load(project, ViewModel.Tree, ViewModel.IssueCount);
        }

        // 空の状態の文言は、計画を持つチームと、タスクを並べるだけの個人とで変える
        bool team = project.IsTeam;
        PlanEmpty.Title = team ? Texts.Get("Empty_PlanTitle") : Texts.Get("Empty_ListTitle");
        PlanEmpty.Message = team
            ? Hints.Shown ? Texts.Get("Empty_PlanMessageWithKeys") : Texts.Get("Empty_PlanMessage")
            : Texts.Get("Empty_ListMessage");
        PlanEmpty.ActionText = KeyHints.Name("task.new");
        PlanEmpty.Visibility = ViewModel.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        IssuesEmpty.Title = Texts.Get("Empty_IssuesTitle");
        IssuesEmpty.Message = Texts.Get("Empty_IssuesMessage");
        IssuesEmpty.ActionText = Texts.Get("Empty_AddIssue");
        IssuesEmpty.Visibility = ViewModel.IsIssuesEmpty ? Visibility.Visible : Visibility.Collapsed;
        OtherViewEmptyText.Message = team ? Texts.Get("Empty_OtherTeamMessage") : Texts.Get("Empty_OtherPersonalMessage");

        // カンバンは課題も並べるため、課題があれば空とはしない。課題の一覧は専用の空の状態を持つ
        bool empty = ViewModel.IsEmpty
            && ListPanel.Visibility != Visibility.Visible
            && IssuesPanel.Visibility != Visibility.Visible
            && !(Kanban.Visibility == Visibility.Visible && !ViewModel.IsIssuesEmpty);
        OtherViewEmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        UpdateFilterBar();
    }

    private void OnGanttToday(object sender, RoutedEventArgs e) => Gantt.ScrollToToday();

    /// <summary>共有ビューを開く（要件 F-UI-GT-10）。</summary>
    private void OnGanttShare(object sender, RoutedEventArgs e) => OpenShareView();

    private void OpenShareView()
    {
        if (ViewModel.Project is not { } project || ViewModel.Tree is not { } tree)
        {
            return;
        }

        // 担当者で絞り込んでいれば、共有ビューも同じ絞り込みに従う
        var filter = ViewModel.AssigneeFilter is null ? null : AssigneeFilterLabel;
        new PlanShareWindow(project, tree, ViewModel.Visible, filter, ProjectPreferences.Resolve(project).Calendar).Activate();
    }

    private void OnGanttEditToggled(object sender, RoutedEventArgs e)
    {
        Gantt.IsEditing = GanttEditToggle.IsChecked == true;
        Gantt.FocusContent();
    }

    /// <summary>ガントの編集モード（Ctrl + E、Esc でも切り替わる）に、ボタンの状態を合わせる。</summary>
    private void SyncGanttEditToggle()
    {
        GanttEditToggle.IsChecked = Gantt.IsEditing;
        ToolTipService.SetToolTip(GanttEditToggle, KeyHints.Tip("gantt.edit"));
    }

    /// <summary>表示の単位を変え、プロジェクトごとに覚える（UX 規約 UX-23）。</summary>
    private void SetScale(GanttScale scale)
    {
        Gantt.TimeScale = scale;
        _scaleButton.SelectedIndex = Array.IndexOf(Scales, scale);
        if (_projectId is not null)
        {
            ViewState.Set($"gantt-scale:{_projectId}", scale == GanttScale.Day ? null : scale.ToString());
        }
    }

    private void OnInazumaToggled(object sender, RoutedEventArgs e)
    {
        Gantt.ShowInazuma = InazumaToggle.IsChecked == true;
        if (_projectId is not null)
        {
            ViewState.Set($"gantt-inazuma:{_projectId}", Gantt.ShowInazuma ? null : "0");
        }
    }

    private void OnGanttStickyToggled(object sender, RoutedEventArgs e)
    {
        Gantt.StickyParents = GanttStickyToggle.IsChecked == true;
        if (_projectId is not null)
        {
            ViewState.Set($"gantt-sticky:{_projectId}", Gantt.StickyParents ? null : "0");
        }
    }

    private void OnPlanStickyToggled(object sender, RoutedEventArgs e)
    {
        Wbs.StickyParents = PlanStickyToggle.IsChecked == true;
        if (_projectId is not null)
        {
            ViewState.Set($"wbs-sticky:{_projectId}", Wbs.StickyParents ? null : "0");
        }
    }

    private void OnSelectView1(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Select(0, args);

    private void OnSelectView2(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Select(1, args);

    private void OnSelectView3(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Select(2, args);

    private void OnSelectView4(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Select(3, args);

    private void OnSelectView5(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Select(4, args);

    /// <summary>出しているビューの並び順で選ぶ。</summary>
    private void Select(int index, KeyboardAcceleratorInvokedEventArgs args)
    {
        var shown = Views.Where(i => i.Visibility == Visibility.Visible).ToList();
        if (index < shown.Count)
        {
            ViewSelector.SelectedItem = shown[index];
            args.Handled = true;
        }
    }

    private SelectorBarItem[] Views => [ListItem, GanttItem, IssuesItem, KanbanItem, StatusItem];

    /// <summary>
    /// プロジェクトの種別に合わせてビューを出し分ける。
    /// 計画・ガント・課題はチームのためのもので、個人プロジェクトではリストとカンバンだけを扱う。
    /// </summary>
    private void ApplyProjectKind(bool isTeam)
    {
        ListItem.Text = isTeam ? Texts.Get("Project_ViewPlanName") : Texts.Get("Project_ViewListName");
        GanttItem.Visibility = isTeam ? Visibility.Visible : Visibility.Collapsed;
        IssuesItem.Visibility = isTeam ? Visibility.Visible : Visibility.Collapsed;
        StatusItem.Visibility = isTeam ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- タスクの追加（UX 規約 UX-12）

    /// <summary>
    /// このプロジェクト、課題の一覧なら課題、計画の表とガントならタスク、担当者で絞り込んでいればその担当者を既定にする。
    /// 作ったものが絞り込みで見えなくなるときは、作成の後に知らせて「表示する」を置く。
    /// </summary>
    public NewTaskContext NewTaskContext() => new()
    {
        ProjectId = _projectId,
        Kind = CurrentView switch
        {
            "issues" => TaskKind.Issue,
            "list" or "gantt" => TaskKind.Task,
            _ => null,
        },
        Assignee = ViewModel.AssigneeFilter is { Length: > 0 } login ? login : null,
        IsShown = ViewModel.MatchesFilter,
        ShowAll = ClearFilters,
    };

    private async void OnAddTask(object sender, RoutedEventArgs e) => await App.Current.MainWindow!.AddTaskAsync();

    private async void OnEmptyAction(object? sender, EventArgs e)
    {
        if (_filteredEmpty)
        {
            ClearFilters();
            return;
        }

        await App.Current.MainWindow!.AddTaskAsync();
    }

    // ---------------------------------------------------------------- コマンドパレット（UX 規約 UX-18）

    public IEnumerable<PaletteCommand> Commands()
    {
        var keymap = App.Current.Services.Keymap;
        var here = Texts.Get("Palette_ThisProject");
        int n = 0;
        foreach (var item in Views.Where(i => i.Visibility == Visibility.Visible))
        {
            var target = item;
            n++;
            yield return new PaletteCommand(Texts.Format("Palette_View", item.Text), $"Ctrl+{n}", () =>
            {
                ViewSelector.SelectedItem = target;
                return Task.CompletedTask;
            }, here);
        }

        // 見出しのチップの詳細（UX 規約 UX-31）
        foreach (var (chip, label) in ((StatusChip, string)[])[(ConflictChip, Texts.Get("Palette_ShowConflict")), (AccessChip, Texts.Get("Palette_ShowAccess"))])
        {
            if (chip.IsShown)
            {
                yield return new PaletteCommand(label, null, () =>
                {
                    chip.OpenDetails();
                    return Task.CompletedTask;
                }, here);
            }
        }

        if (ListPanel.Visibility == Visibility.Visible)
        {
            yield return new PaletteCommand(Texts.Get("Palette_InsertRow"), keymap.Display("wbs.insertAfter"), () => Wbs.InsertHereAsync(false), here);
            yield return new PaletteCommand(Texts.Get("Palette_InsertChildRow"), keymap.Display("task.addChild"), () => Wbs.InsertHereAsync(true), here);
            if (ViewModel.Project?.IsTeam == true)
            {
                yield return new PaletteCommand(KeyHints.Name("wbs.bulk"), keymap.Display("wbs.bulk"), ShowBulkPlanAsync, here);
            }
        }

        if (ViewModel.Project?.IsTeam == true)
        {
            yield return new PaletteCommand(Texts.Get("Project_Milestones"), null, () => ShowMilestonesAsync(), here);
        }

        if (Gantt.Visibility == Visibility.Visible)
        {
            yield return new PaletteCommand(Texts.Get("Palette_GanttToday"), keymap.Display("gantt.today"), () =>
            {
                Gantt.ScrollToToday();
                return Task.CompletedTask;
            }, here);
            yield return new PaletteCommand(Gantt.IsEditing ? Texts.Get("Palette_GanttEndEdit") : Texts.Get("Palette_GanttStartEdit"), keymap.Display("gantt.edit"), () =>
            {
                Gantt.IsEditing = !Gantt.IsEditing;
                return Task.CompletedTask;
            }, here);
            foreach (var value in Scales.Where(s => s != Gantt.TimeScale))
            {
                var label = value switch
                {
                    GanttScale.Week => Texts.Get("Palette_GanttWeek"),
                    GanttScale.Month => Texts.Get("Palette_GanttMonth"),
                    _ => Texts.Get("Palette_GanttDay"),
                };
                yield return new PaletteCommand(label, keymap.Display("gantt.scale"), () =>
                {
                    SetScale(value);
                    return Task.CompletedTask;
                }, here);
            }

            yield return new PaletteCommand(Texts.Get("Palette_GanttShare"), null, () =>
            {
                OpenShareView();
                return Task.CompletedTask;
            }, here);
        }

        if (Kanban.Visibility == Visibility.Visible)
        {
            yield return new PaletteCommand(Texts.Format("Common_Ellipsis", KeyHints.Name("view.sort")), keymap.Display("view.sort"), OpenOrderingAsync, here);
            yield return new PaletteCommand(Texts.Format("Common_Ellipsis", KeyHints.Name("view.group")), keymap.Display("view.group"), OpenGroupingAsync, here);
        }

        if (FilterButton.Visibility == Visibility.Visible)
        {
            yield return new PaletteCommand(Texts.Format("Common_Ellipsis", KeyHints.Name("view.filter")), keymap.Display("view.filter"), OpenFilterAsync, here);
        }

        yield return new PaletteCommand(Texts.Get("Project_Settings"), null, () =>
        {
            OnOpenProjectSettings(this, new RoutedEventArgs());
            return Task.CompletedTask;
        }, here);
        yield return new PaletteCommand(Texts.Get("Palette_RenameProject"), null, RenameProjectAsync, here);
        yield return new PaletteCommand(Texts.Get("Palette_OpenProjectInGitHub"), null, () =>
        {
            OnOpenProjectInBrowser(this, new RoutedEventArgs());
            return Task.CompletedTask;
        }, here);
        yield return new PaletteCommand(Texts.Get("Palette_ArchiveProject"), null, ArchiveProjectAsync, here);
    }

    // ---------------------------------------------------------------- 権限と招待（要件 F-PRJ-06）

    private string? _accessTarget;

    /// <summary>
    /// 自分が Project とリポジトリに書き込めるかを確かめ、足りなければ理由と依頼先を示す。
    /// 招待されていないまま操作すると、送信が失敗し続けるため、先に知らせる。
    /// </summary>
    private async Task CheckAccessAsync(string projectId)
    {
        ProjectAccess access;
        try
        {
            access = await App.Current.Services.Api.GetProjectAccessAsync(projectId);
        }
        catch (GitHubException)
        {
            // オフラインなどで確かめられないときは何も示さない
            return;
        }

        if (projectId != _projectId)
        {
            return;
        }

        string? message = null;
        if (!access.CanWriteRepository && access.RepositoryNameWithOwner is { } repo)
        {
            message = Texts.Format("Access_RepositoryMessage", repo);
            _accessTarget = $"https://github.com/{repo}";
        }
        else if (!access.CanUpdateProject)
        {
            message = Texts.Get("Access_ProjectMessage");
            _accessTarget = ViewModel.Project?.Url;
        }

        if (message is null || _accessTarget is not { } target)
        {
            AccessChip.Hide();
            return;
        }

        var open = target.Contains("/projects/", StringComparison.Ordinal) ? Texts.Get("Access_OpenProject") : Texts.Get("Access_OpenRepository");
        var title = Texts.Get("Access_Title");
        AccessChip.Show("\uE72E", title, title,
            new ChipSection(ChipSeverity.Caution, title, message, [], [new ChipAction(open, () => OpenUrl(target), Primary: true)]));
    }

    /// <summary>Project のアクセス設定（メンバーの招待）を開く。管理者だけが変更できる。</summary>
    private void OnInviteToProject(object sender, RoutedEventArgs e) =>
        OpenUrl(ViewModel.Project?.Url is { } url ? url.TrimEnd('/') + "/settings/access" : null);

    /// <summary>リポジトリのアクセス設定（コラボレーターの招待）を開く。管理者だけが変更できる。</summary>
    private void OnInviteToRepository(object sender, RoutedEventArgs e) =>
        OpenUrl(ViewModel.Project?.RepositoryNameWithOwner is { } repo ? $"https://github.com/{repo}/settings/access" : null);

    private static void OpenUrl(string? url)
    {
        if (url is not null)
        {
            Browser.Open(url);
        }
    }

    // ---------------------------------------------------------------- 依存関係の食い違い（要件 F-DEP-04）

    /// <summary>
    /// 利用者が見送った食い違い（動かす予定の組）。同じ食い違いのあいだは知らせ直さない。
    /// プロジェクトごとに、アプリを閉じるまで覚える。
    /// </summary>
    private static readonly Dictionary<string, string> DismissedConflicts = [];

    /// <summary>ガントでのプレビューを、この知らせから始めたか。</summary>
    private bool _previewFromConflict;

    private string _conflictSignature = "";

    /// <summary>
    /// 先行の遅れや、ガント以外（表、詳細パネル、GitHub）での予定の変更で、
    /// 後続が先行の終わりより前に始まる予定になっていれば、見出しのチップで知らせる。
    /// 見送った食い違いは、内容が変わるまでアイコンだけの小さな形にする。
    /// </summary>
    private void UpdateScheduleConflict()
    {
        if (ViewModel.Project is not { IsTeam: true } project || Gantt.IsPreviewing)
        {
            ConflictChip.Hide();
            return;
        }

        var shifts = DependencyScheduler.Resolve(ViewModel.Tree, AppClock.Today);
        _conflictSignature = string.Join(";", shifts
            .Select(s => $"{s.Task.ItemId}:{s.After.Start}:{s.After.Target}")
            .Order(StringComparer.Ordinal));
        if (shifts.Count == 0)
        {
            ConflictChip.Hide();
            return;
        }

        bool dismissed = DismissedConflicts.GetValueOrDefault(project.Id) == _conflictSignature;
        bool delayed = shifts.Any(s => s.CauseDelayed);
        var title = delayed ? Texts.Get("Conflict_DelayedTitle") : Texts.Get("Conflict_OverlapTitle");

        // 先行の終わりより前に始まっているもの（食い違いそのもの）と、それに押されて連鎖して動くものを分けて数える
        var overlapping = shifts.Where(s => s.Overlaps).ToList();
        var message = delayed
            ? Texts.Count("Conflict_DelayedMessage", overlapping.Count)
            : Texts.Count("Conflict_OverlapMessage", overlapping.Count);
        if (shifts.Count > overlapping.Count)
        {
            message = Texts.Format("Common_TwoSentences", message, Texts.Format("Conflict_ShiftNote", shifts.Count));
        }

        var details = overlapping.Take(5)
            .Select(s => Texts.Format("Conflict_Pair", s.Cause.Title, s.Task.Title))
            .Concat(overlapping.Count > 5 ? [Texts.Format("Delete_More", overlapping.Count - 5)] : [])
            .ToList();
        List<ChipAction> actions = dismissed ? [] : [new ChipAction(Texts.Get("Conflict_Dismiss"), Dismiss)];
        actions.Add(new ChipAction(Texts.Get("Conflict_Preview"), PreviewScheduleConflict, Primary: true));
        ConflictChip.Show("\uE7BA", dismissed ? null : Texts.Count("Conflict_Chip", shifts.Count), title,
            new ChipSection(ChipSeverity.Caution, title, message, details, actions));
    }

    private void PreviewScheduleConflict()
    {
        // ガントに切り替え、計画全体の食い違いを解消する案をプレビューする
        ViewSelector.SelectedItem = GanttItem;
        _previewFromConflict = Gantt.BeginPreview();
        ConflictChip.Hide();
    }

    private void OnGanttPreviewEnded(object? sender, bool confirmed)
    {
        // この知らせから始めたプレビューを取り消したら、見送ったものとして扱う
        if (_previewFromConflict && !confirmed)
        {
            Dismiss();
        }

        _previewFromConflict = false;
        UpdateScheduleConflict();
    }

    private void Dismiss()
    {
        if (ViewModel.Project is { } project)
        {
            DismissedConflicts[project.Id] = _conflictSignature;
        }

        UpdateScheduleConflict();
    }

    // ---------------------------------------------------------------- マイルストーン（要件 F-MS-01〜04）

    private async Task ShowMilestonesAsync(string? focus = null, DateOnly? newDue = null)
    {
        if (ViewModel.Project is { IsTeam: true } project)
        {
            await Dialogs.MilestonesDialog.ShowAsync(XamlRoot, project, ViewModel.Tree, focus, newDue);
        }
    }

    // ---------------------------------------------------------------- 計画をまとめて入力（要件 F-UI-WBS-05）

    private async void OnBulkPlan(object sender, RoutedEventArgs e) => await ShowBulkPlanAsync();

    /// <summary>
    /// 親タスクとマイルストーンをまとめて入力する。計画の表で子を持つ行を選んでいれば、その配下を置き場所の既定にする。
    /// </summary>
    private async Task ShowBulkPlanAsync()
    {
        if (ViewModel.Project is not { IsTeam: true } project)
        {
            return;
        }

        var tree = ViewModel.Tree;
        var parent = Wbs.CurrentTask is { } current && tree.Find(current.ItemId) is { HasChildren: true } node ? node : null;
        await Dialogs.BulkPlanDialog.ShowAsync(XamlRoot, project, tree, parent);
    }

    // ---------------------------------------------------------------- プロジェクトの操作

    /// <summary>
    /// 「…」のメニュー。押したボタンから育つ面に、プロジェクトの操作を並べる。
    /// マイルストーンとメンバーの招待はチームのプロジェクトだけに出し、アーカイブは末尾に分ける。
    /// </summary>
    private void OnProjectMenu(object sender, RoutedEventArgs e)
    {
        bool team = ViewModel.Project?.IsTeam == true;
        var items = new List<MenuEntry>
        {
            new MenuCommand(Texts.Get("Project_Settings"), "\uE713", () => Run(() => OnOpenProjectSettings(this, e))),
            new MenuCommand(Texts.Get("Project_Rename"), "\uE8AC", RenameProjectAsync),
        };
        if (team)
        {
            items.Add(new MenuCommand(Texts.Get("Project_Milestones"), "\u25C6", () => ShowMilestonesAsync()) { GlyphFontFamily = "Segoe UI Symbol" });
            items.Add(new MenuSubmenu(Texts.Get("Project_Invite"), "\uE8FA",
            [
                new MenuCommand(Texts.Get("Project_InviteToProject"), null, () => Run(() => OnInviteToProject(this, e))),
                new MenuCommand(Texts.Get("Project_InviteToRepository"), null, () => Run(() => OnInviteToRepository(this, e))),
            ]));
        }

        items.Add(new MenuCommand(Texts.Get("Project_OpenInGitHub"), "\uE8A7", () => Run(() => OnOpenProjectInBrowser(this, e))));
        items.Add(MenuSeparator.Instance);
        items.Add(new MenuCommand(Texts.Get("Project_Archive"), "\uE7B8", ArchiveProjectAsync));
        new MorphMenu(Texts.Get("Project_Menu"), items).Show((FrameworkElement)sender, null);

        static Task Run(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }

    /// <summary>プロジェクトの設定を開く（要件 F-SET-02）。</summary>
    private void OnOpenProjectSettings(object sender, RoutedEventArgs e)
    {
        if (_projectId is not null)
        {
            App.Current.MainWindow!.OpenProjectSettings(_projectId);
        }
    }

    private void OnOpenProjectInBrowser(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Project?.Url is { } url)
        {
            Browser.Open(url);
        }
    }

    /// <summary>プロジェクトの名前を変える（要件 F-PRJ-01）。</summary>
    private async Task RenameProjectAsync()
    {
        if (ViewModel.Project is not { } project)
        {
            return;
        }

        var box = new TextBox { Text = project.Title, SelectionStart = project.Title.Length };
        AutomationProperties.SetName(box, Texts.Get("Project_NameField"));
        var dialog = AppDialog.Create(XamlRoot, Texts.Get("Project_RenameTitle"), box, Texts.Get("Common_Change"));
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary
            || box.Text.Trim() is not { Length: > 0 } title || title == project.Title)
        {
            return;
        }

        await App.Current.Services.Workspace.RenameProjectAsync(project, title);
        await App.Current.Shell!.ReloadAsync();
    }

    /// <summary>プロジェクトをアーカイブする（要件 F-PRJ-01）。</summary>
    private async Task ArchiveProjectAsync()
    {
        if (ViewModel.Project is not { } project)
        {
            return;
        }

        var dialog = AppDialog.Confirm(XamlRoot, Texts.Get("Archive_Title"), Texts.Format("Archive_Message", project.Title), Texts.Get("Archive_Confirm"));
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await App.Current.Services.Workspace.SetProjectClosedAsync(project, closed: true);
        await App.Current.Shell!.ReloadAsync();
        App.Current.MainWindow?.GoToMyTasks();
    }
}
