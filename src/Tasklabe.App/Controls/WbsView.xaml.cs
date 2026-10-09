using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Tasklabe.App.ViewModels;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Editing;
using Tasklabe.Core.Wbs;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using VirtualKey = Windows.System.VirtualKey;

using Tasklabe.App.Services;

namespace Tasklabe.App.Controls;

/// <summary>
/// WBS の表（要件 F-UI-WBS-01〜03、UI デザイン設計書 3.4.4 節）。
/// 行の選択は ListView に任せ、選択中の列とセルの編集はこのコントロールで扱う。
/// </summary>
public sealed partial class WbsView : UserControl
{
    private const string NewTaskTitle = "新しいタスク";

    private readonly HashSet<string> _collapsed = [];
    private Project? _project;
    private TaskTree? _tree;

    /// <summary>絞り込みで表示するノード（null ならすべて）。</summary>
    private Func<TaskNode, bool>? _visible;
    private int _column;
    private WbsRowViewModel? _editingRow;
    private string? _pendingEditItemId;
    private bool _committing;

    /// <summary>GitHub での作成により置き換わったタスクの ID（旧 → 新）。</summary>
    private readonly Dictionary<string, string> _replacedIds = [];

    /// <summary>編集中、またはキー操作の直後に届いた再表示の要求。落ち着いてから反映する。</summary>
    private (Project Project, TaskTree Tree, Func<TaskNode, bool>? Visible)? _deferredLoad;

