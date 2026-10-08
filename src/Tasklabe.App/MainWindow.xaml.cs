using System.Collections.Specialized;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Tasklabe.App.Services;
using Tasklabe.App.ViewModels;
using Tasklabe.App.Views;
using Tasklabe.App.Views.Dialogs;
using Tasklabe.Core.Domain;
using Tasklabe.App.Controls;
using Tasklabe.Core.Keyboard;
using Microsoft.UI.Xaml.Media;
using Windows.Networking.Connectivity;

namespace Tasklabe.App;

public sealed partial class MainWindow : Window
{
    /// <summary>最小の幅。狭いときは決まった順に畳む（UX 規約 UX-25）。</summary>
    private const int MinWidth = 480;
    private const int MinHeight = 600;

    /// <summary>同期間隔（技術設計書 3.2 節）。</summary>
    private static readonly TimeSpan ActiveInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InactiveInterval = TimeSpan.FromMinutes(5);

    private readonly AppServices _services;
    private readonly DispatcherQueueTimer _syncTimer;
    /// <summary>ナビへ動的に足した項目（プロジェクトと「チーム」の見出し）。</summary>
    private readonly List<NavigationViewItemBase> _projectItems = [];
    private bool _isDialogOpen;
    private readonly TaskCompletionSource _shellShown = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MainWindow(AppServices services)
    {
        _services = services;
        ViewModel = new ShellViewModel(services, DispatcherQueue);
        InitializeComponent();
        // 最後の入力の記録は、キー操作（ピッカーを開くもの）より先に走らせる。同じ要素のハンドラーは付けた順に呼ばれる
        LastInput.Track(RootGrid);
        RootGrid.PreviewKeyDown += OnRootPreviewKeyDown;
        InitializeDetail();

        // 下端の知らせは、内容の上に重ねる（UX 規約 UX-26）。
        // 奥行き（Translation の Z）で浮かせると、押した位置が下の一覧に届いてしまうため、枠と背景で区切る
        Toast.Visibility = Visibility.Collapsed;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/Tasklabe.ico");
        OpenWithinScreen();
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = MinWidth;
            presenter.PreferredMinimumHeight = MinHeight;
        }

        _syncTimer = DispatcherQueue.CreateTimer();
        _syncTimer.Interval = ActiveInterval;
        _syncTimer.Tick += (_, _) => _ = _services.Sync.SyncAsync();
        Activated += OnWindowActivated;
        NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
        Closed += (_, _) => NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;

