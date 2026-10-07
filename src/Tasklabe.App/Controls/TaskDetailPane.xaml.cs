using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Tasklabe.App.Services;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Editing;
using Tasklabe.Core.Keyboard;
using Tasklabe.Core.Scheduling;
using Tasklabe.Core.Wbs;
using VirtualKey = Windows.System.VirtualKey;

namespace Tasklabe.App.Controls;

/// <summary>
/// タスクの詳細パネル（UX 規約 UX-08〜10、UI デザイン設計書 3.4.3 節）。
/// 既存のタスクは項目ごとに確定した時点で反映し（UX-26）、作成中のタスクは「追加」で一度に作る（UX-09）。
/// 候補から選ぶ項目は、一覧やキーと同じピッカーで変える（UX-02、UX-19）。
/// 見出し（番号とプロジェクト、GitHub で開く）は、載せる引き出し（<see cref="Drawer"/>）へ渡す。
/// </summary>
public sealed partial class TaskDetailPane : UserControl
{
    private readonly TextBox _title;
    private readonly TextBox _body;
    private readonly PickerButton _projectButton = new("プロジェクト");
    private readonly PickerButton _status = new("ステータス");
    private readonly PickerButton _assignee = new("担当者");
    private readonly PickerButton _plan = new("予定");
    private readonly PickerButton _estimate = new("想定工数");
    private readonly PickerButton _kind = new("区分");
    private readonly PickerButton _progress = new("進捗率");
    private readonly PickerButton _actual = new("実績");
    private readonly PickerButton _milestone = new("マイルストーン");
    private readonly FrameworkElement _milestoneRow;
    private readonly FrameworkElement _assigneeRow;
    private readonly FrameworkElement _kindRow;
    private readonly FrameworkElement _detailOnly;
    private readonly FrameworkElement _actualRows;
    private readonly FrameworkElement _progressRow;
    private readonly StackPanel _predecessorPanel = new() { Spacing = 4, Visibility = Visibility.Collapsed };
    private readonly StackPanel _predecessorList = new() { Spacing = 2 };
    private readonly StackPanel _inheritedList = new() { Spacing = 2, Visibility = Visibility.Collapsed };
    private readonly Button _chainChildren;
    private readonly CheckBox _nonBlocking = new() { Content = "後続を待たせない" };
    private readonly StackPanel _subtaskPanel = new() { Spacing = 4, Visibility = Visibility.Collapsed };
    private readonly StackPanel _subtaskList = new() { Spacing = 2 };
    private readonly TextBlock _subtaskSummary = new() { HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBox _subtaskInput = new() { PlaceholderText = "サブタスクを追加" };
    private readonly StackPanel _repositoryPanel = new() { Spacing = 4 };
    private readonly StackPanel _repositoryList = new() { Spacing = 2 };
    private Button? _addRepository;
    private readonly Button _openButton = new();

    private TaskItem? _task;
    private Project? _project;
    private TaskDraft? _draft;
    private bool _loading;

    public TaskDetailPane()
    {
        InitializeComponent();

        // 開いたときはパネルそのものにフォーカスを置く（↑↓ で前後のタスクへ移り、Tab で項目へ入る）
        IsTabStop = true;
        UseSystemFocusVisuals = false;

        _openButton.Style = AppResources.Style("Drawer.Button");
        _openButton.Content = new FontIcon { FontSize = 14, Glyph = "" };
        AutomationProperties.SetName(_openButton, "GitHub で開く");
        _openButton.Click += OnOpenInBrowser;

        var caption = AppResources.Style("Text.Caption");
        _title = new TextBox
        {
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            PlaceholderText = "タスク名",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Resources["FlatTextBox"],
        };
        AutomationProperties.SetName(_title, "タスク名");
        _title.KeyDown += OnTitleKeyDown;
        _title.LostFocus += OnTitleLostFocus;
        _title.TextChanged += (_, _) =>
        {
            if (_draft is not null)
            {
                _draft.Title = _title.Text;
                UpdateCreateState();
            }
        };

        _body = new TextBox
        {
            AcceptsReturn = true,
            MinHeight = 80,
            PlaceholderText = "説明を追加…（Markdown）",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Resources["FlatTextBox"],
        };
        AutomationProperties.SetName(_body, "説明");
        _body.LostFocus += OnBodyLostFocus;
        _body.TextChanged += (_, _) =>
        {
            if (_draft is not null)
            {
                _draft.Body = _body.Text;
            }
        };

        // 追加のダイアログと同じ項目を同じ順に置く（UX-10）
        Fields.Children.Add(Row("プロジェクト", _projectButton));
        Fields.Children.Add(_title);
        Fields.Children.Add(_body);
        Fields.Children.Add(Row("ステータス", _status));
        _assigneeRow = Row("担当者", _assignee);
        Fields.Children.Add(_assigneeRow);
        Fields.Children.Add(Row("予定", _plan));
        Fields.Children.Add(Row("想定工数", _estimate));
        _kindRow = Row("区分", _kind);
        Fields.Children.Add(_kindRow);

        // ここから詳細パネルにしかない項目
        _progressRow = Row("進捗率", _progress);
        _actualRows = new StackPanel
        {
            Spacing = 12,
            Children = { Row("実績", _actual) },
        };

        var chainText = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        chainText.Children.Add(new FontIcon { FontSize = 12, Glyph = "" });
        chainText.Children.Add(new TextBlock { Text = "配下を予定の順につなぐ…" });
        _chainChildren = new Button { Content = chainText, Style = AppResources.Style("SubtleButtonStyle"), Visibility = Visibility.Collapsed };
        ToolTipService.SetToolTip(_chainChildren, "予定期間が重なるタスクは並列とみなし、互いにはつなぎません");
        _chainChildren.Click += async (_, _) =>
        {
            if (_task is { } task)
            {
                await TaskCommands.ChainChildrenAsync(XamlRoot, task);
            }
        };

        var addText = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        addText.Children.Add(new FontIcon { FontSize = 12, Glyph = "" });
        addText.Children.Add(new TextBlock { Text = "先行タスクを追加…" });
        var addPredecessor = PickerTriggerButton(addText);
        ToolTipService.SetToolTip(addPredecessor, KeyHints.Tip("task.predecessor", "先行タスクを追加"));
        addPredecessor.Click += async (_, _) =>
        {
            if (_task is { } task)
            {
                await TaskShortcuts.RunAsync("task.predecessor", new TaskTarget(task, addPredecessor));
            }
        };

        ToolTipService.SetToolTip(_nonBlocking, "このタスクの終わりを待たずに、後続（親の後続を含む）を始められるようにします。親タスクの最後の振り返りなどに使います");
        _nonBlocking.Click += async (_, _) =>
        {
            if (_task is { } task)
            {
                await App.Current.Services.Edits.SetNonBlockingAsync(task, _nonBlocking.IsChecked == true);
            }
        };

        var predecessorHeader = new Grid();
        predecessorHeader.Children.Add(new TextBlock { Text = "先行タスク", Style = caption });
        predecessorHeader.Children.Add(new TextBlock { Text = "終わってから始める", Style = caption, HorizontalAlignment = HorizontalAlignment.Right });
        _predecessorPanel.Children.Add(predecessorHeader);
        _predecessorPanel.Children.Add(_predecessorList);
        _predecessorPanel.Children.Add(_inheritedList);
        _predecessorPanel.Children.Add(addPredecessor);
        _predecessorPanel.Children.Add(_chainChildren);
        _predecessorPanel.Children.Add(_nonBlocking);

        // サブタスク（要件 F-TSK-04）。開いたまま分解できるよう、名前を書いて Enter で足す
        var subtaskHeader = new Grid();
        subtaskHeader.Children.Add(new TextBlock { Text = "サブタスク", Style = caption });
        _subtaskSummary.Style = caption;
        subtaskHeader.Children.Add(_subtaskSummary);
        AutomationProperties.SetName(_subtaskInput, "サブタスクを追加");
        _subtaskInput.KeyDown += OnSubtaskInputKeyDown;
        _subtaskPanel.Children.Add(subtaskHeader);
        _subtaskPanel.Children.Add(_subtaskList);
        _subtaskPanel.Children.Add(_subtaskInput);

        // 作業するリポジトリと、Issue に紐づくブランチ（要件 F-TSK-18、19）
        var repositoryHeader = new Grid();
        repositoryHeader.Children.Add(new TextBlock { Text = "作業するリポジトリ", Style = caption });
        repositoryHeader.Children.Add(new TextBlock { Text = "ブランチ", Style = caption, HorizontalAlignment = HorizontalAlignment.Right });
        var addRepositoryText = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        addRepositoryText.Children.Add(new FontIcon { FontSize = 12, Glyph = "\uE710" });
        addRepositoryText.Children.Add(new TextBlock { Text = "リポジトリを選ぶ…" });
        var addRepository = PickerTriggerButton(addRepositoryText);
        _addRepository = addRepository;
        ToolTipService.SetToolTip(addRepository, KeyHints.Tip("task.repository", "作業するリポジトリを選ぶ"));
        addRepository.Click += async (_, _) =>
        {
            if (_task is { } task)
            {
                await TaskShortcuts.RunAsync("task.repository", new TaskTarget(task, addRepository));
            }
        };
        _repositoryPanel.Children.Add(repositoryHeader);
        _repositoryPanel.Children.Add(_repositoryList);
        _repositoryPanel.Children.Add(addRepository);

        _milestoneRow = Row("マイルストーン", _milestone);
        _detailOnly = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new Border
                {
                    BorderBrush = ThemeResources.Brush("DividerStrokeColorDefaultBrush"),
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    Padding = new Thickness(0, 8, 0, 0),
                    Child = new TextBlock { Text = "詳細", Style = caption },
                },
                _progressRow,
                _milestoneRow,
                _actualRows,
                _subtaskPanel,
                _repositoryPanel,
                _predecessorPanel,
            },
        };
        Fields.Children.Add(_detailOnly);

