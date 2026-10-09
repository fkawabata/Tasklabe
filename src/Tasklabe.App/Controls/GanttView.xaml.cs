using System.Globalization;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Tasklabe.App.Services;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Editing;
using Tasklabe.Core.Gantt;
using Tasklabe.Core.Milestones;
using Tasklabe.Core.Wbs;
using Windows.Foundation;
using Windows.System;

namespace Tasklabe.App.Controls;

/// <summary>表示の粒度（UI デザイン設計書 3.2 節）。1 日の幅で表す。</summary>
public enum GanttScale
{
    Day,
    Week,
    Month,
}

/// <summary>
/// ガントチャート（要件 F-UI-GT-01〜08、技術設計書 5 章）。
/// 左は WBS の列、右は Win2D で描くタイムライン。どちらも表示範囲の行だけを扱い、縦のスクロールを共有する。
/// </summary>
public sealed partial class GanttView : UserControl
{
    public const double RowHeight = 32;
    private const double HeaderHeight = 56;
    private const float BarHeight = 16;
    private const float EdgeGrip = 5;
    private const float LinkHandleOffset = 9;
    private const double MinDayWidth = 2;
    private const double MaxDayWidth = 64;

    private Project? _project;
    private TaskTree _tree = TaskTree.Build([]);
    private Func<TaskNode, bool>? _visible;
    private IReadOnlyList<GanttRow> _rows = [];
    private readonly HashSet<string> _collapsed = [];
    private readonly List<GanttRowPresenter> _presenters = [];
    private string? _selectedItemId;
    private int _hoverRow = -1;
    private string? _loadedProjectId;

    private DateOnly _origin;
    private int _dayCount = 1;
    private double _dayWidth = 28;
    private double _scrollX;
    private double _scrollY;
    private bool _syncingScrollBars;

    private Drag? _drag;

    /// <summary>編集モードか（UI デザイン設計書 3.3.4 節）。閲覧モードでは、ドラッグで表示する場所を動かす。</summary>
    private bool _editing;

    /// <summary>ドラッグがチャートの端に来たときに、少しずつ画面を動かすタイマー。</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _autoScrollTimer;

    /// <summary>「今日へ」のスクロールの動き。</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _scrollAnimation;

    /// <summary>送信した日程の変更を、次の再表示まで描き続ける（再表示までの一瞬のちらつきを防ぐ）。</summary>
    private Drag? _committed;

    private readonly CanvasTextFormat _headerFormat = new() { FontFamily = "Segoe UI Variable Text", FontSize = 12, WordWrapping = CanvasWordWrapping.NoWrap };
    private readonly CanvasTextFormat _dayFormat = new() { FontFamily = "Segoe UI Variable Text", FontSize = 12, WordWrapping = CanvasWordWrapping.NoWrap };

    private static readonly string[] DayNumbers = Enumerable.Range(0, 32).Select(d => d.ToString(CultureInfo.InvariantCulture)).ToArray();

    /// <summary>見出しの文字のレイアウト。スクロールのたびに作り直さないよう使い回す。</summary>
    private readonly Dictionary<(string Text, CanvasTextFormat Format), CanvasTextLayout> _textCache = [];
    private CanvasDevice? _textDevice;
    private int _frameSelected = -1;
    private readonly CanvasTextFormat _captionFormat = new() { FontFamily = "BIZ UDPGothic", FontSize = 12, WordWrapping = CanvasWordWrapping.NoWrap };
    private readonly CanvasTextFormat _labelFormat = new() { FontFamily = "BIZ UDPGothic", FontSize = 12, WordWrapping = CanvasWordWrapping.NoWrap };

    public GanttView()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        Canvas.SetZIndex(_stickyDivider, 200);
        RowsHost.Children.Add(_stickyDivider);