        ViewModel.Projects.CollectionChanged += OnProjectsChanged;
        InitializeArrange();
        ViewModel.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.SelectedTask) && ViewModel.SelectedTask is { } task)
            {
                _animateHeader = DetailDrawer.IsOpen;
                await DetailPane.ShowAsync(task);
            }
            else if (e.PropertyName == nameof(ShellViewModel.Draft) && ViewModel.Draft is { } draft)
            {
                _animateHeader = DetailDrawer.IsOpen;
                DetailPane.ShowDraft(draft);
            }
            else if (e.PropertyName == nameof(ShellViewModel.IsDetailOpen))
            {
                UpdateDetailDrawer();
            }
            else if (e.PropertyName is nameof(ShellViewModel.HasSyncProblems) or nameof(ShellViewModel.SyncProblemSeverity))
            {
                UpdateSyncButton();
            }
            else if (e.PropertyName == nameof(ShellViewModel.IsToastOpen))
            {
                // 閉じているあいだは影も残さないよう、要素ごと隠す
                Toast.Visibility = ViewModel.IsToastOpen ? Visibility.Visible : Visibility.Collapsed;
                if (ViewModel.IsToastOpen)
                {
                    KeepToastUnfocusable();
                }
                else
                {
                    // 隠すとポインターが離れた通知が来ないため、ここで解く
                    _toastPointer = _toastFocus = false;
                    ViewModel.HoldToast(false);
                }
            }
        };
        ViewModel.SyncDetailsRequested += (_, _) => OpenSyncDetails();

        // 知らせにポインターかフォーカスがあるあいだは閉じない
        Toast.PointerEntered += (_, _) => HoldToast(pointer: true);
        Toast.PointerExited += (_, _) => HoldToast(pointer: false);
        Toast.PointerCanceled += (_, _) => HoldToast(pointer: false);
        Toast.GotFocus += (_, _) => HoldToast(focus: true);
        Toast.LostFocus += (_, _) => HoldToast(focus: false);
        ViewModel.SubtaskRequested += async (_, task) =>
        {
            await DetailPane.ShowAsync(task);
            DetailPane.FocusSubtaskInput();
        };
        _services.Sync.AuthenticationFailed += (_, reason) => DispatcherQueue.TryEnqueue(() =>
        {
            AppLog.Info($"認証が無効になったため、再サインインを求めます: {reason}");
            _services.InvalidateToken();
            ShowSignIn("GitHub の認証が無効になりました。もう一度サインインしてください。この PC の送信待ちの変更は保持されています。");
        });
    }

    public ShellViewModel ViewModel { get; }

    /// <summary>サインインの後の画面（マイタスク）を表示し終えた。</summary>
    internal Task ShellShown => _shellShown.Task;

    /// <summary>いま表示している画面。</summary>
    internal object? CurrentPage => ContentFrame.Content;

    /// <summary>保存済みのセッションがあれば本体を、なければサインイン画面を表示する。</summary>
    public void Start()
    {
        if (_services.TryRestoreSession())
        {
            _ = ShowShellAsync();
        }
        else
        {
            ShowSignIn();
        }
    }

    // ---------------------------------------------------------------- サインイン

    private void ShowSignIn(string? message = null)
    {
        _syncTimer.Stop();
        ViewModel.SelectTask(null);
        var vm = new SignInViewModel(_services) { InfoText = message };
        vm.Completed += (_, _) => _ = ShowShellAsync();
        SignInHost.Content = new SignInView(vm);
        SignInHost.Visibility = Visibility.Visible;
        NavView.Visibility = Visibility.Collapsed;
        UpdateSizer();
        SyncButton.Visibility = Visibility.Collapsed;
        AppTitleBar.IsPaneToggleButtonVisible = false;
        AppTitleBar.IsBackButtonVisible = false;
    }

    private async Task ShowShellAsync()
    {
        SignInHost.Content = null;
        SignInHost.Visibility = Visibility.Collapsed;
        NavView.Visibility = Visibility.Visible;
        UpdateSizer();
        SyncButton.Visibility = Visibility.Visible;
        AppTitleBar.IsPaneToggleButtonVisible = true;
        AppTitleBar.IsBackButtonVisible = true;

        // キャッシュから即座に表示し、同期はその後で行う（要件 F-SYNC-01）
        try
        {
            await ViewModel.ReloadAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Reload", ex);
        }

        NavView.SelectedItem = MyTasksItem;
        _shellShown.TrySetResult();
        LogStartupTime();
        _syncTimer.Start();
        await _services.Sync.RefreshCountsAsync();
        await ViewModel.RefreshFailuresAsync();

        // 同期で最新の子を取り込んだあと、前から子と食い違っている親タスクのステータスも子に合わせる（要件 F-PRG-03）
        _ = Task.Run(async () =>
        {
            await _services.Sync.SyncAsync();
            await _services.Edits.ReconcileAfterSyncAsync();
        });
    }

    /// <summary>起動からマイタスクの表示までにかかった時間を記録する（要件 NF-01）。</summary>
    private static void LogStartupTime()
    {
        if (_startupLogged)
        {
            return;
        }

        _startupLogged = true;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        AppLog.Info($"起動からマイタスクの表示まで {(DateTime.Now - process.StartTime).TotalMilliseconds:0} ms");
    }

    private static bool _startupLogged;

    public async Task SignOutAsync()
    {
        _syncTimer.Stop();
        await _services.SignOutAsync();
        await ViewModel.ReloadAsync();
        ViewModel.SelectTask(null);
        ContentFrame.BackStack.Clear();
        ContentFrame.Content = null;
        ShowSignIn();
    }

    // ---------------------------------------------------------------- 同期

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        _syncTimer.Interval = args.WindowActivationState == WindowActivationState.Deactivated ? InactiveInterval : ActiveInterval;
    }

    /// <summary>
    /// インターネットにつながり直したら、次の定期の同期を待たずに送信キューを送る（要件 F-SYNC-03）。
    /// 通知は UI スレッドの外から来るため、UI スレッドへ移してから送る。サインインしていない（同期を止めている）あいだは何もしない。
    /// </summary>
    private void OnNetworkStatusChanged(object sender)
    {
        if (NetworkInformation.GetInternetConnectionProfile()?.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.InternetAccess)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_syncTimer.IsRunning)
            {
                _ = _services.Sync.ReconnectedAsync();
            }
        });
    }

    // ---------------------------------------------------------------- ショートカット（要件 F-KEY-01〜04、UI デザイン設計書 4.1 節）

    /// <summary>
    /// 一覧や表より先に受けるキー操作（全体の操作と、選んでいるタスクへの操作）。
    /// 一覧は文字キーを頭文字での移動に使うため、ショートカットはここで先に受け取る。
    /// 修飾キーのない 1 文字は、文字を入力しているところやピッカー・ダイアログが開いているあいだは使わない。
    /// </summary>
    private async void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_services.HasToken || SignInHost.Visibility == Visibility.Visible || KeyInput.From(e.Key) is not { } gesture)
        {
            return;
        }

        // 変えられない操作: Esc は一番内側の 1 層だけを閉じる（UX 規約 UX-14）。
        // ピッカーやダイアログはそれ自身が閉じるため、開いていなければ詳細パネルを閉じる
        if (gesture == new KeyGesture("Escape"))
        {
            if (CancelProjectDrag())
            {
                e.Handled = true;
                return;
            }

            if (!IsPopupOpen() && ViewModel.IsDetailOpen)
            {
                e.Handled = true;
                await CloseDetailAsync();
            }

            return;
        }

        // 変えられない操作: F6 でナビゲーションと画面のあいだを移る（UX-16）。詳細パネルを開いているあいだは、パネルの中にとどまる
        if (gesture == new KeyGesture("F6") && !IsPopupOpen())
        {
            e.Handled = true;
            ToggleFocusRegion();
            return;
        }

        // 変えられない操作: Alt + 数字でナビゲーションの n 番目へ
        if (gesture.Modifiers == KeyModifiers.Alt && KeyInput.Digit(e.Key) is { } n and <= 9)
        {
            e.Handled = !ViewModel.IsDetailOpen && GoToNavItem(n);
            return;
        }

        // 変えられない操作: 元に戻す（Ctrl + Z）・やり直す（Ctrl + Y、Ctrl + Shift + Z）。
        // 文字の入力中は入力欄の取り消しに任せ、日程の調整のプレビュー中は確定か取り消しを先に求める
        if (UndoGesture(gesture) is { } redo)
        {
            if (!IsTypingSomewhere() && !IsPopupOpen() && ContentFrame.Content is not ProjectPage { IsGanttPreviewing: true })
            {
                e.Handled = true;
                await ViewModel.UndoAsync(redo);
            }

            return;
        }

        if ((!gesture.HasCommandModifier && IsTypingSomewhere()) || IsPopupOpen())
        {
            return;
        }

        var keymap = _services.Keymap;
        if (keymap.Find(gesture, ShortcutScope.Global) is { } global)
        {
            // 詳細パネルを開いているあいだは、下の画面を変える操作（絞り込み・並び順・グループ・設定へ移る）を受けない
            if (ViewModel.IsDetailOpen && global.Id is "view.filter" or "view.sort" or "view.group" or "nav.settings")
            {
                return;
            }

            e.Handled = true;
            await RunGlobalAsync(global.Id);
            return;
        }

        if (keymap.Find(gesture, ShortcutScope.Task) is { } action)
        {
            // フォーカスが一覧の外（タイトルバーやナビゲーション）にあれば、表示中の一覧へ戻してから対象を求める。
            // 詳細パネルを開いているあいだは、フォーカスが幕の下の一覧に残っていても、パネルのタスクを対象にし、
            // ピッカーはパネルの項目から育てる（下の画面の行から開くと、幕の下に隠れる）
            var target = ViewModel.IsDetailOpen && !IsFocusWithin(DetailDrawer)
                ? DetailPane.CurrentTask is { } shown ? new TaskTarget(shown, DetailPane) : null
                : TaskShortcuts.FocusedTask(Content.XamlRoot);
            if (target is null && !ViewModel.IsDetailOpen && FocusPage())
            {
                target = TaskShortcuts.FocusedTask(Content.XamlRoot);
            }

            if (target is not null)
            {
                e.Handled = true;
                await TaskShortcuts.RunAsync(action.Id, target);
            }
        }
    }

    /// <summary>元に戻すキーなら false、やり直すキーなら true。どちらでもなければ null。</summary>
    private static bool? UndoGesture(KeyGesture gesture) => gesture switch
    {
        { Key: "Z", Modifiers: KeyModifiers.Ctrl } => false,
        { Key: "Y", Modifiers: KeyModifiers.Ctrl } or { Key: "Z", Modifiers: KeyModifiers.Ctrl | KeyModifiers.Shift } => true,
        _ => null,
    };

    /// <summary>一覧や表が処理しなかったキー（Esc、フォーカスが外れたときの ↑↓）。</summary>
    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_services.HasToken || SignInHost.Visibility == Visibility.Visible || KeyInput.From(e.Key) is not { } gesture)
        {
            return;
        }

        // ↑↓ で行を選びたいのにフォーカスが一覧の外にあるときは、一覧へ戻す（詳細パネルを開いているあいだは戻さない）
        if (gesture.Modifiers == KeyModifiers.None && gesture.Key is "Up" or "Down" && !ViewModel.IsDetailOpen && !IsFocusInPage())
        {
            e.Handled = FocusPage();
        }
    }

    /// <summary>表示中の画面の一覧へフォーカスを移す。</summary>
    private bool FocusPage()
    {
        if (ContentFrame.Content is not IKeyboardContent page)
        {
            return false;
        }

        page.FocusContent();
        return true;
    }

    private bool IsFocusInPage() => ContentFrame.Content is DependencyObject page && IsFocusWithin(page);

    /// <summary>
    /// ダイアログを閉じたら、開く前にフォーカスがあったところへ戻す（戻せなければ表示中の一覧へ）。
    /// キーボードで続けて操作できるようにする。
    /// </summary>
    private async Task WithFocusRestoredAsync(Func<Task> action)
    {
        var before = FocusManager.GetFocusedElement(Content.XamlRoot) as Control;
        await action();
        if (before?.XamlRoot is null || !before.Focus(FocusState.Programmatic))
        {
            FocusPage();
        }
    }

    private async Task RunGlobalAsync(string id)
    {
        switch (id)
        {
            case "task.new":
                await WithFocusRestoredAsync(() => AddTaskAsync());
                break;
            case "app.sync":
                // 利用者が求めた同期は、GitHub で変えたステータスの選択肢なども取り込めるよう、すべてを取り直す
                _ = _services.Sync.SyncAsync(force: true);
                break;
            case "app.help":
                await WithFocusRestoredAsync(() => ShortcutHelpDialog.ShowAsync(Content.XamlRoot));
                break;
            case "nav.settings":
                NavView.SelectedItem = NavView.SettingsItem;
                break;
            case "app.palette":
                await CommandPalette.ShowAsync(this);
                break;
            case "view.filter":
                if (ContentFrame.Content is IFilterHost host)
                {
                    await host.OpenFilterAsync();
                }

                break;
            case "view.sort":
                if (ContentFrame.Content is IViewOptionsHost sorting)
                {
                    await sorting.OpenOrderingAsync();
                }

                break;
            case "view.group":
                if (ContentFrame.Content is IViewOptionsHost grouping)
                {
                    await grouping.OpenGroupingAsync();
                }

                break;
        }
    }

    /// <summary>コマンドパレットなどから、全体の操作を名前で実行する。</summary>
    internal Task RunGlobalCommandAsync(string id) => RunGlobalAsync(id);

    // ---------------------------------------------------------------- 詳細パネル（UX 規約 UX-10、UX-15）

    /// <summary>次に見出しが変わるときに、新しい文字が入ってくる向き（↑ で前のタスクへ移ったときは -1）。</summary>
    private int _stepDirection = 1;

    /// <summary>開く前から開いていたパネルの中身を替えたか（見出しの入れ替わりを見せるか）。</summary>
    private bool _animateHeader;

    /// <summary>見出しを引いて閉じたときの、面が端へ向かっていた速さ（閉じる動きに引き継ぐ）。</summary>
    private double _dismissVelocity;

    /// <summary>
    /// 既定の大きさ（1400 × 900）で開く。画面の作業領域（タスクバーを除く）がそれより小さければ作業領域の 90 % に抑え、
    /// 作業領域の中央に置いて、画面の外へはみ出さないようにする。
    /// </summary>
    private void OpenWithinScreen()
    {
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        int width = Math.Min(1400, (int)(area.Width * 0.9));
        int height = Math.Min(900, (int)(area.Height * 0.9));
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
    }

    /// <summary>詳細パネルを載せた引き出しをつなぐ。</summary>
    private void InitializeDetail()
    {
        DetailDrawer.Actions = DetailPane.HeaderActions;
        DetailDrawer.DismissRequested += async (_, e) => await CloseDetailAsync(e.Velocity);
        DetailPane.HeaderChanged += (_, _) =>
        {
            DetailDrawer.SetHeader(DetailPane.HeaderTitle, DetailPane.HeaderDescription, _stepDirection, _animateHeader);
            _stepDirection = 1;
        };
        DetailPane.StepRequested += (_, delta) => StepDetail(delta);

        // 面はタイトルバーの下から出し、幕はウィンドウ全体を覆う
        AppTitleBar.SizeChanged += (_, _) => DetailDrawer.SheetInset = new Thickness(0, AppTitleBar.ActualHeight, 0, 0);
        RootGrid.SizeChanged += OnRootSizeChanged;
    }

    /// <summary>
    /// 詳細パネルの開閉を、開いているタスク（作成中のタスク）に合わせる。開いたらパネルへフォーカスを移し、
    /// 閉じたら、パネルにあったフォーカスを一覧へ戻す（UX-15）。
    /// </summary>
    private void UpdateDetailDrawer()
    {
        if (ViewModel.IsDetailOpen)
        {
            if (DetailDrawer.IsOpen)
            {
                return;
            }

            DetailDrawer.Open();

            // タイトルやサブタスクの入力欄など、開いた操作がフォーカスの置き場所を決めていればそちらに任せる
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (DetailDrawer.IsOpen && !IsFocusWithin(DetailDrawer))
                {
                    DetailPane.Focus(FocusState.Programmatic);
                }
            });
            return;
        }

        bool focused = IsFocusWithin(DetailDrawer);
        DetailDrawer.Close(_dismissVelocity);
        _dismissVelocity = 0;
        if (focused)
        {
            FocusPage();
        }
    }

    /// <summary>
    /// 詳細パネルを閉じ、開く前の行（↑↓ で移ったときは最後に見ていたタスクの行）へフォーカスを戻す。
    /// 作成中で入力があれば、破棄してよいかを確かめ、破棄しなければ引いた面を元へ戻す。
    /// </summary>
    /// <param name="velocity">見出しを引いて払ったときの速さ（閉じる動きに引き継ぐ）。</param>
    internal async Task CloseDetailAsync(double velocity = 0)
    {
        if (!await DetailPane.ConfirmCloseAsync())
        {
            DetailDrawer.Settle();
            return;
        }

        _dismissVelocity = velocity;
        ViewModel.SelectTask(null);
        FocusPage();
    }

    /// <summary>↑↓: 表示中のビューで前後のタスクを選び、詳細パネルの中身をそのタスクに替える。</summary>
    private void StepDetail(int delta)
    {
        if (ContentFrame.Content is ITaskSequence sequence && DetailPane.CurrentTask is { } current
            && sequence.Step(current.ItemId, delta) is { } next)
        {
            _stepDirection = delta;
            ViewModel.SelectTask(next);
        }
    }

    /// <summary>F6: ナビゲーションと画面のあいだでフォーカスを移す。詳細パネルを開いているあいだは移さない。</summary>
    private void ToggleFocusRegion()
    {
        if (ViewModel.IsDetailOpen)
        {
            return;
        }

        if (IsFocusInPage())
        {
            (NavView.SelectedItem as Control)?.Focus(FocusState.Keyboard);
        }
        else
        {
            FocusPage();
        }
    }

    private bool IsFocusWithin(DependencyObject ancestor) =>
        VisualTree.FocusedAncestors(Content.XamlRoot).Any(e => ReferenceEquals(e, ancestor));

    /// <summary>詳細パネルの幅。狭いときは、左に幕を少し残してほぼ全幅にする（UX 規約 UX-25）。</summary>
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e) =>
        DetailDrawer.SheetWidth = e.NewSize.Width <= 640 ? e.NewSize.Width - 12 : Math.Clamp(e.NewSize.Width * 0.34, 380, 440);

    /// <summary>ナビゲーションの n 番目（1 はマイタスク、続いてプロジェクト）へ移る。</summary>
    private bool GoToNavItem(int n)
    {
        var items = NavView.MenuItems.OfType<NavigationViewItem>().Where(i => i.SelectsOnInvoked && i.Visibility == Visibility.Visible).ToList();
        if (n > items.Count)
        {
            return false;
        }

        NavView.SelectedItem = items[n - 1];

        // Alt を離すと Windows がタイトルバーへフォーカスを移すため、落ち着いてから一覧へ戻す
        _refocus ??= new Debouncer(DispatcherQueue, TimeSpan.FromMilliseconds(300));
        _refocus.Run(() =>
        {
            if (!IsFocusInPage())
            {
                FocusPage();
            }
        });
        return true;
    }

    /// <summary>ナビゲーションの後に、フォーカスを一覧へ戻す。</summary>
    private Debouncer? _refocus;

    /// <summary>ピッカーやダイアログが開いているか（ツールチップは数えない）。</summary>
    private bool IsPopupOpen() =>
        _isDialogOpen || VisualTreeHelper.GetOpenPopupsForXamlRoot(Content.XamlRoot).Any(p => p.Child is not ToolTip);

    /// <summary>詳細ペインを開き、タイトルの編集を始める（ショートカットのタイトル変更）。</summary>
    internal void EditTitleInDetail(TaskItem task)
    {
        ViewModel.SelectTask(task);

        // 詳細ペインの表示が済んでからフォーカスする
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, DetailPane.FocusTitle);
    }

    // ---------------------------------------------------------------- タスクの追加

    /// <summary>文字を入力しているところにフォーカスがあるか（N キーをそのまま打てるようにする）。</summary>
    private bool IsTypingSomewhere() =>
        KeyInput.IsTextInput(Content?.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) : null);

    /// <summary>
    /// タスクを追加する。始めた場所の文脈（UX 規約 UX-12）を既定値にする。
    /// 文脈を渡さなければ、いま開いている画面から求める。
    /// </summary>
    internal async Task AddTaskAsync(NewTaskContext? context = null)
    {
        var projects = await NewTaskDialog.ProjectsAsync();
        if (projects.Count == 0)
        {
            return;
        }

        // 画面の文脈（追加先・絞り込み）に、始めた場所の文脈（カンバンの列など）を重ねる
        var page = (ContentFrame.Content as INewTaskContextSource)?.NewTaskContext() ?? new NewTaskContext();
        context = context is null ? page : page with
        {
            Status = context.Status ?? page.Status,
            Kind = context.Kind ?? page.Kind,
            Assignee = context.Assignee ?? page.Assignee,
            ParentIssueId = context.ParentIssueId ?? page.ParentIssueId,
            Due = context.Due ?? page.Due,
        };

        _isDialogOpen = true;
        try
        {
            await NewTaskDialog.ShowAsync(Content.XamlRoot, projects, context);
        }
        finally
        {
            _isDialogOpen = false;
        }
    }

    // ---------------------------------------------------------------- ナビゲーション

    private void OnProjectsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 並べ替えは、項目を作り直さずに動かして、ほかの項目が滑るように見せる
        if (e.Action == NotifyCollectionChangedAction.Move && e.OldItems?[0] is ProjectNavItem moved)
        {
            MoveProjectItem(moved.Id);
            return;
        }

        var selectedTag = (NavView.SelectedItem as NavigationViewItem)?.Tag as string;

        foreach (var item in _projectItems)
        {
            NavView.MenuItems.Remove(item);
        }

        _projectItems.Clear();
        _navProjects.Clear();

        // 個人・チームとも、プロジェクトがあるときだけ見出しを付けて並べる
        foreach (var (isTeam, title) in ((bool, string)[])[(false, "個人"), (true, "チーム")])
        {
            var items = ViewModel.Projects.Where(p => p.IsTeam == isTeam).ToList();
            if (items.Count == 0)
            {
                continue;
            }

            var header = new NavigationViewItemHeader { Content = title };
            _projectItems.Add(header);
            NavView.MenuItems.Add(header);

            foreach (var project in items)
            {
                AddProjectItem(project);
            }
        }

        if (selectedTag is not null && FindProjectItem(selectedTag) is { } reselect)
        {
            NavView.SelectedItem = reselect;
        }
    }

    private void AddProjectItem(ProjectNavItem project)
    {
        var item = new NavigationViewItem
        {
            Content = project.Name,
            Tag = $"project:{project.Id}",
            Icon = new FontIcon { Glyph = project.IsTeam ? "\uE716" : "\uE77B" },
        };
        ToolTipService.SetToolTip(item, project.Name);
        EnableProjectDrag(item, project);
        _projectItems.Add(item);
        NavView.MenuItems.Add(item);
    }

    private NavigationViewItem? FindProjectItem(string tag) =>
        _projectItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string?)i.Tag == tag);

    private async void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer?.Tag as string == "project:new")
        {
            await CreateProjectAsync();
        }
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // ページの移動に合わせて選択状態を直しているときは、移動し直さない
        if (_syncingSelection)
        {
            return;
        }

        if (args.IsSettingsSelected)
        {
            Navigate(typeof(SettingsPage), null, args.RecommendedNavigationTransitionInfo);
            return;
        }

        if (args.SelectedItemContainer?.Tag is not string tag)
        {
            return;
        }

        var (pageType, parameter) = tag switch
        {
            "my" => (typeof(MyTasksPage), null),
            _ when tag.StartsWith("project:", StringComparison.Ordinal) =>
                (typeof(ProjectPage), ViewModel.Projects.FirstOrDefault(p => p.Id == tag[8..])),
            _ => (null, null),
        };

        if (pageType is not null)
        {
            Navigate(pageType, parameter, args.RecommendedNavigationTransitionInfo);
        }
    }

    private void Navigate(Type pageType, object? parameter, NavigationTransitionInfo transition)
    {
        if (ContentFrame.CurrentSourcePageType == pageType && Equals(ContentFrame.Tag, parameter))
        {
            return;
        }

        ContentFrame.Tag = parameter;
        ContentFrame.Navigate(pageType, parameter, transition);
    }

    private void OnFrameNavigated(object sender, NavigationEventArgs e)
    {
        ContentFrame.Tag = e.Parameter;
        _syncingSelection = true;
        try
        {
            if (e.SourcePageType == typeof(SettingsPage))
            {
                NavView.SelectedItem = NavView.SettingsItem;
                return;
            }

            // 戻る操作でページが変わったときに、ナビゲーションの選択状態を追従させる。
            // プロジェクトの設定は、そのプロジェクトを選んだ状態で見せる
            string? tag = e.SourcePageType switch
            {
                var t when t == typeof(MyTasksPage) => "my",
                var t when (t == typeof(ProjectPage) || t == typeof(ProjectSettingsPage)) && e.Parameter is ProjectNavItem p => $"project:{p.Id}",
                _ => null,
            };

            if (tag is not null && FindItem(NavView.MenuItems, tag) is { } item && !ReferenceEquals(NavView.SelectedItem, item))
            {
                NavView.SelectedItem = item;
            }
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private bool _syncingSelection;

    /// <summary>プロジェクトの設定を開く（プロジェクトの「…」、全体の設定のプロジェクトの一覧から）。</summary>
    internal void OpenProjectSettings(string projectId)
    {
        if (ViewModel.Projects.FirstOrDefault(p => p.Id == projectId) is { } project)
        {
            Navigate(typeof(ProjectSettingsPage), project, new DrillInNavigationTransitionInfo());
        }
    }

    /// <summary>プロジェクトを開く。</summary>
    internal void OpenProject(string projectId)
    {
        if (FindProjectItem($"project:{projectId}") is { } item)
        {
            NavView.SelectedItem = item;
        }
    }

    /// <summary>全体の設定を、指定したタブ（general / personal / team）で開く。</summary>
    internal void OpenSettings(string tab) => Navigate(typeof(SettingsPage), tab, new DrillInNavigationTransitionInfo());

    /// <summary>マイタスクへ戻る（開いていたプロジェクトがなくなったときなど）。</summary>
    internal void GoToMyTasks() => NavView.SelectedItem = MyTasksItem;

    private static NavigationViewItem? FindItem(IList<object> items, string tag)
    {
        foreach (var item in items.OfType<NavigationViewItem>())
        {
            if (item.SelectsOnInvoked && item.Tag as string == tag)
            {
                return item;
            }

            if (FindItem(item.MenuItems, tag) is { } child)
            {
                return child;
            }
        }

        return null;
    }

    private void OnTitleBarBackRequested(TitleBar sender, object args)
    {
        if (ContentFrame.CanGoBack)
        {
            ContentFrame.GoBack();
        }
    }

    private void OnTitleBarPaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    // ---------------------------------------------------------------- プロジェクト作成

    private async Task CreateProjectAsync()
    {
        // ContentDialog は同時に 1 つしか開けないため、連続した操作では開かない
        if (_isDialogOpen)
        {
            return;
        }

        _isDialogOpen = true;
        try
        {
            await ShowCreateProjectDialogAsync();
        }
        finally
        {
            _isDialogOpen = false;
        }
    }

    private async Task ShowCreateProjectDialogAsync()
    {
        if (await Views.Dialogs.NewProjectDialog.ShowAsync(Content.XamlRoot) is { } project)
        {
            await ViewModel.ReloadAsync();
            if (FindProjectItem($"project:{project.Id}") is { } item)
            {
                NavView.SelectedItem = item;
            }
        }
    }

    // ---------------------------------------------------------------- 同期の状態（UX 規約 UX-31）

    private readonly Flyout _syncDetails = new() { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };

    /// <summary>問題がなければ今すぐ同期し、あれば詳細と操作を開く（同期は詳細の中からも、F5 でもできる）。</summary>
    private void OnSyncButtonClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.HasSyncProblems)
        {
            OpenSyncDetails();
        }
        else
        {
            ViewModel.SyncNowCommand.Execute(null);
        }
    }

    private void OpenSyncDetails()
    {
        if (!ViewModel.HasSyncProblems)
        {
            return;
        }

        var details = StatusChip.Details(ViewModel.SyncProblems, _syncDetails.Hide);
        var sync = new HyperlinkButton { Content = KeyHints.Tip("app.sync", "今すぐ同期"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0) };
        sync.Click += (_, _) =>
        {
            _syncDetails.Hide();
            ViewModel.SyncNowCommand.Execute(null);
        };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(details);
        panel.Children.Add(sync);
        _syncDetails.Content = panel;
        _syncDetails.ShowAt(SyncButton);
    }

    /// <summary>問題があるときは、ほかのチップと同じ色の丸い形にする。</summary>
    private void UpdateSyncButton()
    {
        bool critical = ViewModel.SyncProblemSeverity == ChipSeverity.Critical;
        SyncButton.Background = ViewModel.HasSyncProblems
            ? ThemeResources.Brush(critical ? "SystemFillColorCriticalBackgroundBrush" : "SystemFillColorCautionBackgroundBrush")
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        if (ViewModel.HasSyncProblems)
        {
            SyncIcon.Foreground = ThemeResources.Brush(critical ? "SystemFillColorCriticalBrush" : "SystemFillColorCautionBrush");
        }
        else
        {
            SyncIcon.ClearValue(IconElement.ForegroundProperty);
        }
    }

    private bool _toastPointer;
    private bool _toastFocus;

    /// <summary>
    /// 知らせの ✕ にフォーカスを取らせない。知らせは時間で閉じるため、✕ をフォーカスで行き来する先にしない。
    /// 押してもフォーカスを移さないので、閉じた知らせにフォーカスが取り残されず、✕ にフォーカスの枠も出ない。
    /// 操作のボタン（「表示する」など）は、ほかに入口のないものがあるため、フォーカスを取れるまま残す。
    /// ✕ はテンプレートの部品のため、表示して当て終えてから設定する。
    /// </summary>
    private void KeepToastUnfocusable()
    {
        Toast.UpdateLayout();
        foreach (var button in VisualTree.Descendants<Button>(Toast).Where(b => b.Name == "CloseButton"))
        {
            button.IsTabStop = false;
            button.AllowFocusOnInteraction = false;
        }
    }

    private void HoldToast(bool? pointer = null, bool? focus = null)
    {
        _toastPointer = pointer ?? _toastPointer;
        _toastFocus = focus ?? _toastFocus;
        ViewModel.HoldToast(_toastPointer || _toastFocus);
    }
}