        _projectButton.Pick = PickProjectAsync;
        _status.Pick = b => PickAsync(b, "task.status");
        _assignee.Pick = b => PickAsync(b, "task.assign");
        _plan.Pick = b => PickRangeAsync(b, plan: true);
        _estimate.Pick = b => PickAsync(b, "task.estimate");
        _kind.Pick = PickKindAsync;
        _progress.Pick = b => PickAsync(b, "task.progress");
        _actual.Pick = b => PickRangeAsync(b, plan: false);
        _milestone.Pick = PickMilestoneAsync;

        foreach (var (button, id) in ((PickerButton, string)[])
            [(_projectButton, "composer.project"), (_status, "composer.status"), (_assignee, "composer.assign"),
             (_plan, "composer.due"), (_estimate, "composer.estimate"), (_kind, "composer.kind")])
        {
            ToolTipService.SetToolTip(button, KeyHints.Tip(id, AutomationProperties.GetName(button).Split(':')[0]));
        }

        ToolTipService.SetToolTip(_openButton, KeyHints.Tip("task.openInBrowser", "GitHub で開く"));
        AddHandler(PreviewKeyDownEvent, new KeyEventHandler(OnPanePreviewKeyDown), true);
    }

    /// <summary>
    /// 一覧に足すピッカーを開くボタン（「先行タスクを追加…」など）。ピッカーのボタンの形として扱い、上端の欄をこのボタンと同じ見た目・
    /// 同じ位置にする。選んだ候補はボタンの値にしない。ピッカーの幅はボタンの幅になるため、パネルの幅いっぱいに広げる。
    /// </summary>
    private static Button PickerTriggerButton(FrameworkElement content)
    {
        var button = new Button
        {
            Content = content,
            Style = AppResources.Style("SubtleButtonStyle"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        PickerTrigger.SetIsTrigger(button, true);
        PickerTrigger.SetShowsChoice(button, false);
        return button;
    }

    private static Grid Row(string label, FrameworkElement control)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock
        {
            Text = label,
            Style = AppResources.Style("Text.Caption"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    /// <summary>表示しているタスク（作成中は null）。</summary>
    // XAML の型情報生成に載せないため internal にする（TaskItem は required メンバーを持つ）
    internal TaskItem? CurrentTask => _task;

    /// <summary>見出しの操作（GitHub で開く）。引き出しの見出しの閉じるボタンの左に置く。</summary>
    public UIElement HeaderActions => _openButton;

    /// <summary>見出しの名前（番号。作成中は「新しいタスク」）。</summary>
    public string HeaderTitle { get; private set; } = "";

    /// <summary>見出しの補足（プロジェクト名）。</summary>
    public string HeaderDescription { get; private set; } = "";

    /// <summary>見出しの名前か補足が変わった。</summary>
    public event EventHandler? HeaderChanged;

    /// <summary>↑↓ で、一覧の前（-1）か後（1）のタスクへ移るよう求めた。</summary>
    public event EventHandler<int>? StepRequested;

    private void SetHeader(string title, string description)
    {
        if (title == HeaderTitle && description == HeaderDescription)
        {
            return;
        }

        HeaderTitle = title;
        HeaderDescription = description;
        HeaderChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>タイトルの入力欄にフォーカスし、全体を選ぶ（ショートカットのタイトル変更）。</summary>
    public void FocusTitle()
    {
        _title.Focus(FocusState.Keyboard);
        _title.SelectAll();
    }

    // ---------------------------------------------------------------- 既存のタスク

    /// <summary>表示するタスクを設定する。同じタスクの更新では、編集中の項目を上書きしない。</summary>
    public async Task ShowAsync(TaskItem? task)
    {
        bool sameTask = task is not null && _task?.ItemId == task.ItemId;
        _task = task;
        if (task is null)
        {
            return;
        }

        _draft = null;
        var services = App.Current.Services;
        var project = await services.Store.GetProjectAsync(task.ProjectId);
        _project = project;

        _loading = true;
        try
        {
            var projectName = project is null ? "" : ProjectDisplay.Name(project);
            SetHeader(task.IsLocal ? "GitHub へ送信待ち" : TaskKeys.Of(task), projectName);
            _openButton.Visibility = Visibility.Visible;
            _openButton.IsEnabled = task.Url is not null;
            if (!sameTask)
            {
                // 前後のタスクへ移ったときは、上から読めるようにする
                Scroller.ChangeView(null, 0, null, disableAnimation: true);
            }
            ExistingFooter.Visibility = Visibility.Visible;
            CreateFooter.Visibility = Visibility.Collapsed;

            var focused = FocusManager.GetFocusedElement(XamlRoot);
            if (!sameTask || !ReferenceEquals(focused, _title))
            {
                _title.Text = task.Title;
            }

            if (!sameTask || !ReferenceEquals(focused, _body))
            {
                _body.Text = task.Body ?? "";
            }

            bool team = project?.IsTeam == true;
            bool planned = team && task.IsPlanned;
            _projectButton.SetValue(projectName, ProjectGlyph(project));
            _projectButton.IsEnabled = !task.IsLocal;
            _status.SetValue(task.StatusName ?? StatusVisuals.Name(task.Category), StatusVisuals.Glyph(task.Category), StatusVisuals.BrushKey(task.Category));
            _assigneeRow.Visibility = team ? Visibility.Visible : Visibility.Collapsed;
            _assignee.SetValue(string.Join("、", task.Assignees.Select(a => "@" + a)), "");
            _plan.SetValue(DateText.Range(task.Start, task.Target), "");
            _estimate.SetValue(EffortText.Of(task.EstimateHours), "");
            _kindRow.Visibility = team ? Visibility.Visible : Visibility.Collapsed;
            _kind.SetValue(KindVisuals.Name(task.Kind));
            KindVisuals.Apply(_kind.Icon, task.Kind);
            _kind.Icon.Visibility = Visibility.Visible;

            _detailOnly.Visibility = Visibility.Visible;
            _progressRow.Visibility = Visibility.Visible;
            _actualRows.Visibility = Visibility.Visible;
            _progress.SetValue($"{task.EffectiveProgress:0}%", "");
            _progress.IsEnabled = !task.IsDone;
            ToolTipService.SetToolTip(_progress, task.IsDone ? "完了・中止したタスクの進捗率は 100 % に固定しています" : KeyHints.Tip("task.progress", "進捗率を変える"));
            _actual.SetValue(DateText.Range(task.ActualStart, task.ActualEnd), "");
            ShowMilestone(task, project);

            // 計画への出し入れは、計画を持つチームプロジェクトでだけ意味を持つ
            PlanButton.Visibility = team ? Visibility.Visible : Visibility.Collapsed;
            PlanButtonText.Text = task.Kind == TaskKind.Issue ? "計画に移す…" : "課題へ戻す";
            PlanButtonIcon.Glyph = task.Kind == TaskKind.Issue ? "" : "";
            AutomationProperties.SetName(PlanButton, PlanButtonText.Text);
            ToolTipService.SetToolTip(PlanButton, KeyHints.Tip("task.plan", PlanButtonText.Text.TrimEnd('…')));
            ToolTipService.SetToolTip(DeleteButton, KeyHints.Tip("task.delete", "削除"));

            // サブタスクは計画のタスク（個人ではすべてのタスク）だけが持つ
            _subtaskPanel.Visibility = TaskShortcuts.CanHaveChildren(task) ? Visibility.Visible : Visibility.Collapsed;
            if (TaskShortcuts.CanHaveChildren(task))
            {
                await BuildSubtasksAsync(task);
            }

            _repositoryPanel.Visibility = Visibility.Visible;
            BuildRepositories(task, LinkedBranches.Cached(task.IssueId));
            if (!sameTask && !task.IsLocal)
            {
                _ = RefreshBranchesAsync(task);
            }

            // 先行タスクは計画のタスクだけが持つ（要件 F-DEP-01）
            _predecessorPanel.Visibility = planned ? Visibility.Visible : Visibility.Collapsed;
            if (planned)
            {
                await BuildPredecessorsAsync(task);
            }

            HintText.Text = "↑↓ で前後のタスク　Esc で閉じる　Alt + S などで項目を選ぶ";
            HintText.Visibility = Hints.Shown ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _loading = false;
        }
    }

    private static string ProjectGlyph(Project? project) => project?.Kind switch
    {
        ProjectKind.Inbox => "",
        ProjectKind.Team => "",
        _ => "",
    };

    private async Task PickAsync(PickerButton button, string actionId)
    {
        if (_draft is not null)
        {
            await PickDraftAsync(button, actionId);
        }
        else if (_task is { } task)
        {
            await TaskShortcuts.RunAsync(actionId, new TaskTarget(task, button));
        }
    }

    /// <summary>予定（開始日〜期日）か実績（開始日〜終了日）を、日付範囲のピッカーでまとめて選ぶ。</summary>
    private async Task PickRangeAsync(PickerButton button, bool plan)
    {
        if (_draft is { } draft)
        {
            if (await DateRangePicker.ShowAsync(button, "予定", draft.Start, draft.Target, ValuePickers.PlanPresets) is { } picked)
            {
                draft.Start = picked.Start;
                draft.Target = picked.End;
                ShowDraftValues();
            }

            return;
        }

        if (_task is not { } task)
        {
            return;
        }

        var (startField, endField) = plan ? (TaskField.Start, TaskField.Target) : (TaskField.ActualStart, TaskField.ActualEnd);
        var (start, end) = plan ? (task.Start, task.Target) : (task.ActualStart, task.ActualEnd);
        if (await DateRangePicker.ShowAsync(button, plan ? "予定" : "実績", start, end, plan ? ValuePickers.PlanPresets : ValuePickers.ActualPresets) is { } range)
        {
            var changes = TaskRules.Set(task, startField, TaskValues.Date(range.Start))
                .Concat(TaskRules.Set(task, endField, TaskValues.Date(range.End)))
                .ToList();
            if (changes.Count > 0)
            {
                await App.Current.Services.Edits.ApplyChangesAsync(task, changes);
            }
        }
    }

    private async Task PickProjectAsync(PickerButton button)
    {
        if (_draft is { } draft)
        {
            if (await ValuePickers.ProjectAsync(button, null, "追加先", draft.Project is { } p ? [p.Id] : [], excludeCurrent: false) is { } picked)
            {
                SetDraftProject(draft, picked.Value, keepValues: true);
                ShowDraftValues();
            }

            return;
        }

        if (_task is { } task
            && await ValuePickers.ProjectAsync(button, null, "別のプロジェクトへ移す", [task.ProjectId], excludeCurrent: true) is { } target)
        {
            var moved = await TaskCommands.MoveAsync([task], target.Value);
            if (moved.Count > 0)
            {
                App.Current.Shell?.SelectTask(moved[0]);
            }
        }
    }

    private async Task PickKindAsync(PickerButton button)
    {
        if (_draft is { } draft)
        {
            if (await ValuePickers.KindAsync(button, null, [draft.Kind]) is { } picked)
            {
                draft.Kind = picked.Value;
                ShowDraftValues();
            }

            return;
        }

        if (_task is not { } task || await ValuePickers.KindAsync(button, null, [task.Kind]) is not { } kind || kind.Value == task.Kind)
        {
            return;
        }

        // 課題を計画へ入れるときは置き場所と日程を決める（要件 F-TSK-09。「計画に移す」と同じ道筋）
        if (task.Kind == TaskKind.Issue && kind.Value == TaskKind.Task)
        {
            await TaskCommands.PromoteAsync(XamlRoot, task);
        }
        else if (kind.Value == TaskKind.Issue)
        {
            await TaskCommands.DemoteAsync(task);
        }
        else
        {
            await App.Current.Services.Edits.SetAsync(task, TaskField.Kind, ProjectConventions.KindOption(kind.Value));
        }
    }

    private async Task SetAsync(TaskField field, string? value)
    {
        if (_loading || _task is null)
        {
            return;
        }

        if (await App.Current.Services.Edits.SetAsync(_task, field, value) is { } updated)
        {
            _task = updated;
        }
    }

    private async void OnTitleLostFocus(object sender, RoutedEventArgs e)
    {
        if (_draft is not null)
        {
            return;
        }

        var title = _title.Text.Trim();
        if (title.Length == 0 && _task is not null)
        {
            _title.Text = _task.Title; // タイトルは空にできない
            return;
        }

        await SetAsync(TaskField.Title, title);
    }

    private async void OnTitleKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            if (_draft is null)
            {
                await SetAsync(TaskField.Title, _title.Text.Trim() is { Length: > 0 } t ? t : _task?.Title);
            }
            else
            {
                _body.Focus(FocusState.Keyboard);
            }
        }
    }

    private async void OnBodyLostFocus(object sender, RoutedEventArgs e)
    {
        if (_draft is null)
        {
            await SetAsync(TaskField.Body, _body.Text);
        }
    }

    private async void OnTogglePlan(object sender, RoutedEventArgs e)
    {
        if (_task is { } task)
        {
            await TaskShortcuts.RunAsync("task.plan", new TaskTarget(task, PlanButton));
        }
    }

    private void OnOpenInBrowser(object sender, RoutedEventArgs e)
    {
        if (_task?.Url is { } url)
        {
            Browser.Open(url);
        }
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_task is not null)
        {
            await TaskCommands.ConfirmAndDeleteAsync(XamlRoot, _task, DeleteButton);
        }
    }

    // ---------------------------------------------------------------- 作成中のタスク（UX 規約 UX-09）

    /// <summary>下書きを表示し、作成の続きを受け付ける。</summary>
    public void ShowDraft(TaskDraft draft)
    {
        _draft = draft;
        _task = null;
        _project = draft.Project;
        SetHeader("新しいタスク", "まだ追加していません");
        _openButton.Visibility = Visibility.Collapsed;
        ExistingFooter.Visibility = Visibility.Collapsed;
        CreateFooter.Visibility = Visibility.Visible;
        _projectButton.IsEnabled = true;
        _title.Text = draft.Title;
        _body.Text = draft.Body;
        ShowDraftValues();

        var submit = App.Current.Services.Keymap.Display("composer.submit");
        HintText.Text = $"{submit} で追加　Esc で閉じる　Alt + S などで項目を選ぶ";
        HintText.Visibility = Hints.Shown ? Visibility.Visible : Visibility.Collapsed;
        UpdateCreateState();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FocusDraftField);
    }

    private void ShowDraftValues()
    {
        if (_draft is not { } draft)
        {
            return;
        }

        var project = draft.Project;
        bool team = project?.IsTeam == true;
        var status = project?.StatusOptions.FirstOrDefault(o => o.Id == draft.StatusOptionId);
        _projectButton.SetValue(project is null ? null : ProjectDisplay.Name(project), ProjectGlyph(project));
        _status.SetValue(status?.Name, StatusVisuals.Glyph(status?.Category ?? StatusCategory.Todo), StatusVisuals.BrushKey(status?.Category ?? StatusCategory.Todo));
        _assigneeRow.Visibility = team ? Visibility.Visible : Visibility.Collapsed;
        _assignee.SetValue(string.Join("、", draft.Assignees.Select(a => "@" + a)), "");
        _plan.SetValue(DateText.Range(draft.Start, draft.Target), "");
        _estimate.SetValue(EffortText.Of(draft.EstimateHours), "");
        _kindRow.Visibility = team ? Visibility.Visible : Visibility.Collapsed;
        _kind.SetValue(KindVisuals.Name(draft.Kind));
        KindVisuals.Apply(_kind.Icon, draft.Kind);
        _kind.Icon.Visibility = Visibility.Visible;

        // 作成前は詳細の欄を出さない（予定は上の欄で決め、進捗・実績・先行は作ってから決める）
        _detailOnly.Visibility = Visibility.Collapsed;
        _progressRow.Visibility = Visibility.Collapsed;
        _actualRows.Visibility = Visibility.Collapsed;
        _predecessorPanel.Visibility = Visibility.Collapsed;
        _repositoryPanel.Visibility = Visibility.Collapsed;
        _milestoneRow.Visibility = Visibility.Collapsed;
    }

    /// <summary>追加先を変えたときに、そのプロジェクトで選べない値を既定に戻す。</summary>
    internal static void SetDraftProject(TaskDraft draft, Project project, bool keepValues)
    {
        var previous = draft.Project;
        draft.Project = project;

        // 同じ名前のステータスがあれば引き継ぎ、なければ Todo の代表にする
        var status = previous?.StatusOptions.FirstOrDefault(o => o.Id == draft.StatusOptionId);
        draft.StatusOptionId = (keepValues && status is not null
                ? new StatusChoice(status.Name, status.Category).In(project)
                : null)?.Id
            ?? ProjectConventions.DefaultStatus(project.StatusOptions, StatusCategory.Todo)?.Id;
        if (!project.IsTeam || previous?.RepositoryNameWithOwner != project.RepositoryNameWithOwner)
        {
            draft.Assignees.Clear();
        }

        if (!project.IsTeam || previous?.Id != project.Id)
        {
            draft.ParentIssueId = null;
            draft.Kind = ProjectPreferences.Resolve(project).NewTaskKind;
        }
    }

    private async Task PickDraftAsync(PickerButton button, string actionId)
    {
        if (_draft is not { Project: { } project } draft)
        {
            return;
        }

        switch (actionId)
        {
            case "task.status":
                var current = project.StatusOptions.FirstOrDefault(o => o.Id == draft.StatusOptionId);
                if (await ValuePickers.StatusAsync(button, null, [project], [(current?.Name, current?.Category ?? StatusCategory.Todo)]) is { } status)
                {
                    draft.StatusOptionId = status.Value.In(project)?.Id;
                }

                break;
            case "task.assign" when project.IsTeam:
                if (await ValuePickers.AssigneesAsync(button, null, [project.RepositoryNameWithOwner], [draft.Assignees]) is { } choice)
                {
                    var next = choice.ApplyTo(draft.Assignees);
                    draft.Assignees.Clear();
                    draft.Assignees.AddRange(next);
                }

                break;
            case "task.estimate":
                if (await ValuePickers.EstimateAsync(button, null, [draft.EstimateHours]) is { } estimate)
                {
                    draft.EstimateHours = estimate.Value;
                }

                break;
        }

        ShowDraftValues();
    }

    private void UpdateCreateState()
    {
        bool can = _draft?.CanCreate == true;
        CreateButton.IsEnabled = can;
        ToolTipService.SetToolTip(CreateButton, can
            ? KeyHints.Tip("composer.submit", "追加")
            : "タスク名を入力すると追加できます");
    }

    private void FocusDraftField()
    {
        Control target = _draft?.FocusField switch
        {
            "body" => _body,
            "project" => _projectButton,
            "status" => _status,
            "assign" => _assignee,
            "due" => _plan,
            "estimate" => _estimate,
            "kind" => _kind,
            _ => _title,
        };
        target.Focus(FocusState.Keyboard);
    }

    private async void OnCreate(object sender, RoutedEventArgs e) => await CreateAsync();

    /// <summary>下書きからタスクを作り、作ったタスクの詳細に切り替える。</summary>
    private async Task CreateAsync()
    {
        if (_draft is not { CanCreate: true } draft)
        {
            _title.Focus(FocusState.Keyboard);
            return;
        }

        _draft = null;
        var created = await draft.CreateAsync();
        App.Current.Shell?.SelectTask(created);
        App.Current.Shell?.ShowToast($"「{created.Title}」を追加しました");
    }

    private async void OnDiscard(object sender, RoutedEventArgs e) => await App.Current.MainWindow!.CloseDetailAsync();

    /// <summary>
    /// 閉じてよいかを確かめる。作成中で入力があれば、破棄してよいかを確かめる（UX 規約 UX-11、UX-14）。
    /// </summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (_draft is not { HasContent: true } draft)
        {
            return true;
        }

        var dialog = AppDialog.Confirm(XamlRoot, "入力中のタスクを破棄しますか？",
            $"「{(draft.Title.Trim().Length > 0 ? draft.Title.Trim() : "（タスク名なし）")}」はまだ追加していません。破棄すると入力した内容は戻せません。", "破棄");
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    // ---------------------------------------------------------------- キーボード（UX 規約 UX-13）

    /// <summary>
    /// パネルの中では、追加のダイアログと同じ Alt + 文字で項目を選び、Ctrl + Enter で追加する。
    /// 文字を入力していないときの ↑↓ は、一覧の前後のタスクへ移る（作成中は移らない）。
    /// </summary>
    private async void OnPanePreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || KeyInput.From(e.Key) is not { } gesture)
        {
            return;
        }

        if (gesture is { Modifiers: KeyModifiers.None, Key: "Up" or "Down" })
        {
            if (_draft is null && _task is not null && !KeyInput.IsTextInput(FocusManager.GetFocusedElement(XamlRoot)))
            {
                e.Handled = true;
                StepRequested?.Invoke(this, gesture.Key == "Down" ? 1 : -1);
            }

            return;
        }

        if (App.Current.Services.Keymap.Find(gesture, ShortcutScope.Composer) is not { } action)
        {
            return;
        }

        PickerButton? button = action.Id switch
        {
            "composer.project" => _projectButton,
            "composer.status" => _status,
            "composer.assign" => _assignee,
            "composer.due" => _plan,
            "composer.estimate" => _estimate,
            "composer.kind" => _kind,
            _ => null,
        };

        if (action.Id == "composer.submit" && _draft is not null)
        {
            e.Handled = true;
            await CreateAsync();
        }
        else if (button is { IsEnabled: true, Pick: { } pick } && IsShown(button))
        {
            e.Handled = true;
            button.Focus(FocusState.Keyboard);
            await pick(button);
        }
    }

    /// <summary>
    /// キーの操作（S・P・A・E・Shift + P）で開くピッカーの起点にする、その値の項目のボタン。表示していない項目や、
    /// 項目のボタンと違う形の値を選ぶ操作（D は 1 日を選ぶが、項目は予定の範囲）では null（パネルから開く）。
    /// </summary>
    internal FrameworkElement? FieldFor(string actionId)
    {
        Control? button = actionId switch
        {
            "task.status" => _status,
            "task.progress" => _progress,
            "task.assign" => _assignee,
            "task.estimate" => _estimate,
            "task.project" => _projectButton,
            "task.repository" => _addRepository,
            _ => null,
        };
        return button is { IsEnabled: true } && IsShown(button) ? button : null;
    }

    private static bool IsShown(FrameworkElement element) =>
        !VisualTree.AncestorsAndSelf(element).Any(e => e is UIElement { Visibility: Visibility.Collapsed });

    // ---------------------------------------------------------------- サブタスク（要件 F-TSK-04）

    /// <summary>サブタスクの入力欄にフォーカスを置く（右クリックのメニューの「子タスクを追加」から）。</summary>
    public void FocusSubtaskInput()
    {
        if (_subtaskPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        // メニューが閉じてフォーカスが元の画面へ戻った後に移す
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _subtaskInput.StartBringIntoView();
            _subtaskInput.Focus(FocusState.Keyboard);
        });
    }

    /// <summary>子を並び順に並べ、完了した数を見出しの右に示す。名前を押すと、その子の詳細を開く。</summary>
    private async Task BuildSubtasksAsync(TaskItem task)
    {
        var children = (await App.Current.Services.Store.GetTasksAsync(task.ProjectId))
            .Where(t => t.ParentIssueId == task.IssueId)
            .OrderBy(t => t.SortOrder)
            .ToList();
        var caption = AppResources.Style("Text.Caption");
        _subtaskList.Children.Clear();
        foreach (var child in children)
        {
            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new StatusIcon { Category = child.Category, Progress = child.EffectiveProgress, VerticalAlignment = VerticalAlignment.Center });

            var open = new HyperlinkButton
            {
                Content = new TextBlock { Text = child.Title, TextTrimming = TextTrimming.CharacterEllipsis },
                Padding = new Thickness(4, 2, 4, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            AutomationProperties.SetName(open, $"サブタスク「{child.Title}」を開く");
            open.Click += (_, _) => App.Current.Shell?.SelectTask(child);
            Grid.SetColumn(open, 1);
            grid.Children.Add(open);

            var due = new TextBlock { Text = child.Target is { } t ? DateText.Short(t) : "", Style = caption, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(due, 2);
            grid.Children.Add(due);
            _subtaskList.Children.Add(grid);
        }

        _subtaskSummary.Text = children.Count == 0 ? "" : $"完了 {children.Count(c => c.IsCompleted)} / {children.Count}";
    }

    private async void OnSubtaskInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || _task is not { } parent || _project is not { } project)
        {
            return;
        }

        e.Handled = true;
        var title = _subtaskInput.Text.Trim();
        if (title.Length == 0)
        {
            return;
        }

        // 末尾の子の後ろに、計画のタスクとして足す。担当者は親から引き継がない（子ごとに決める）
        var tasks = await App.Current.Services.Store.GetTasksAsync(parent.ProjectId);
        double order = tasks.Where(t => t.ParentIssueId == parent.IssueId).Select(t => t.SortOrder).DefaultIfEmpty(0).Max() + 1024;
        _subtaskInput.Text = "";
        await App.Current.Services.Edits.CreateAsync(project, title, parentIssueId: parent.IssueId, sortOrder: order, kind: TaskKind.Task);
        if (_task?.ItemId == parent.ItemId)
        {
            await BuildSubtasksAsync(parent);
        }
    }

    // ---------------------------------------------------------------- 先行タスク（要件 F-DEP-01、02）

    private async Task BuildPredecessorsAsync(TaskItem task)
    {
        var tasks = await App.Current.Services.Store.GetTasksAsync(task.ProjectId);
        var byIssue = tasks.GroupBy(t => t.IssueId).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var caption = AppResources.Style("Text.Caption");

        _predecessorList.Children.Clear();
        foreach (var id in task.BlockedBy)
        {
            var pred = byIssue.GetValueOrDefault(id);
            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new TextBlock
            {
                Text = pred?.Title ?? "（このプロジェクトの計画にないタスク）",
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            grid.Children.Add(title);

            var end = new TextBlock
            {
                Text = pred?.Target is { } t ? $"〜{DateText.Short(t)}" : "",
                Style = caption,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(end, 1);
            grid.Children.Add(end);

            var remove = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 12 },
                Style = AppResources.Style("SubtleButtonStyle"),
                Padding = new Thickness(6),
            };
            AutomationProperties.SetName(remove, $"先行タスク「{title.Text}」を外す");
            ToolTipService.SetToolTip(remove, "外す");
            remove.Click += async (_, _) =>
            {
                if (_task is { } current)
                {
                    await App.Current.Services.Edits.SetBlockedByAsync(current, current.BlockedBy.Where(b => b != id));
                }
            };
            Grid.SetColumn(remove, 2);
            grid.Children.Add(remove);
            _predecessorList.Children.Add(grid);
        }

        // 親に張った依存関係は配下にも効くため、どこから受け継いでいるかを示す
        var tree = TaskTree.Build(tasks.Where(t => t.IsPlanned));
        var inherited = DependencyPlanner.InheritedPredecessors(tree, task);
        _inheritedList.Children.Clear();
        foreach (var (ancestor, pred) in inherited)
        {
            _inheritedList.Children.Add(new TextBlock
            {
                Text = $"{pred.Title}（親「{ancestor.Title}」から受け継ぐ）",
                Style = caption,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        _inheritedList.Visibility = inherited.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (task.BlockedBy.Count == 0 && inherited.Count == 0)
        {
            _predecessorList.Children.Add(new TextBlock { Text = "—", Style = caption });
        }

        _nonBlocking.IsChecked = task.NonBlocking;
        _chainChildren.Visibility = tree.All().Any(n => n.Task.ParentIssueId == task.IssueId)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- 作業するリポジトリ（要件 F-TSK-18、19）

    /// <summary>Issue に紐づくブランチを取り直し、同じタスクを表示しているあいだなら描き直す。</summary>
    private async Task RefreshBranchesAsync(TaskItem task)
    {
        var branches = await LinkedBranches.FetchAsync(task.IssueId);
        if (branches is not null && _task is { } current && current.ItemId == task.ItemId)
        {
            BuildRepositories(current, branches);
        }
    }

    /// <summary>
    /// 作業するリポジトリを並べ、その下に、Issue に紐づくブランチと選んだ既存のブランチを添える（要件 F-TSK-20）。
    /// ブランチだけがあるリポジトリも、選んでいなくても並べる（どこで作業しているかを示すため）。
    /// </summary>
    private void BuildRepositories(TaskItem task, IReadOnlyList<Tasklabe.GitHub.LinkedBranch>? linked)
    {
        // 選んだブランチは Issue に紐づかないため、URL はここで組み立てる。紐づいたものと重なれば紐づいたほうを使う
        var hostName = App.Current.Services.Host.Name;
        var chosen = task.Branches.Select(TaskValues.SplitBranchEntry)
            .Where(c => !(linked ?? []).Any(l => l.Name == c.Branch && string.Equals(l.RepositoryNameWithOwner, c.Repository, StringComparison.OrdinalIgnoreCase)))
            .Select(c => (Branch: new Tasklabe.GitHub.LinkedBranch(c.Repository, c.Branch, $"https://{hostName}/{c.Repository}/tree/{c.Branch}"), Chosen: true));
        var branches = (linked ?? []).Select(b => (Branch: b, Chosen: false)).Concat(chosen).ToList();

        var caption = AppResources.Style("Text.Caption");
        var subtle = AppResources.Style("SubtleButtonStyle");
        var host = App.Current.Services.Host.Name;
        var repositories = task.Repositories
            .Concat(branches.Select(b => b.Branch.RepositoryNameWithOwner))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _repositoryList.Children.Clear();
        foreach (var repository in repositories)
        {
            bool associated = task.Repositories.Contains(repository, StringComparer.OrdinalIgnoreCase);
            var grid = new Grid { ColumnSpacing = 4 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            name.Children.Add(new FontIcon { Glyph = "", FontSize = 12 });
            name.Children.Add(new TextBlock { Text = repository, TextTrimming = TextTrimming.CharacterEllipsis });
            var link = new HyperlinkButton { Content = name, NavigateUri = new Uri($"https://{host}/{repository}"), Padding = new Thickness(4, 2, 4, 2) };
            AutomationProperties.SetName(link, $"{repository} を GitHub で開く");
            ToolTipService.SetToolTip(link, associated ? "GitHub で開く" : "ブランチだけがあるリポジトリです（作業するリポジトリには選んでいません）");
            grid.Children.Add(link);

            var branch = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Style = subtle, Padding = new Thickness(6), IsEnabled = !task.IsLocal };
            AutomationProperties.SetName(branch, $"{repository} のブランチを作る・選ぶ");
            ToolTipService.SetToolTip(branch, KeyHints.Tip("task.branch", "ブランチを作る・選ぶ"));

            // パネルの右端はウィンドウの右端にあたるため、行の右端のボタンのツールチップは左へ出し、画面の外で切れないようにする
            ToolTipService.SetPlacement(branch, Microsoft.UI.Xaml.Controls.Primitives.PlacementMode.Left);
            branch.Click += async (_, _) =>
            {
                if (_task is { } current)
                {
                    await Views.Dialogs.BranchFlow.ShowAsync(branch, null, current, repository);
                }
            };
            Grid.SetColumn(branch, 1);
            grid.Children.Add(branch);

            if (associated)
            {
                var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Style = subtle, Padding = new Thickness(6) };
                AutomationProperties.SetName(remove, $"{repository} を作業するリポジトリから外す");
                ToolTipService.SetToolTip(remove, "外す");
                ToolTipService.SetPlacement(remove, Microsoft.UI.Xaml.Controls.Primitives.PlacementMode.Left);
                remove.Click += async (_, _) =>
                {
                    if (_task is { } current)
                    {
                        await App.Current.Services.Edits.SetAsync(current, TaskField.Repositories,
                            TaskValues.Repositories(current.Repositories.Where(r => !string.Equals(r, repository, StringComparison.OrdinalIgnoreCase))));
                    }
                };
                Grid.SetColumn(remove, 2);
                grid.Children.Add(remove);
            }

            _repositoryList.Children.Add(grid);

            foreach (var (b, isChosen) in branches.Where(b => string.Equals(b.Branch.RepositoryNameWithOwner, repository, StringComparison.OrdinalIgnoreCase)))
            {
                var row = new Grid { Margin = new Thickness(20, 0, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                label.Children.Add(new FontIcon { Glyph = "\uE8D4", FontSize = 12 });
                label.Children.Add(new TextBlock { Text = b.Name, Style = caption, TextTrimming = TextTrimming.CharacterEllipsis });
                var branchLink = new HyperlinkButton { Content = label, Padding = new Thickness(4, 0, 4, 0) };
                if (b.Url is { } url)
                {
                    branchLink.NavigateUri = new Uri(url);
                }

                AutomationProperties.SetName(branchLink, $"ブランチ {b.Name} を GitHub で開く");
                ToolTipService.SetToolTip(branchLink, isChosen ? "GitHub で開く（選んだブランチ）" : "GitHub で開く（Issue に紐づくブランチ）");
                row.Children.Add(branchLink);

                // 選んだブランチは外せる。Issue に紐づくブランチは GitHub の Development で扱う
                if (isChosen)
                {
                    var entry = TaskValues.BranchEntry(b.RepositoryNameWithOwner, b.Name);
                    var unlink = new Button { Content = new FontIcon { Glyph = "\uE711", FontSize = 12 }, Style = subtle, Padding = new Thickness(6) };
                    AutomationProperties.SetName(unlink, $"ブランチ {b.Name} を外す");
                    ToolTipService.SetToolTip(unlink, "外す（ブランチは消しません）");
                    unlink.Click += async (_, _) =>
                    {
                        if (_task is { } current)
                        {
                            await App.Current.Services.Edits.SetAsync(current, TaskField.Branches,
                                TaskValues.Branches(current.Branches.Where(e => e != entry)));
                        }
                    };
                    Grid.SetColumn(unlink, 1);
                    row.Children.Add(unlink);
                }

                _repositoryList.Children.Add(row);
            }
        }

        if (repositories.Count == 0)
        {
            _repositoryList.Children.Add(new TextBlock { Text = "—", Style = caption });
        }
    }

    // ---------------------------------------------------------------- マイルストーン（要件 F-MS-02）

    /// <summary>
    /// 計画のタスクだけがマイルストーンを持つ。期日から自動で決まる値と同じものは薄い斜体で示し、
    /// 予定終了日がマイルストーンの期日より後なら（期日超え）、そのことをツールチップで示す。
    /// </summary>
    private void ShowMilestone(TaskItem task, Project? project)
    {
        bool shown = project is { IsTeam: true } && Tasklabe.Core.Milestones.MilestonePlan.Applies(task);
        _milestoneRow.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (!shown)
        {
            return;
        }

        var current = project!.Milestones.FirstOrDefault(m => m.Id == task.MilestoneId);
        bool auto = task.MilestoneId == Tasklabe.Core.Milestones.MilestonePlan.Auto(task.Target, project.Milestones)?.Id;
        _milestone.SetValue(current?.Title, "", placeholder: project.Milestones.Count == 0 ? "まだありません" : "なし", isAuto: auto);
        MilestoneVisuals.Apply(_milestone.Icon);
        _milestone.Icon.Visibility = Visibility.Visible;
        ToolTipService.SetToolTip(_milestone, current?.Due is { } due && task.Target > due && !task.IsDone
            ? $"期日超え: 予定終了日がマイルストーンの期日（{DateText.Short(due)}）より後です"
            : null);
    }

    private async Task PickMilestoneAsync(PickerButton button)
    {
        if (_task is not { } task || _project is not { } project)
        {
            return;
        }

        var ordered = Tasklabe.Core.Milestones.MilestonePlan.Ordered(project.Milestones);
        var auto = Tasklabe.Core.Milestones.MilestonePlan.Auto(task.Target, project.Milestones);
        bool isAuto = task.MilestoneId == auto?.Id;
        var choices = new List<(string Label, string? Detail, string? Id)>
        {
            ("期日から自動で決める", auto is null ? "なし" : auto.Title, auto?.Id),
        };
        choices.AddRange(ordered.Concat(project.Milestones.Where(m => m.Due is null))
            .Select(m => ((string Label, string? Detail, string? Id))("◆ " + m.Title, m.Due is { } d ? DateText.Short(d) : null, m.Id)));
        choices.Add(("なし", null, null));
        var options = choices.Select((c, i) => new PickerOption(c.Label, c.Detail,
            IsSelected: i == 0 ? isAuto : !isAuto && c.Id == task.MilestoneId)).ToList();
        var picker = new QuickPicker("マイルストーン", options,
            note: project.Milestones.Count == 0 ? "マイルストーンは、プロジェクトの「…」→「マイルストーン」で作れます。" : null);
        if (await picker.ShowAsync(button) is { Index: >= 0 } r && choices[r.Index].Id != task.MilestoneId)
        {
            await App.Current.Services.Edits.SetAsync(task, TaskField.Milestone, choices[r.Index].Id);
        }
    }
}