        // 狭いときは、チャートより先に左の表を縮める（担当・工数・進捗の列から隠れる。UX 規約 UX-25）
        SizeChanged += (_, e) =>
        {
            if (!_leftResized && e.NewSize.Width > 0)
            {
                LeftColumn.Width = new GridLength(Math.Clamp(e.NewSize.Width * 0.38, 200, 420));
                ApplySideColumns();
            }
        };
        ContextRequested += OnContextRequested;
        PointerWheelChanged += OnWheel;
        ActualThemeChanged += (_, _) =>
        {
            LayoutRows(rebind: true);
            RequestRender();
        };
        Loaded += (_, _) =>
        {
            _xamlRoot = XamlRoot;
            _xamlRoot.Changed += OnXamlRootChanged;
            RequestRender();
        };
        Unloaded += (_, _) =>
        {
            // ウィンドウを閉じるときは XamlRoot が先に外れているため、購読したときのものから外す
            if (_xamlRoot is not null)
            {
                _xamlRoot.Changed -= OnXamlRootChanged;
                _xamlRoot = null;
            }

            StopRendering();
            _swapChain?.Dispose();
            _swapChain = null;
            ClearTextCache();
        };
        Loaded += (_, _) => StartBenchmarkIfRequested();
    }

    /// <summary>イナズマ線を表示するか（要件 F-UI-GT-03）。</summary>
    public bool ShowInazuma
    {
        get;
        set
        {
            field = value;
            RequestRender();
        }
    } = true;

    public GanttScale TimeScale
    {
        get => _dayWidth >= 18 ? GanttScale.Day : _dayWidth >= 6 ? GanttScale.Week : GanttScale.Month;
        set => Zoom(value switch { GanttScale.Day => 28, GanttScale.Week => 12, _ => 4 }, Chart.ActualWidth / 2);
    }

    private static DateOnly Today => AppClock.Today;

    // ================================================================ 読み込み

    // ---------------------------------------------------------------- マイルストーン（要件 F-MS-03、F-MS-04）

    /// <summary>期日の順のマイルストーンと、それぞれの集計。期日の位置に縦線を引き、日付の軸の上に名前を置く。</summary>
    private IReadOnlyList<MilestoneSummary> _milestones = [];

    /// <summary>日付の軸の上に描いたマイルストーンの名前の範囲（押したとき・重ねたときに使う）。</summary>
    private readonly List<(Rect Bounds, MilestoneSummary Summary)> _milestoneHits = [];

    private readonly CanvasStrokeStyle _milestoneDash = new() { CustomDashStyle = [4, 3] };

    /// <summary>日付の軸の上のマイルストーンの名前を押した。</summary>
    public event EventHandler<Milestone>? MilestoneInvoked;

    /// <summary>日付の軸を右クリックして、その日にマイルストーンを置こうとした。</summary>
    public event EventHandler<DateOnly>? MilestoneRequested;

    /// <summary>マイルストーンの縦線の位置（期日の日の右端。その日の終わりまでが期限）。</summary>
    private float MilestoneX(DateOnly due) => X(due.AddDays(1));

    private MilestoneSummary? MilestoneAt(Point position) =>
        _milestoneHits.Where(h => h.Bounds.Contains(position)).Select(h => h.Summary).LastOrDefault();

    public void Load(Project project, TaskTree tree, Func<TaskNode, bool>? isVisible = null)
    {
        if (!BenchmarkRunning)
        {
            LoadCore(project, tree, isVisible);
        }
    }

    private void LoadCore(Project project, TaskTree tree, Func<TaskNode, bool>? isVisible)
    {
        bool projectChanged = _loadedProjectId != project.Id;
        _project = project;
        _tree = _pendingLink is { } pending ? WithLink(tree, pending) : tree;
        _milestones = project.IsTeam ? [.. MilestonePlan.Summarize(tree, project.Milestones).Where(s => s.Milestone.Due is not null)] : [];
        _visible = isVisible;
        _committed = null;
        if (projectChanged)
        {
            // 折りたたみはプロジェクトごとに覚える（UX 規約 UX-23）
            _collapsed.Clear();
            _collapsed.UnionWith(ViewState.GetSet($"gantt-collapsed:{project.Id}"));
            if (int.TryParse(ViewState.Get($"gantt-left:{project.Id}"), out var left) && left >= 200)
            {
                _leftResized = true;
                LeftColumn.Width = new GridLength(left);
            }
            _selectedItemId = null;
            _pinned = null;
            _pendingLink = null;
        }

        Rebuild();
        UpdatePreviewBar();

        // 日付の範囲を、予定・実績と今日を含むように広げる（左端の日付は保つ）
        double leftDay = _origin.DayNumber + _scrollX / _dayWidth;
        var today = Today;
        var extent = GanttModel.Extent(_rows);
        var first = Min(extent?.Start ?? today, today).AddDays(-35);
        var last = Max(extent?.End ?? today, today).AddDays(120);
        first = first.AddDays(-(((int)first.DayOfWeek + 6) % 7)); // 月曜に揃える
        if (!projectChanged && _dayCount > 1)
        {
            // 画面の移動で広げた範囲は、読み込み直しても保つ
            first = Min(first, _origin);
            last = Max(last, _origin.AddDays(_dayCount - 1));
        }

        _origin = first;
        _dayCount = last.DayNumber - first.DayNumber + 1;

        if (projectChanged)
        {
            // 別のプロジェクトを開いたときは、閲覧モードから始める
            IsEditing = false;
            _loadedProjectId = project.Id;
            // 最初は今日の少し前から表示する。直近に始まった予定があれば、その始まりから表示する
            leftDay = Math.Max(Math.Min(today.DayNumber - 7, (extent?.Start ?? today).DayNumber - 2), today.DayNumber - 21);
            _scrollY = 0;
        }

        _scrollX = (leftDay - _origin.DayNumber) * _dayWidth;
        ApplySideColumns();

        UpdateScrollBars();
        LayoutRows(rebind: true);
        UpdateTodayJump();
        RequestRender();
    }

    private void Rebuild()
    {
        RefreshPreview();
        var nodes = (_previewTree ?? _tree).Flatten(n => !_collapsed.Contains(n.Task.ItemId));
        if (_visible is { } visible)
        {
            nodes = nodes.Where(visible);
        }

        _rows = GanttModel.Build(nodes, Today, _previewTree ?? _tree, _milestones);
        _depths = [.. _rows.Select(r => r.Node?.Depth ?? 0)];
        _hasInazuma = _rows.Any(r => r.Inazuma is not null);
    }

    private bool _hasInazuma;

    /// <summary>行ごとの階層の深さ（上端に残す親を求めるのに使う）。</summary>
    private int[] _depths = [];

    /// <summary>縦にスクロールしたとき、上端に残している親の行（UI デザイン設計書 3.3.1 節）。</summary>
    private IReadOnlyList<StickyRow> _sticky = [];

    /// <summary>縦にスクロールしても、親の行を上端に残すか（ツールバーで切り替える）。</summary>
    public bool StickyParents
    {
        get;
        set
        {
            field = value;
            LayoutRows(rebind: false);
            RequestRender();
        }
    } = true;

    /// <summary>上端に残す行の上限。表示範囲の半分までとし、残りで配下の行を読めるようにする。</summary>
    private int MaxSticky => StickyParents ? Math.Max((int)(BodyHeight / RowHeight / 2), 0) : 0;

    private void UpdateSticky() => _sticky = StickyRows.Compute(_depths, _scrollY, RowHeight, MaxSticky);

    /// <summary>行の見えている上端（チャートの座標）。上端に残している行は、残している位置。</summary>
    private float DisplayTop(int index) =>
        StickyRows.TopOf(_sticky, index) is { } top ? (float)(HeaderHeight + top) : RowTop(index);

    private int SelectedIndex => _selectedItemId is null ? -1 : _rows.FirstOrDefault(r => r.Key == _selectedItemId)?.Index ?? -1;

    // ================================================================ スクロールと拡大縮小

    private double ContentHeight => _rows.Count * RowHeight;

    private double ContentWidth => _dayCount * _dayWidth;

    private double BodyHeight => Math.Max(Chart.ActualHeight - HeaderHeight, 0);

    /// <summary>利用者が左の表の幅を変えたか（変えたら、ウィンドウの幅に合わせて縮めない）。</summary>
    private bool _leftResized;

    /// <summary>左の表の幅は、落ち着いてからプロジェクトごとに覚える（UX 規約 UX-23）。</summary>
    private Debouncer? _saveLeftWidth;

    private void OnSplitterManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        _leftResized = true;
        LeftColumn.Width = new GridLength(Math.Clamp(LeftColumn.ActualWidth + e.Delta.Translation.X, 200, Math.Max(ActualWidth - 240, 200)));
        _saveLeftWidth ??= new Debouncer(DispatcherQueue, TimeSpan.FromMilliseconds(500));
        _saveLeftWidth.Run(() =>
        {
            if (_project is { } project)
            {
                ViewState.Set($"gantt-left:{project.Id}", ((int)LeftColumn.Width.Value).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        });
        ApplySideColumns();
    }

    /// <summary>担当・工数・進捗の列の幅。</summary>
    internal readonly record struct SideColumns(double Assignee, double Estimate, double Progress);

    /// <summary>タスク名の列に残す最小の幅。これを割り込むときは、進捗 → 工数 → 担当の順に列を隠す。</summary>
    private const double MinTitleWidth = 180;

    private SideColumns _sideColumns = new(88, 56, 52);

    /// <summary>
    /// 左の表の幅に合わせて、担当・工数・進捗の列を出すか決める。狭めてもタスク名が読めるよう、
    /// タスク名の列を優先し、補助の列から順に隠す。
    /// </summary>
    private void ApplySideColumns()
    {
        double width = LeftColumn.Width.IsAbsolute ? LeftColumn.Width.Value : LeftColumn.ActualWidth;
        double assignee = _project?.IsTeam == true ? 88 : 0;
        double estimate = 56;
        double progress = 52;
        if (width - assignee - estimate - progress < MinTitleWidth)
        {
            progress = 0;
        }

        if (width - assignee - estimate - progress < MinTitleWidth)
        {
            estimate = 0;
        }

        if (width - assignee - estimate - progress < MinTitleWidth)
        {
            assignee = 0;
        }

        var columns = new SideColumns(assignee, estimate, progress);
        AssigneeHeaderColumn.Width = new GridLength(assignee);
        EstimateHeaderColumn.Width = new GridLength(estimate);
        ProgressHeaderColumn.Width = new GridLength(progress);
        foreach (var header in LeftHeader.Children.OfType<FrameworkElement>())
        {
            int column = Grid.GetColumn(header);
            header.Visibility = column switch
            {
                1 => assignee > 0 ? Visibility.Visible : Visibility.Collapsed,
                2 => estimate > 0 ? Visibility.Visible : Visibility.Collapsed,
                3 => progress > 0 ? Visibility.Visible : Visibility.Collapsed,
                _ => Visibility.Visible,
            };
        }

        if (columns == _sideColumns && _presenters.Count > 0 && ReferenceEquals(_sideColumnsProject, _project))
        {
            return;
        }

        _sideColumns = columns;
        _sideColumnsProject = _project;
        foreach (var presenter in _presenters.Concat(_stickyPresenters.Select(p => p.Presenter)))
        {
            presenter.SetColumnWidths(columns);
        }
    }

    private Project? _sideColumnsProject;

    // ================================================================ 左側の行

    private void LayoutRows(bool rebind)
    {
        double height = RowsHost.ActualHeight;
        RowsHost.Clip = new RectangleGeometry { Rect = new Rect(0, 0, RowsHost.ActualWidth, height) };
        UpdateSticky();

        int first = (int)Math.Floor(_scrollY / RowHeight);
        int count = height <= 0 ? 0 : (int)Math.Ceiling(height / RowHeight) + 1;
        while (_presenters.Count < count)
        {
            var presenter = new GanttRowPresenter();
            presenter.SetColumnWidths(_sideColumns);
            _presenters.Add(presenter);
            RowsHost.Children.Add(presenter);
        }

        int selected = SelectedIndex;
        for (int k = 0; k < _presenters.Count; k++)
        {
            var presenter = _presenters[k];
            int index = first + k;
            if (k >= count || index >= _rows.Count)
            {
                presenter.Visibility = Visibility.Collapsed;
                continue;
            }

            var row = _rows[index];
            presenter.Visibility = Visibility.Visible;
            presenter.Width = RowsHost.ActualWidth;
            Canvas.SetTop(presenter, index * RowHeight - _scrollY);
            if (rebind || presenter.Row != row)
            {
                presenter.Bind(row, !_collapsed.Contains(row.Key), row.Index == selected);
            }
            else
            {
                presenter.SetSelected(row.Index == selected);
            }
        }

        LayoutStickyRows(rebind, selected);
    }

    /// <summary>上端に残す行。外側の親を手前に重ね、配下の終わりで押し上げられた内側の親は外側の下へ隠れていく。</summary>
    private readonly List<(Grid Host, GanttRowPresenter Presenter)> _stickyPresenters = [];

    /// <summary>上端に残した行の下の区切り。</summary>
    private readonly Border _stickyDivider = new() { Height = 1, IsHitTestVisible = false };

    /// <summary>上端に残した行の下地。下の行が透けないよう、チャートと同じ不透明な色で塗る。</summary>
    private readonly SolidColorBrush _stickyFill = new();

    private void LayoutStickyRows(bool rebind, int selected)
    {
        if (rebind || _stickyPresenters.Count == 0)
        {
            _stickyFill.Color = ReadPalette().Surface;
            _stickyDivider.Background = ThemeResources.Brush("DividerStrokeColorDefaultBrush");
        }

        while (_stickyPresenters.Count < _sticky.Count)
        {
            var presenter = new GanttRowPresenter();
            presenter.SetColumnWidths(_sideColumns);
            var host = new Grid { Background = _stickyFill, Children = { presenter } };
            _stickyPresenters.Add((host, presenter));
            RowsHost.Children.Add(host);
        }

        for (int k = 0; k < _stickyPresenters.Count; k++)
        {
            var (host, presenter) = _stickyPresenters[k];
            if (k >= _sticky.Count)
            {
                host.Visibility = Visibility.Collapsed;
                continue;
            }

            var row = _rows[_sticky[k].Index];
            host.Visibility = Visibility.Visible;
            host.Width = RowsHost.ActualWidth;
            Canvas.SetTop(host, _sticky[k].Top);
            Canvas.SetZIndex(host, 100 - k);
            if (rebind || presenter.Row != row)
            {
                presenter.Bind(row, !_collapsed.Contains(row.Key), row.Index == selected);
            }
            else
            {
                presenter.SetSelected(row.Index == selected);
            }
        }

        double covered = StickyRows.Covered(_sticky, RowHeight);
        _stickyDivider.Visibility = covered > 0 ? Visibility.Visible : Visibility.Collapsed;
        _stickyDivider.Width = RowsHost.ActualWidth;
        Canvas.SetTop(_stickyDivider, covered - 1);
    }

    /// <summary>本体の上端からの位置にある行。上端に残している行を先に見る。</summary>
    private int RowAt(double bodyY)
    {
        if (StickyRows.At(_sticky, bodyY, RowHeight) is { } sticky)
        {
            return sticky;
        }

        return NaturalRowAt(bodyY);
    }

    /// <summary>本体の上端からの位置にある、本来の位置の行（上端に残している行に覆われていても、その下の行）。</summary>
    private int NaturalRowAt(double bodyY)
    {
        int index = (int)Math.Floor((bodyY + _scrollY) / RowHeight);
        return index >= 0 && index < _rows.Count ? index : -1;
    }

    private bool IsOnSticky(double bodyY) => StickyRows.At(_sticky, bodyY, RowHeight) is not null;

    /// <summary>上端に残している行を押した（押すと本来の位置へ戻すため、続けて押した 2 回目は別の行に当たる）。</summary>
    private (string Key, long At)? _stickyPress;

    /// <summary>上端に残している行を押して本来の位置へ戻し、選ぶ。</summary>
    private void SelectSticky(int index)
    {
        _stickyPress = (_rows[index].Key, Environment.TickCount64);
        Select(index);
    }

    /// <summary>ダブルクリックの 1 回目で上端に残している行を押していれば、その行（2 回目の位置の行ではなく）。</summary>
    private GanttRow? StickyDoubleTapped()
    {
        if (_stickyPress is not { } press || Environment.TickCount64 - press.At > new Windows.UI.ViewManagement.UISettings().DoubleClickTime)
        {
            return null;
        }

        _stickyPress = null;
        return _rows.FirstOrDefault(r => r.Key == press.Key);
    }

    /// <summary>右クリックした行を選ぶ。上端に残している行はその場で選び、メニューの位置がずれないようスクロールしない。</summary>
    private void SelectForMenu(int index, double bodyY)
    {
        if (!IsOnSticky(bodyY))
        {
            Select(index);
            return;
        }

        _selectedItemId = _rows[index].Key;
        LayoutRows(rebind: false);
        RequestRender();
    }

    private void OnRowsPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(RowsHost);
        int index = RowAt(point.Position.Y);
        Focus(FocusState.Pointer);
        if (index < 0 || !point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        // 開閉の矢印の上なら、子タスクの表示を切り替える
        if (IsOnExpander(index, point.Position.X))
        {
            ToggleExpanded(_rows[index]);
        }
        else if (IsOnSticky(point.Position.Y))
        {
            SelectSticky(index);
        }
        else
        {
            Select(index);
        }

        e.Handled = true;
    }

    private void OnRowsDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // 開閉の矢印を続けて押したときは開閉だけにし、詳細は開かない（計画の表と同じ）
        var position = e.GetPosition(RowsHost);
        if (StickyDoubleTapped() is { } sticky)
        {
            OpenDetail(sticky);
            return;
        }

        int index = RowAt(position.Y);
        if (index >= 0 && !IsOnExpander(index, position.X))
        {
            OpenDetail(_rows[index]);
        }
    }

    private bool IsOnExpander(int index, double x)
        => _rows[index].Node is { HasChildren: true } node
            && x >= GanttRowPresenter.TitleLeft(node.Depth) && x <= GanttRowPresenter.TitleLeft(node.Depth) + 20;

    private void OnRowsRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var position = e.GetPosition(RowsHost);
        int index = RowAt(position.Y);
        if (index >= 0)
        {
            SelectForMenu(index, position.Y);
            ShowContextMenu(_rows[index], RowsHost, position);
            e.Handled = true;
        }
    }

    private void Select(int index)
    {
        _selectedItemId = index >= 0 && index < _rows.Count ? _rows[index].Key : null;
        if (index >= 0)
        {
            ScrollRowIntoView(index);
        }

        LayoutRows(rebind: false);
        RequestRender();
    }

    private void ToggleExpanded(GanttRow row)
    {
        if (row.Node?.HasChildren != true)
        {
            return;
        }

        if (!_collapsed.Remove(row.Task.ItemId))
        {
            _collapsed.Add(row.Task.ItemId);
        }

        ViewState.SetSet($"gantt-collapsed:{_project?.Id}", _collapsed);

        // 上端に残している行を開閉したときは、その行が同じ位置に見えるようにスクロールする（配下が消えて別の行へ跳ばないように）
        if (StickyRows.TopOf(_sticky, row.Index) is { } stickyTop)
        {
            _scrollY = row.Index * RowHeight - Math.Max(stickyTop, 0);
        }

        _selectedItemId = row.Task.ItemId;
        Rebuild();
        UpdateScrollBars();
        LayoutRows(rebind: true);
        RequestRender();
    }

    /// <summary>タスクは詳細を開き、マイルストーンは編集を開く。</summary>
    private void OpenDetail(GanttRow row)
    {
        if (IsPreviewing)
        {
            return;
        }

        if (row.Milestone is { } milestone)
        {
            HideHover();
            MilestoneInvoked?.Invoke(this, milestone.Milestone);
        }
        else
        {
            App.Current.Shell?.SelectTask(row.Task);
        }
    }

    private async void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // ドラッグの途中で Esc を押したら、何も変えずに取りやめる（UX 規約 UX-21）
        if (_drag is not null && e.Key == VirtualKey.Escape)
        {
            _drag = null;
            _autoScrollTimer?.Stop();
            ChartInput.ReleasePointerCaptures();
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
            RequestRender();
            e.Handled = true;
            return;
        }

        if (IsPreviewing && e.Key == VirtualKey.Escape)
        {
            EndPreview(confirmed: false);
            e.Handled = true;
            return;
        }

        if (_editing && e.Key == VirtualKey.Escape)
        {
            IsEditing = false;
            e.Handled = true;
            return;
        }

        if (KeyInput.From(e.Key) is { } gesture && await HandleShortcutAsync(gesture))
        {
            e.Handled = true;
            return;
        }

        int selected = SelectedIndex;
        switch (e.Key)
        {
            case VirtualKey.Up:
                Select(Math.Max(selected - 1, 0));
                break;
            case VirtualKey.Down:
                Select(Math.Min(selected + 1, _rows.Count - 1));
                break;
            case VirtualKey.Left when selected >= 0:
                var row = _rows[selected];
                if (row.Node is { HasChildren: true } && !_collapsed.Contains(row.Key))
                {
                    ToggleExpanded(row);
                }
                else if (row.Node?.Parent is { } parent && _rows.FirstOrDefault(r => r.Node == parent) is { } parentRow)
                {
                    Select(parentRow.Index);
                }

                break;
            case VirtualKey.Right when selected >= 0 && _collapsed.Contains(_rows[selected].Key):
                ToggleExpanded(_rows[selected]);
                break;
            case VirtualKey.Enter when selected >= 0:
                OpenDetail(_rows[selected]);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // ================================================================ 座標

    private float X(double dayNumber) => (float)((dayNumber - _origin.DayNumber) * _dayWidth - _scrollX);

    private float X(DateOnly date) => X(date.DayNumber);

    private float RowTop(int index) => (float)(HeaderHeight + index * RowHeight - _scrollY);

    private float RowCenter(int index) => RowTop(index) + (float)RowHeight / 2;

    private DateOnly DateAt(double x) => DateOnly.FromDayNumber(_origin.DayNumber + (int)Math.Floor((x + _scrollX) / _dayWidth));

    /// <summary>行の表示上の予定（ドラッグ中はドラッグ後の日付）。</summary>
    private (DateOnly Start, DateOnly End)? PlanOf(GanttRow row)
    {
        foreach (var d in (Drag?[])[_drag, _committed])
        {
            if (d is { Mode: DragMode.Move or DragMode.ResizeStart or DragMode.ResizeEnd or DragMode.Create }
                && d.ItemId == row.Key && d.Changed)
            {
                return (d.NewStart, d.NewEnd);
            }
        }

        return row.PlanStart is { } s && row.PlanEnd is { } e ? (s, e) : null;
    }

    /// <summary>
    /// 行のバーが描かれている左右の範囲。展開している親タスクの範囲の線は予定と実績の和、
    /// タスクのバー（折りたたんだ親タスクを含む）は DrawTaskBar と同じく予定に、予定を超えた未完了の実績を足した範囲とする。
    /// </summary>
    private (float Left, float Right)? BarExtent(GanttRow row)
    {
        bool range = row.Kind == GanttRowKind.Parent && !_collapsed.Contains(row.Key);
        if (!range && PlanOf(row) is { } p)
        {
            float barLeft = X(p.Start);
            float barRight = Math.Max(X(p.End.DayNumber + 1), barLeft + 2);
            if (WorkStart(row, p) is { } work)
            {
                barLeft = Math.Min(barLeft, X(work));
            }

            if (!row.IsDone && row.ActualEnd is { } actualEnd)
            {
                barRight = Math.Max(barRight, X(actualEnd.DayNumber + 1));
            }

            return (barLeft, barRight);
        }

        float? left = null, right = null;
        if (PlanOf(row) is { } plan)
        {
            left = X(plan.Start);
            right = X(plan.End.DayNumber + 1);
        }

        if (row.ActualStart is { } a && row.ActualEnd is { } b)
        {
            left = Math.Min(left ?? float.MaxValue, X(a));
            right = Math.Max(right ?? float.MinValue, X(b.DayNumber + 1));
        }

        return left is { } l && right is { } r ? (l, r) : null;
    }

    // ================================================================ 描画

    /// <summary>ガントへフォーカスを移す。選んでいる行がなければ先頭を選ぶ。</summary>
    public void FocusContent()
    {
        if (SelectedIndex < 0 && _rows.Count > 0)
        {
            Select(0);
        }

        Focus(FocusState.Programmatic);
    }

    /// <summary>指定したタスクの上下のタスクの行を選ぶ（詳細パネルの ↑↓。UX-10）。マイルストーンの行は飛ばす。</summary>
    internal TaskItem? Step(string itemId, int delta)
    {
        if (IsPreviewing)
        {
            return null;
        }

        var tasks = _rows.Where(r => !r.IsMilestone).ToList();
        int index = tasks.FindIndex(r => r.Task.ItemId == itemId);
        if (index < 0 || index + delta < 0 || index + delta >= tasks.Count)
        {
            return null;
        }

        var next = tasks[index + delta];
        Select(next.Index);
        return RealTask(next.Task.ItemId);
    }

    /// <summary>表示の単位を次へ切り替えてほしい（切り替えはプロジェクト画面が持つ）。</summary>
    public event EventHandler? ScaleCycleRequested;

    /// <summary>選んでいる行のタスク（保存されている値）。</summary>
    // XAML の型情報生成に載せないため internal にする（TaskItem は required メンバーを持つ）
    internal TaskItem? SelectedTask => SelectedIndex is >= 0 and var i && !_rows[i].IsMilestone ? RealTask(_rows[i].Task.ItemId) : null;

    /// <summary>選んでいる行の、ピッカーを出す位置（この部品の座標）。</summary>
    public Point? SelectedRowPoint => SelectedIndex is >= 0 and var i
        ? Chart.TransformToVisual(this).TransformPoint(new Point(24, DisplayTop(i) + RowHeight))
        : null;

    private async Task<bool> HandleShortcutAsync(Tasklabe.Core.Keyboard.KeyGesture gesture)
    {
        var keymap = App.Current.Services.Keymap;
        if (IsPreviewing && keymap.Find(gesture, Tasklabe.Core.Keyboard.ShortcutScope.Preview)?.Id == "preview.confirm")
        {
            OnConfirmPreview(this, new RoutedEventArgs());
            return true;
        }

        switch (keymap.Find(gesture, Tasklabe.Core.Keyboard.ShortcutScope.Gantt)?.Id)
        {
            case "gantt.today":
                ScrollToToday();
                return true;
            case "gantt.edit":
                IsEditing = !IsEditing;
                return true;
            case "gantt.scale":
                ScaleCycleRequested?.Invoke(this, EventArgs.Empty);
                return true;
            case "gantt.moveEarlier":
                await NudgeAsync(-1, -1);
                return true;
            case "gantt.moveLater":
                await NudgeAsync(1, 1);
                return true;
            case "gantt.shrink":
                await NudgeAsync(0, -1);
                return true;
            case "gantt.extend":
                await NudgeAsync(0, 1);
                return true;
            default:
                return false;
        }
    }

    /// <summary>選んでいるバーの予定を日単位で動かす（ドラッグと同じく、後続に影響があればプレビューへ入る）。</summary>
    private async Task NudgeAsync(int startDays, int endDays)
    {
        if (SelectedIndex is not (>= 0 and var i) || _rows[i] is not { Kind: GanttRowKind.Task } row || PlanOf(row) is not { } plan)
        {
            return;
        }

        var start = plan.Start.AddDays(startDays);
        var end = plan.End.AddDays(endDays);
        if (end < start)
        {
            return;
        }

        await CommitScheduleAsync(row, start, end);
    }

    /// <summary>
    /// 一覧・計画の表・カンバンと同じメニュー（UX 規約 UX-20）。ガントに固有の操作（予定のクリア、依存関係）は、
    /// 共通の項目のあとに区切って置く。
    /// </summary>
    /// <param name="position">右クリックした位置。null ならキー（Shift + F10）で開いたとき。</param>
    private async void ShowContextMenu(GanttRow row, UIElement target, Point? position)
    {
        if (IsPreviewing || row.IsMilestone)
        {
            return;
        }

        var task = row.Task;
        bool parent = row.Kind == GanttRowKind.Parent;
        var extras = new List<MenuEntry>
        {
            new MenuCommand("予定をクリア", "\uE894", () => App.Current.Services.Edits.ApplyChangesAsync(task,
                [.. TaskRules.Set(task, TaskField.Start, null).Concat(TaskRules.Set(task, TaskField.Target, null))]))
            {
                IsEnabled = !parent && (task.Start is not null || task.Target is not null),
            },
        };
        extras.AddRange(TaskMenu.DependencyEntries(new TaskTarget(task, this, SelectedRowPoint), row.Node!.HasChildren,
            _tree.All().Select(n => n.Task)));

        await TaskMenu.ShowAsync(new TaskTarget(task, this, position ?? SelectedRowPoint), target, position ?? SelectedRowPoint, extras, isParent: parent);
    }

    /// <summary>キー（Shift + F10、アプリケーションキー）でメニューを開く（UX 規約 UX-20）。</summary>
    private void OnContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (SelectedIndex is var index and >= 0 && !e.TryGetPosition(this, out _))
        {
            e.Handled = true;
            ShowContextMenu(_rows[index], this, null);
        }
    }

    // ================================================================ 依存関係による日程の調整（UI デザイン設計書 3.3.5 節）

    /// <summary>保存されている（プレビューを当てはめる前の）タスク。</summary>
    private TaskItem? RealTask(string itemId) => _tree.Find(itemId)?.Task;

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;
}