    /// <summary>キーボードでの操作を受け付けている間は再表示を待つ（連続した操作が落ちないようにする）。</summary>
    private readonly DispatcherTimer _settleTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };

    public WbsView()
    {
        InitializeComponent();

        // XAML から参照すると行の型のメンバーが型情報の生成対象になるため、コードで結び付ける
        List.ItemsSource = Rows;

        App.Current.Services.Store.ItemIdReplaced += (_, ids) =>
            DispatcherQueue.TryEnqueue(() => _replacedIds[ids.OldItemId] = ids.NewItemId);

        _settleTimer.Tick += (_, _) =>
        {
            _settleTimer.Stop();
            ApplyDeferredLoad();
        };

        InitializeSticky();
    }

    /// <summary>置き換え後の ID をたどる。</summary>
    private string Resolve(string itemId)
    {
        while (_replacedIds.TryGetValue(itemId, out var next))
        {
            itemId = next;
        }

        return itemId;
    }

    private ObservableCollection<WbsRowViewModel> Rows { get; } = [];

    /// <summary>担当者の列を表示するか（チームプロジェクトのみ）。</summary>
    private bool ShowAssignee => _project?.IsTeam == true;

    /// <summary>予定開始日は計画のための列。個人プロジェクトでは出さない（要件 F-UI-PR-01）。</summary>
    private bool ShowStart => _project?.IsTeam == true;

    /// <summary>
    /// 操作の中心になる行。複数を選んでいるときは、フォーカスのある行（なければ最後に 1 件だけ選んだ行）。
    /// </summary>
    private WbsRowViewModel? Current => FocusedRow() ?? (_anchor is { } a && List.SelectedItems.Contains(a) ? a : List.SelectedItem as WbsRowViewModel);

    /// <summary>最後に 1 件だけ選んだ行（複数選択の起点）。</summary>
    private WbsRowViewModel? _anchor;

    private WbsRowViewModel? FocusedRow()
    {
        foreach (var e in VisualTree.FocusedAncestors(XamlRoot))
        {
            if (e is ListViewItem { Content: WbsRowViewModel row })
            {
                return row;
            }

            if (ReferenceEquals(e, List))
            {
                break;
            }
        }

        return null;
    }

    /// <summary>折りたたみを覚えるキー（UX 規約 UX-23）。</summary>
    private string CollapsedKey => $"wbs-collapsed:{_project?.Id}";

    // ================================================================ 表示

    /// <summary>
    /// 表示を更新する。選択中の行と列、キーボードフォーカスは可能な限り保つ。
    /// </summary>
    /// <param name="isVisible">絞り込みで表示するノード（null ならすべて）。</param>
    public void Load(Project project, TaskTree tree, Func<TaskNode, bool>? isVisible = null)
    {
        // 編集中やキー操作の直後に行を作り直すと、入力やキーが失われるため落ち着くまで待つ
        if (Editor.Visibility == Visibility.Visible || _settleTimer.IsEnabled)
        {
            _deferredLoad = (project, tree, isVisible);
            return;
        }

        Reload(project, tree, isVisible);
    }

    /// <summary>この表の操作による再表示。待たせずにその場で反映する。</summary>
    private void Reload(Project project, TaskTree tree, Func<TaskNode, bool>? isVisible)
    {
        _visible = isVisible;

        var selectedId = Current?.ItemId is { } id ? Resolve(id) : null;
        bool hadFocus = HasKeyboardFocus();
        if (_project?.Id != project.Id)
        {
            _collapsed.Clear();
            _collapsed.UnionWith(ViewState.GetSet($"wbs-collapsed:{project.Id}"));
        }

        _project = project;
        _tree = tree;

        var today = AppClock.Today;
        var rows = tree.Flatten(n => !_collapsed.Contains(n.Task.ItemId))
            .Where(node => isVisible?.Invoke(node) ?? true)
            .Select(node => new WbsRowViewModel(node, !_collapsed.Contains(node.Task.ItemId), today))
            .ToList();
        Merge(rows);

        ApplyWidths(HeaderRow);

        var target = _pendingEditItemId is { } pending ? Resolve(pending) : selectedId;
        if (target is not null && Rows.FirstOrDefault(r => r.ItemId == target) is { } row)
        {
            // 行を作り直すとフォーカスのある行が消えるため、配置を確定させてから選択中の行へ戻す
            Select(row, focus: false);
            if (hadFocus || _pendingEditItemId is not null)
            {
                List.UpdateLayout();
                FocusCurrent();
            }

            if (_pendingEditItemId is not null)
            {
                _pendingEditItemId = null;
                StartTitleEdit(row);
            }
        }
    }

    /// <summary>
    /// 表示中の行を新しい行に合わせる。見た目の変わらない行はそのまま残し（タスクだけを差し替える）、
    /// 変わった行は入れ替え、増えた行・減った行だけを出し入れする。すべてを作り直すと、行の部品がすべて作り直されて表が点滅し、
    /// 開閉で増える行・減る行も見分けられない。
    /// </summary>
    private void Merge(IReadOnlyList<WbsRowViewModel> rows)
    {
        var wanted = rows.Select(r => r.ItemId).ToHashSet();
        for (int i = Rows.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(Rows[i].ItemId))
            {
                Rows.RemoveAt(i);
            }
        }

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (i < Rows.Count && Rows[i].ItemId == row.ItemId)
            {
                if (Rows[i].LooksSame(row))
                {
                    Rows[i].Adopt(row);
                }
                else
                {
                    Rows[i] = row;
                }

                continue;
            }

            // 並びが変わって後ろにある行は、いまの位置から外して入れ直す
            int existing = IndexOf(row.ItemId, i + 1);
            if (existing >= 0)
            {
                Rows.RemoveAt(existing);
            }

            Rows.Insert(i, row);
        }

        while (Rows.Count > rows.Count)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }

        int IndexOf(string itemId, int from)
        {
            for (int i = from; i < Rows.Count; i++)
            {
                if (Rows[i].ItemId == itemId)
                {
                    return i;
                }
            }

            return -1;
        }
    }

    /// <summary>新しく追加した行のタイトルの編集を始める。</summary>
    private void StartTitleEdit(WbsRowViewModel row)
    {
        _column = (int)WbsColumn.Title;
        row.ActiveColumn = _column;
        List.UpdateLayout();
        BeginTitleEdit(row);
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        // 担当者・開始の列はチームプロジェクトのときだけ、ほかの列は幅が足りるときだけ幅を持たせる
        ApplyWidths(args.ItemContainer.ContentTemplateRoot as Grid);
    }

    /// <summary>
    /// 選んでいるセルは、ピッカーのボタンと同じ面（塗りと濃い枠）で示す。ステータスのピッカーは、閉じるとこの面へ着地する。
    /// </summary>
    public static Brush CellFill(int active, int column) =>
        active == column
            ? ThemeResources.Brush("Morph.Trigger")
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    public static Brush CellBorder(int active, int column) =>
        active == column
            ? ThemeResources.Brush("Morph.StrokeHover")
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    // ================================================================ 選択

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var row in Rows.Where(r => r.ActiveColumn >= 0))
        {
            row.ActiveColumn = -1;
        }

        int count = List.SelectedItems.Count;
        Bar.Update(count);
        if (count == 1)
        {
            _anchor = List.SelectedItem as WbsRowViewModel;
        }

        if (Current is { } current)
        {
            current.ActiveColumn = _column;
        }

        RefreshStickyStates();
        if (count == 1 && _anchor is { } selectedRow)
        {
            // ListView がキーの操作で行を上端にそろえて止めると、上端に残した親に隠れる。止まった後で、隠れていれば見せる
            DispatcherQueue.TryEnqueue(() => RevealIfCovered(selectedRow));
        }

        // 詳細パネルを開いていれば選んだ行に追従させ、複数を選んだら閉じる（UX-07、UX-10）
        if (App.Current.Shell is { IsDetailOpen: true, IsCreating: false } shell)
        {
            if (count > 1)
            {
                shell.SelectTask(null);
            }
            else if (_anchor is { } one)
            {
                shell.SelectTask(one.Task);
            }
        }
    }

    /// <summary>タイトルのセルを押すと、その行とタイトルの列を選ぶ（行の選択は ListView が受け持つ）。</summary>
    private void OnTitleTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WbsRowViewModel row })
        {
            SetColumn(row, (int)WbsColumn.Title);
        }
    }

    /// <summary>値のセルを押すと、一覧の値と同じくその場でピッカーを開く。選んでいる行に入っていなければ、その行を選ぶ。</summary>
    private void OnValueCellClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag, DataContext: WbsRowViewModel row } && int.TryParse(tag, out var column))
        {
            Select(row, focus: false);
            SetColumn(row, column);
            StartEditing(row, (WbsColumn)column);
        }
    }

    /// <summary>番号とタイトルのダブルクリックで詳細を開く（一覧・ガントと同じ。UI デザイン設計書 3.4.4 節）。</summary>
    private void OnTitleDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (StickyDoubleTapped() is { } sticky)
        {
            App.Current.Shell?.SelectTask(sticky.Task);
            return;
        }

        if (sender is FrameworkElement { DataContext: WbsRowViewModel row })
        {
            App.Current.Shell?.SelectTask(row.Task);
        }
    }

    private void OnStatusIconClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: WbsRowViewModel row } anchor)
        {
            Select(row, focus: false);
            PickOn(row, anchor, "task.status");
        }
    }

    private void OnChevronClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: WbsRowViewModel row })
        {
            ToggleExpanded(row, !row.IsExpanded);
        }
    }

    private void Select(WbsRowViewModel row, bool focus)
    {
        if (!(List.SelectedItems.Count > 1 && List.SelectedItems.Contains(row)))
        {
            List.SelectedItem = row;
        }

        _anchor = row;
        row.ActiveColumn = _column;
        ScrollRowIntoView(row);
        if (focus)
        {
            DispatcherQueue.TryEnqueue(FocusCurrent);
        }
    }

    private void SetColumn(WbsRowViewModel row, int column)
    {
        // 表示していない列（個人プロジェクトの担当・開始、幅が足りずに隠した列）は飛ばす
        int step = column >= _column ? 1 : -1;
        column = Math.Clamp(column, 0, (int)WbsColumn.Progress);
        while (column > 0 && column < (int)WbsColumn.Progress && ColumnWidth(column) == 0)
        {
            column += step;
        }

        if (ColumnWidth(column) == 0)
        {
            column = _column;
        }

        _column = Math.Clamp(column, 0, (int)WbsColumn.Progress);
        row.ActiveColumn = _column;
    }

    // ================================================================ 幅に応じた列（UX 規約 UX-25）

    /// <summary>列の既定の幅: タスク、担当、ステータス、開始、終了、工数、進捗。</summary>
    /// <remarks>開始と終了は、親タスクの枠超え（「▲2日」）を添えられる幅にする。</remarks>
    private static readonly double[] DefaultWidths = [0, 120, 112, 132, 132, 72, 64];

    /// <summary>隠す順: 進捗 → 工数 → 担当 → 開始。タスク（状態を含む）、ステータス、終了（期日）は隠さない。</summary>
    private static readonly int[] HideOrder = [6, 5, 1, 3];

    /// <summary>タスク名の列に残す最小の幅。</summary>
    private const double MinTitleWidth = 240;

    private readonly HashSet<int> _hidden = [];

    private double ColumnWidth(int column) =>
        column == 0 ? 1
        : _hidden.Contains(column) ? 0
        : BaseWidth(column);

    /// <summary>隠す前の幅（担当と開始はチームプロジェクトだけ）。</summary>
    private double BaseWidth(int column) =>
        (column == (int)WbsColumn.Assignee && !ShowAssignee) || (column == (int)WbsColumn.Start && !ShowStart) ? 0
        : DefaultWidths[column];

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateHiddenColumns();

    private void UpdateHiddenColumns()
    {
        var hidden = new HashSet<int>();
        double used() => Enumerable.Range(1, DefaultWidths.Length - 1).Where(c => !hidden.Contains(c)).Sum(BaseWidth);
        bool minimal = ActualWidth > 0 && ActualWidth < 600;
        foreach (var column in HideOrder)
        {
            if (!minimal && (ActualWidth <= 0 || ActualWidth - 32 - used() >= MinTitleWidth))
            {
                break;
            }

            hidden.Add(column);
        }

        if (hidden.SetEquals(_hidden))
        {
            return;
        }

        _hidden.Clear();
        _hidden.UnionWith(hidden);
        ApplyWidths(HeaderRow);
        for (int i = 0; i < Rows.Count; i++)
        {
            if (List.ContainerFromIndex(i) is ListViewItem { ContentTemplateRoot: Grid grid })
            {
                ApplyWidths(grid);
            }
        }

        foreach (var host in _stickyHosts)
        {
            ApplyWidths(StickyTemplateRoot(host));
        }

        if (Current is { } row && ColumnWidth(_column) == 0)
        {
            SetColumn(row, _column);
        }
    }

    private void ApplyWidths(Grid? grid)
    {
        if (grid is null)
        {
            return;
        }

        for (int column = 1; column < DefaultWidths.Length; column++)
        {
            grid.ColumnDefinitions[column].Width = new GridLength(ColumnWidth(column));
        }

        foreach (var child in grid.Children.OfType<FrameworkElement>())
        {
            int column = Grid.GetColumn(child);
            child.Visibility = ColumnWidth(column) > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void FocusCurrent() => FocusCurrent(FocusState.Keyboard);

    private void FocusCurrent(FocusState state)
    {
        if (Current is { } row && List.ContainerFromItem(row) is ListViewItem container)
        {
            container.Focus(state);
        }
    }

    private bool HasKeyboardFocus() =>
        XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is DependencyObject focused && VisualTree.IsWithin(focused, List);

    private void ToggleExpanded(WbsRowViewModel row, bool expanded)
    {
        if (!row.HasChildren || row.IsExpanded == expanded)
        {
            return;
        }

        if (expanded)
        {
            _collapsed.Remove(row.ItemId);
        }
        else
        {
            _collapsed.Add(row.ItemId);
        }

        ViewState.SetSet(CollapsedKey, _collapsed);

        if (StickyRows.TopOf(_sticky, Rows.IndexOf(row)) is { } stickyTop)
        {
            KeepStickyPosition(row, stickyTop);
        }

        if (_project is not null && _tree is not null)
        {
            Load(_project, _tree);
        }
    }

    // ================================================================ キーボード

    private async void OnListPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (Editor.Visibility == Visibility.Visible || Current is not { } row)
        {
            return;
        }

        // 複数の選択を解き、フォーカスのある 1 件だけにする（UX 規約 UX-07、UX-14）
        if (e.Key == VirtualKey.Escape && List.SelectedItems.Count > 1)
        {
            e.Handled = true;
            List.SelectedItem = row;
            FocusCurrent();
            return;
        }

        // 範囲の選択と、選択を変えずにフォーカスだけを移す操作は ListView に任せる（UX-06、UX-07）
        if (e.Key is VirtualKey.Up or VirtualKey.Down && (IsDown(VirtualKey.Shift) || IsDown(VirtualKey.Control)))
        {
            return;
        }

        // 行の追加・字下げ・並べ替え・開閉は、キーの割り当てに従う（UI デザイン設計書 4.1 節）
        var keymap = App.Current.Services.Keymap;
        if (Services.KeyInput.From(e.Key) is { } gesture && keymap.Find(gesture, Tasklabe.Core.Keyboard.ShortcutScope.Wbs) is { } action)
        {
            e.Handled = true;
            switch (action.Id)
            {
                case "wbs.insertAfter":
                    await InsertAsync(row, asChild: false);
                    break;
                case "wbs.indent":
                    await ApplyStructureAsync(row, WbsOperations.Indent);
                    break;
                case "wbs.outdent":
                    await ApplyStructureAsync(row, WbsOperations.Outdent);
                    break;
                case "wbs.moveUp":
                    await ApplyStructureAsync(row, (tree, node) => WbsOperations.Move(tree, node, -1));
                    break;
                case "wbs.moveDown":
                    await ApplyStructureAsync(row, (tree, node) => WbsOperations.Move(tree, node, +1));
                    break;
                case "wbs.collapse":
                    ToggleExpanded(row, false);
                    break;
                case "wbs.expand":
                    ToggleExpanded(row, true);
                    break;
                case "wbs.bulk":
                    BulkRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }

            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Left:
                SetColumn(row, _column - 1);
                break;
            case VirtualKey.Right:
                SetColumn(row, _column + 1);
                break;
            case VirtualKey.Enter:
                // 一覧・ガントと同じく、Enter で詳細を開く（UX 規約の領域のキー）
                App.Current.Shell?.SelectTask(row.Task);
                break;
            case VirtualKey.F2:
                StartEditing(row, (WbsColumn)_column);
                break;
            case VirtualKey.Space when !IsDown(VirtualKey.Control):
                if (TaskShortcuts.FocusedTask(XamlRoot) is { } target)
                {
                    await TaskShortcuts.RunAsync("task.toggleDone", target);
                }

                break;
            case VirtualKey.Space:
                // Ctrl + Space はフォーカスのある行を選ぶ・外す（ListView に任せる）
                return;
            case VirtualKey.Tab:
                // 字下げに割り当てていないときも、表の外へフォーカスが抜けないようにする
                break;
            default:
                // それ以外（1 文字のショートカットや Delete）はウィンドウへ渡し、選んでいるタスクへの操作として扱う
                return;
        }

        e.Handled = true;
    }

    private static bool IsDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private async Task ApplyStructureAsync(WbsRowViewModel row, Func<TaskTree, TaskNode, IReadOnlyList<TaskChange>> operation)
    {
        if (_tree?.Find(row.ItemId) is not { } node)
        {
            return;
        }

        // 続けて押されるキーを取りこぼさないよう、同期による再表示は少しのあいだ待たせる
        _settleTimer.Stop();
        _settleTimer.Start();

        var changes = operation(_tree, node);
        if (changes.Count == 0)
        {
            return;
        }

        var updated = await App.Current.Services.Edits.ApplyChangesAsync(row.Task, changes);
        if (updated is not null && _project is { } project)
        {
            // 送信の完了を待たずに手元の木を作り直す。そうしないと、続けて押したキーが古い並びで計算される
            var tasks = _tree.All().Select(n => n.Task.ItemId == updated.ItemId ? updated : n.Task).ToList();
            Reload(project, TaskTree.Build(tasks), _visible);
        }
    }

    /// <summary>表へフォーカスを移す。選んでいる行がなければ先頭を選ぶ。</summary>
    public void FocusContent()
    {
        if ((Current ?? Rows.FirstOrDefault()) is { } row)
        {
            Select(row, focus: false);

            // 作り直した直後は行の部品がまだないため、配置を確定させてからフォーカスする
            List.UpdateLayout();
            if (List.ContainerFromItem(row) is ListViewItem container)
            {
                container.Focus(FocusState.Programmatic);
            }
        }
    }

    /// <summary>計画をまとめて入力するよう求めた（要件 F-UI-WBS-05）。</summary>
    public event EventHandler? BulkRequested;

    /// <summary>フォーカスのある行のタスク（まとめて入力の置き場所の既定に使う）。</summary>
    internal TaskItem? CurrentTask => Current?.Task;

    /// <summary>フォーカスのある行のタスク（画面を移るときに覚える。UX 規約 UX-23）。</summary>
    public string? FocusedItemId => Current?.ItemId;

    /// <summary>指定したタスクの上下の行を選ぶ（詳細パネルの ↑↓。UX-10）。</summary>
    internal TaskItem? Step(string itemId, int delta)
    {
        int index = Rows.ToList().FindIndex(r => r.ItemId == Resolve(itemId));
        if (index < 0 || index + delta < 0 || index + delta >= Rows.Count)
        {
            return null;
        }

        var next = Rows[index + delta];
        Select(next, focus: false);
        return next.Task;
    }

    /// <summary>指定したタスクの行を選び、フォーカスを移す。</summary>
    public void FocusItem(string itemId)
    {
        if (Rows.FirstOrDefault(r => r.ItemId == Resolve(itemId)) is { } row)
        {
            Select(row, focus: false);
            List.UpdateLayout();
            FocusCurrent();
        }
        else
        {
            FocusContent();
        }
    }

    /// <summary>選んでいる行の直後（または子）に行を追加する（コマンドパレットから）。</summary>
    public Task InsertHereAsync(bool asChild) => InsertAsync(Current, asChild);

    /// <summary>選んでいる行のタイトルの編集を始める（ショートカットのタイトル変更）。</summary>
    public void EditTitle()
    {
        if (Current is { } row)
        {
            _column = (int)WbsColumn.Title;
            StartEditing(row, WbsColumn.Title);
        }
    }

    /// <summary>指定したタスクの子の行を足し、タイトルの編集を始める（右クリックのメニューから）。</summary>
    public Task InsertChildAsync(TaskItem parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return InsertAsync(Rows.FirstOrDefault(r => r.ItemId == parent.ItemId), asChild: true);
    }

    /// <summary>足した直後で、まだ名前を決めていない行（Esc で取り消す）。</summary>
    private string? _justInsertedItemId;

    /// <summary>
    /// 新しいタスクを作り、タイトルの編集を始める。
    /// 既定では選択中の行の直後、<paramref name="asChild"/> のときは選択中の行の子として置く。
    /// </summary>
    public async Task InsertAsync(WbsRowViewModel? row, bool asChild = false)
    {
        if (_project is null || _tree is null)
        {
            return;
        }

        var node = row is null ? null : _tree.Find(row.ItemId);
        var (parent, order) = asChild && node is not null
            ? WbsOperations.PositionInside(node)
            : WbsOperations.PositionAfter(_tree, node);
        var created = await App.Current.Services.Edits.CreateAsync(_project, NewTaskTitle, parentIssueId: parent, sortOrder: order);
        _justInsertedItemId = created.ItemId;

        // 作成の通知による再表示が先に済んでいれば、ここで編集を始める。まだなら再表示の後に始める
        if (Rows.FirstOrDefault(r => r.ItemId == created.ItemId) is { } createdRow)
        {
            Select(createdRow, focus: false);
            StartTitleEdit(createdRow);
        }
        else
        {
            _pendingEditItemId = created.ItemId;
        }
    }

    // ================================================================ ドラッグでの並べ替えと親子の変更（要件 F-UI-WBS-04）

    private string? _draggingItemId;

    private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.FirstOrDefault() is not WbsRowViewModel row)
        {
            e.Cancel = true;
            return;
        }

        _draggingItemId = row.ItemId;
        e.Data.RequestedOperation = DataPackageOperation.Move;
        e.Data.SetText(row.Task.Title);
    }

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        if (DropTarget(e.GetPosition(List)) is not { } target)
        {
            HideDropHint();
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.IsGlyphVisible = false;
        e.DragUIOverride.Caption = target.Position switch
        {
            WbsOperations.DropPosition.Inside => $"「{target.Row.Task.Title}」の子にする",
            WbsOperations.DropPosition.Before => $"「{target.Row.Task.Title}」の前へ",
            _ => $"「{target.Row.Task.Title}」の後ろへ",
        };

        ShowDropHint(target.Row, target.Position);
        e.Handled = true;
    }

    private void OnListDragLeave(object sender, DragEventArgs e) => HideDropHint();

    private async void OnListDrop(object sender, DragEventArgs e)
    {
        HideDropHint();
        var dragged = _draggingItemId;
        _draggingItemId = null;

        if (_tree is null || dragged is null || DropTarget(e.GetPosition(List)) is not { } target
            || _tree.Find(dragged) is not { } node || _tree.Find(target.Row.ItemId) is not { } targetNode)
        {
            return;
        }

        e.Handled = true;
        var changes = WbsOperations.DropOn(_tree, node, targetNode, target.Position);
        if (changes.Count > 0)
        {
            await App.Current.Services.Edits.ApplyChangesAsync(node.Task, changes);
        }
    }

    /// <summary>落とす先の行と位置。行の上下の端なら前後、真ん中なら子にする。</summary>
    private (WbsRowViewModel Row, WbsOperations.DropPosition Position)? DropTarget(Point position)
    {
        for (int i = 0; i < Rows.Count; i++)
        {
            if (List.ContainerFromIndex(i) is not ListViewItem container)
            {
                continue;
            }

            var top = container.TransformToVisual(List).TransformPoint(new Point(0, 0)).Y;
            double height = container.ActualHeight;
            if (position.Y < top || position.Y > top + height)
            {
                continue;
            }

            double ratio = (position.Y - top) / height;
            var where = ratio switch
            {
                < 0.25 => WbsOperations.DropPosition.Before,
                > 0.75 => WbsOperations.DropPosition.After,
                _ => WbsOperations.DropPosition.Inside,
            };
            return (Rows[i], where);
        }

        return null;
    }

    private void ShowDropHint(WbsRowViewModel row, WbsOperations.DropPosition position)
    {
        int index = Rows.IndexOf(row);
        if (index < 0 || List.ContainerFromIndex(index) is not ListViewItem container)
        {
            return;
        }

        var top = container.TransformToVisual(EditorLayer).TransformPoint(new Point(0, 0)).Y;
        double height = container.ActualHeight;
        double width = List.ActualWidth;

        if (position == WbsOperations.DropPosition.Inside)
        {
            DropLine.Visibility = Visibility.Collapsed;
            DropInside.Width = width;
            DropInside.Height = height;
            Canvas.SetLeft(DropInside, 0);
            Canvas.SetTop(DropInside, top);
            DropInside.Visibility = Visibility.Visible;
            return;
        }

        DropInside.Visibility = Visibility.Collapsed;
        DropLine.Width = width;
        Canvas.SetLeft(DropLine, 0);
        Canvas.SetTop(DropLine, position == WbsOperations.DropPosition.Before ? top : top + height - 2);
        DropLine.Visibility = Visibility.Visible;
    }

    private void HideDropHint()
    {
        DropLine.Visibility = Visibility.Collapsed;
        DropInside.Visibility = Visibility.Collapsed;
    }

    // ================================================================ セルの編集

    /// <summary>
    /// セルの値を変える。タイトルはセルの上に重ねた入力欄で書き換え、ほかの値は一覧やカンバンと同じピッカーをそのセルに開く。
    /// </summary>
    private void StartEditing(WbsRowViewModel row, WbsColumn column)
    {
        if (row.IsReadOnly(column))
        {
            return;
        }

        if (column == WbsColumn.Title)
        {
            BeginTitleEdit(row);
            return;
        }

        if (FindCell(row, (int)column) is not { } cell)
        {
            return;
        }

        // 開始と終了は、どちらからも予定（開始〜終了）をまとめて選ぶ。開始を持たない個人のプロジェクトでは期日を選ぶ
        if (column is WbsColumn.Start or WbsColumn.Target && ShowStart)
        {
            PickPlan(row, cell);
            return;
        }

        PickOn(row, cell, column switch
        {
            WbsColumn.Status => "task.status",
            WbsColumn.Assignee => "task.assign",
            WbsColumn.Target => "task.due",
            WbsColumn.Estimate => "task.estimate",
            _ => "task.progress",
        });
    }

    private void BeginTitleEdit(WbsRowViewModel row)
    {
        if (FindCell(row, (int)WbsColumn.Title) is not { } cell)
        {
            return;
        }

        var position = cell.TransformToVisual(EditorLayer).TransformPoint(new Windows.Foundation.Point(0, 0));
        Canvas.SetLeft(Editor, position.X);
        Canvas.SetTop(Editor, position.Y);
        Editor.Width = Math.Max(cell.ActualWidth, 80);
        Editor.Height = cell.ActualHeight;

        _editingRow = row;
        Editor.Text = row.Task.Title;
        Editor.Visibility = Visibility.Visible;
        Editor.Focus(FocusState.Keyboard);
        Editor.SelectAll();
    }

    private async void OnEditorKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                await CommitEditAsync(moveDown: true);
                break;
            case VirtualKey.Tab:
                e.Handled = true;
                var row = _editingRow;
                await CommitEditAsync(moveDown: false);
                if (row is not null)
                {
                    SetColumn(row, _column + (IsDown(VirtualKey.Shift) ? -1 : 1));
                }

                break;
            case VirtualKey.Escape:
                e.Handled = true;
                await CancelEditAsync();
                break;
        }
    }

    /// <summary>
    /// 編集を取りやめる。足した直後の行で名前を決めていなければ、その行ごと取り消す（UX 規約 UX-21）。
    /// </summary>
    private async Task CancelEditAsync()
    {
        var row = _editingRow;
        bool justInserted = row is not null && _justInsertedItemId is { } id && Resolve(id) == Resolve(row.ItemId);
        EndEdit();
        _justInsertedItemId = null;
        if (justInserted && await App.Current.Services.Store.GetTaskAsync(Resolve(row!.ItemId)) is { } task && task.Title == NewTaskTitle)
        {
            await App.Current.Services.Edits.DeleteAsync(task);
        }
    }

    private async void OnEditorLostFocus(object sender, RoutedEventArgs e)
    {
        if (!_committing && Editor.Visibility == Visibility.Visible)
        {
            await CommitEditAsync(moveDown: false, refocus: false);
        }
    }

    private async Task CommitEditAsync(bool moveDown, bool refocus = true)
    {
        if (_editingRow is not { } row || _committing)
        {
            return;
        }

        _committing = true;
        try
        {
            var text = Editor.Text.Trim();

            // 編集中に GitHub での作成が終わり ID が変わっていても、最新のタスクに反映する
            var task = await App.Current.Services.Store.GetTaskAsync(Resolve(row.ItemId)) ?? row.Task;
            if (text.Length > 0)
            {
                await App.Current.Services.Edits.SetAsync(task, TaskField.Title, text);
            }

            EndEdit(refocus);
            if (moveDown && Rows.IndexOf(Rows.FirstOrDefault(r => r.ItemId == task.ItemId)!) is var index and >= 0 && index + 1 < Rows.Count)
            {
                List.SelectedIndex = index + 1;
                FocusCurrent();
            }
        }
        finally
        {
            _committing = false;
        }
    }

    private void EndEdit(bool refocus = true)
    {
        _editingRow = null;
        Editor.Visibility = Visibility.Collapsed;

        ApplyDeferredLoad();

        if (refocus)
        {
            FocusCurrent();
        }
    }

    private void ApplyDeferredLoad()
    {
        if (Editor.Visibility != Visibility.Visible && !_settleTimer.IsEnabled && _deferredLoad is { } deferred)
        {
            _deferredLoad = null;
            Load(deferred.Project, deferred.Tree, deferred.Visible);
        }
    }

    /// <summary>
    /// キーの操作（S・A・P・E・D）で開くピッカーの起点にする、その値のセル。行の左端から開くと、どのタスクを変えているのかを
    /// 示すタイトルがピッカーに隠れるため。列を隠しているときや、セルに当たらない操作では null（行から開く）。
    /// </summary>
    internal FrameworkElement? CellFor(TaskItem task, string actionId)
    {
        WbsColumn? column = actionId switch
        {
            "task.status" => WbsColumn.Status,
            "task.assign" => WbsColumn.Assignee,
            "task.progress" => WbsColumn.Progress,
            "task.estimate" => WbsColumn.Estimate,
            "task.due" => WbsColumn.Target,
            _ => null,
        };
        return column is { } c && ColumnWidth((int)c) > 0 && Rows.FirstOrDefault(r => r.ItemId == task.ItemId) is { } row
            ? FindCell(row, (int)c)
            : null;
    }

    /// <summary>選んでいる行のタスク。押した行が選択に入っていなければ、その行だけ。</summary>
    private List<TaskItem> TargetTasks(WbsRowViewModel row)
    {
        var selected = List.SelectedItems.OfType<WbsRowViewModel>().ToList();
        return selected.Contains(row) ? [.. selected.Select(r => r.Task)] : [row.Task];
    }

    /// <summary>一覧やカンバンと同じピッカーを、そのセル（または状態アイコン）に開く。選んでいる行すべてに効く。</summary>
    private async void PickOn(WbsRowViewModel row, FrameworkElement anchor, string actionId)
    {
        await TaskShortcuts.RunAsync(actionId, new TaskTarget(row.Task, anchor, Wbs: this, Selection: TargetTasks(row)));

        // ピッカーはまだ縮んでいる。ここでは枠を出さずにフォーカスだけを戻し、キーボードの枠はピッカーが閉じ終えたときに付く
        FocusCurrent(FocusState.Programmatic);
    }

    /// <summary>予定（開始〜終了）を、詳細パネルと同じ日付範囲のピッカーで選ぶ。選んでいる行すべてを同じ予定にする。</summary>
    private async void PickPlan(WbsRowViewModel row, FrameworkElement cell)
    {
        var tasks = TargetTasks(row);
        if (await DateRangePicker.ShowAsync(cell, "予定", row.Task.Start, row.Task.Target, ValuePickers.PlanPresets) is { } range)
        {
            await TaskCommands.ApplyAsync([.. tasks.Select(t => (t, (IReadOnlyList<TaskChange>)[
                .. TaskRules.Set(t, TaskField.Start, TaskValues.Date(range.Start)),
                .. TaskRules.Set(t, TaskField.Target, TaskValues.Date(range.End))]))],
                tasks.Count > 1 ? $"{tasks.Count} 件の予定を変えました" : null);
        }

        FocusCurrent(FocusState.Programmatic);
    }

    /// <summary>一覧やカンバンと同じメニュー（UX 規約 UX-20）。複数を選んでいれば、選んでいるすべてに効く。</summary>
    private async void OnRowContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not WbsRowViewModel row || _project is null)
        {
            return;
        }

        e.Handled = true;
        var selected = List.SelectedItems.OfType<WbsRowViewModel>().ToList();
        if (!selected.Contains(row))
        {
            Select(row, focus: false);
            selected = [row];
        }

        bool hasPosition = e.TryGetPosition(sender, out var position);
        var anchor = hasPosition ? (FrameworkElement)sender : (FrameworkElement)e.OriginalSource;
        var target = new TaskTarget(row.Task, anchor, hasPosition ? position : null, this, [.. selected.Select(r => r.Task)]);
        var extras = TaskMenu.DependencyEntries(target, row.HasChildren, _tree?.All().Select(n => n.Task) ?? []);
        await TaskMenu.ShowAsync(target, anchor, hasPosition ? position : null, extras, isParent: row.HasChildren && selected.Count == 1);
    }

    // ================================================================ ビジュアルツリー

    private FrameworkElement? FindCell(WbsRowViewModel row, int column)
    {
        ScrollRowIntoView(row);
        List.UpdateLayout();
        return List.ContainerFromItem(row) is DependencyObject container
            ? VisualTree.Descendants<FrameworkElement>(container).FirstOrDefault(e => e.Tag is string tag && tag == column.ToString(CultureInfo.InvariantCulture))
            : null;
    }
}
