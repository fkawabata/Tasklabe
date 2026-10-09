using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Tasklabe.App.Services;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Editing;
using Tasklabe.Core.Gantt;
using Tasklabe.Core.Scheduling;
using Tasklabe.Core.Wbs;
using Windows.Foundation;
using Windows.System;

namespace Tasklabe.App.Controls;

/// <summary>
/// ガントチャートのポインターの操作（要件 F-UI-GT-08、09）。編集モード、バーのドラッグ、端での自動スクロール、依存関係を張る操作。
/// </summary>
public sealed partial class GanttView
{
    /// <summary>
    /// 編集モードか。閲覧モードでは、チャートのドラッグで表示する場所を動かし、バーは動かさない。
    /// 編集モードでは、バーのドラッグで日程を変え、バーの外のドラッグで表示する場所を動かす。
    /// </summary>
    public bool IsEditing
    {
        get => _editing;
        set
        {
            if (_editing == value)
            {
                return;
            }

            _editing = value;
            Root.BorderBrush = ThemeResources.Brush(value ? "Brand.Accent" : "CardStrokeColorDefaultBrush");
            Root.BorderThickness = new Thickness(value ? 2 : 1);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, value ? "ガントチャート（編集モード）" : "ガントチャート");
            EditingChanged?.Invoke(this, EventArgs.Empty);
            RequestRender();
        }
    }

    /// <summary>編集モードが切り替わった（ツールバーのボタンを合わせる）。</summary>
    public event EventHandler? EditingChanged;

    /// <summary>バーを動かせるか。日程の調整のプレビュー中は、微調整のため常に動かせる。</summary>
    private bool CanEditBars => _editing || IsPreviewing;

    /// <summary>ドラッグがチャートの端の近くにあれば、少しずつ画面を動かす（編集のドラッグのあいだだけ）。</summary>
    private void UpdateAutoScroll()
    {
        if (_drag is { Mode: not DragMode.Pan } && AutoScrollVelocity(_drag.Pointer) != default)
        {
            if (_autoScrollTimer is null)
            {
                _autoScrollTimer = DispatcherQueue.CreateTimer();
                _autoScrollTimer.Interval = TimeSpan.FromMilliseconds(16);
                _autoScrollTimer.Tick += OnAutoScrollTick;
            }

            if (!_autoScrollTimer.IsRunning)
            {
                _autoScrollTimer.Start();
            }
        }
        else
        {
            _autoScrollTimer?.Stop();
        }
    }

    private void OnAutoScrollTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (_drag is not { Mode: not DragMode.Pan } drag)
        {
            sender.Stop();
            return;
        }

        var (vx, vy) = AutoScrollVelocity(drag.Pointer);
        if (vx == 0 && vy == 0)
        {
            sender.Stop();
            return;
        }

        ExtendRangeIfNeeded(_scrollX + vx);
        ScrollTo(_scrollX + vx, _scrollY + vy);
        UpdateDrag(drag, drag.Pointer);
        RequestRender();
    }

    /// <summary>端からの近さに応じた速さ（1 フレームあたりの px）。端の 48 px の中で、端に近いほど速く動かす。</summary>
    private (double X, double Y) AutoScrollVelocity(Point p)
    {
        const double Zone = 48;
        const double MaxSpeed = 18;
        static double Speed(double distance) => distance >= Zone ? 0 : MaxSpeed * Math.Pow(1 - Math.Max(distance, 0) / Zone, 2);

        double width = Chart.ActualWidth;
        double vx = p.X < Zone ? -Speed(p.X) : p.X > width - Zone ? Speed(width - p.X) : 0;

        // 上下は依存関係の線を引くときだけ動かす（日程のドラッグは行が変わらないため）
        double vy = 0;
        if (_drag?.Mode == DragMode.Link)
        {
            double top = p.Y - HeaderHeight;
            double bottom = HeaderHeight + BodyHeight - p.Y;
            vy = top < Zone ? -Speed(top) : bottom < Zone ? Speed(bottom) : 0;
        }

        return (vx, vy);
    }

    private enum DragMode
    {
        Move,
        ResizeStart,
        ResizeEnd,
        Create,
        Link,

        /// <summary>表示する場所を動かす（閲覧モードのドラッグ、編集モードのバーの外のドラッグ）。</summary>
        Pan,
    }

    private sealed class Drag
    {
        public required DragMode Mode { get; init; }

        /// <summary>ドラッグしているタスク。行番号は再表示で変わるため ID で持つ。</summary>
        public required string ItemId { get; init; }

        public required double StartX { get; init; }

        public double StartY { get; init; }

        /// <summary>押したときのスクロール位置。自動スクロールや範囲の拡張のあいだも、日付の差を内容の座標で求める。</summary>
        public double StartScrollX { get; set; }

        public double StartScrollY { get; init; }

        /// <summary>ポインターが動いたか。押しただけの操作で予定を作らないために見る。</summary>
        public bool Moved { get; set; }

        public DateOnly OriginalStart { get; init; }

        public DateOnly OriginalEnd { get; init; }

        public DateOnly NewStart { get; set; }

        public DateOnly NewEnd { get; set; }

        public Point Pointer { get; set; }

        public string? LinkTargetItemId { get; set; }

        public bool Changed => Mode != DragMode.Pan && (NewStart != OriginalStart || NewEnd != OriginalEnd || (Mode == DragMode.Create && Moved));
    }

    /// <summary>ドラッグしているタスクの現在の行。再表示で消えていれば null。</summary>
    private GanttRow? RowOf(Drag? drag) =>
        drag is null ? null : _rows.FirstOrDefault(r => r.Key == drag.ItemId);

    private (DragMode Mode, int Row)? HitTest(Point position)
    {
        // 上端に残している親の行は、押すと本来の位置へ戻すだけにし、バーはつかませない
        if (position.Y < HeaderHeight || IsOnSticky(position.Y - HeaderHeight))
        {
            return null;
        }

        int index = RowAt(position.Y - HeaderHeight);
        if (index < 0)
        {
            return null;
        }

        if (!CanEditBars)
        {
            return null;
        }

        var row = _rows[index];
        if (row.IsMilestone)
        {
            // マイルストーンの期日は編集のダイアログで変える。バーは動かさず、依存関係も張らない
            return null;
        }

        float x = (float)position.X;
        float cy = RowCenter(index);
        bool editable = row.Kind != GanttRowKind.Parent;

        if (!IsPreviewing && BarExtent(row) is { } extent
            && Math.Abs(x - (extent.Right + LinkHandleOffset)) <= 6 && Math.Abs(position.Y - cy) <= 7)
        {
            return (DragMode.Link, index);
        }

        if (!editable)
        {
            return null;
        }

        if (PlanOf(row) is not { } plan)
        {
            return IsPreviewing ? null : (DragMode.Create, index);
        }

        float left = X(plan.Start);
        float right = X(plan.End.DayNumber + 1);
        if (Math.Abs(position.Y - cy) > BarHeight / 2 + 4)
        {
            return null;
        }

        if (Math.Abs(x - left) <= EdgeGrip)
        {
            return (DragMode.ResizeStart, index);
        }

        if (Math.Abs(x - right) <= EdgeGrip)
        {
            return (DragMode.ResizeEnd, index);
        }

        return x > left && x < right ? (DragMode.Move, index) : null;
    }

    private void OnChartPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ChartInput);
        Focus(FocusState.Pointer);

        // 右ボタンの押下を処理済みにすると右クリックのメニューが出なくなるため、左ボタンだけを扱う
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        var position = point.Position;
        if (MilestoneAt(position) is { } milestone)
        {
            HideHover();
            MilestoneInvoked?.Invoke(this, milestone.Milestone);
            return;
        }

        int index = position.Y >= HeaderHeight ? RowAt(position.Y - HeaderHeight) : -1;
        if (index >= 0 && IsOnSticky(position.Y - HeaderHeight))
        {
            // 上端に残している親の行を押したら、その行を本来の位置へ戻して選ぶ。戻した後の位置で、別の行のドラッグや画面の移動を始めない
            SelectSticky(index);
            return;
        }

        if (index >= 0)
        {
            Select(index);
        }

        if (HitTest(position) is { } hit && _project is not null)
        {
            var row = _rows[hit.Row];
            var plan = PlanOf(row);
            var day = DateAt(position.X);
            _drag = new Drag
            {
                Mode = hit.Mode,
                ItemId = row.Task.ItemId,
                StartX = position.X,
                OriginalStart = plan?.Start ?? day,
                OriginalEnd = plan?.End ?? day,
                NewStart = plan?.Start ?? day,
                NewEnd = plan?.End ?? day,
                Pointer = position,
                StartScrollX = _scrollX,
                StartScrollY = _scrollY,
            };
            ChartInput.CapturePointer(e.Pointer);
            HideHover();
            return;
        }

        // バーの外（閲覧モードではどこでも）: 表示する場所を動かす
        _scrollAnimation?.Stop();
        _drag = new Drag
        {
            Mode = DragMode.Pan,
            ItemId = "",
            StartX = position.X,
            StartY = position.Y,
            StartScrollX = _scrollX,
            StartScrollY = _scrollY,
            Pointer = position,
        };
        ChartInput.CapturePointer(e.Pointer);
    }

    /// <summary>ポインターの位置からドラッグ後の日付を求める。日付の差は内容の座標（スクロール位置を足した値）で測る。</summary>
    private void UpdateDrag(Drag drag, Point position)
    {
        drag.Pointer = position;
        drag.Moved |= Math.Abs(position.X - drag.StartX) >= 3 || Math.Abs(position.Y - drag.StartY) >= 3;
        int delta = (int)Math.Round((position.X + _scrollX - (drag.StartX + drag.StartScrollX)) / _dayWidth);
        switch (drag.Mode)
        {
            case DragMode.Move:
                drag.NewStart = drag.OriginalStart.AddDays(delta);
                drag.NewEnd = drag.OriginalEnd.AddDays(delta);
                break;
            case DragMode.ResizeStart:
                drag.NewStart = Min(drag.OriginalStart.AddDays(delta), drag.OriginalEnd);
                break;
            case DragMode.ResizeEnd:
                drag.NewEnd = Max(drag.OriginalEnd.AddDays(delta), drag.OriginalStart);
                break;
            case DragMode.Create:
                var day = DateAt(position.X);
                drag.NewStart = Min(drag.OriginalStart, day);
                drag.NewEnd = Max(drag.OriginalStart, day);
                break;
            case DragMode.Link:
                int target = position.Y >= HeaderHeight && !IsOnSticky(position.Y - HeaderHeight) ? RowAt(position.Y - HeaderHeight) : -1;
                drag.LinkTargetItemId = target >= 0 && !_rows[target].IsMilestone && _rows[target].Key != drag.ItemId
                    ? _rows[target].Key
                    : null;
                break;
        }
    }

    private void OnChartPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(ChartInput).Position;

        if (_drag is { Mode: DragMode.Pan } pan)
        {
            // 押した位置の内容がポインターに付いてくるように動かす
            pan.Pointer = position;
            pan.Moved |= Math.Abs(position.X - pan.StartX) >= 3 || Math.Abs(position.Y - pan.StartY) >= 3;
            if (pan.Moved)
            {
                ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
                double x = pan.StartScrollX - (position.X - pan.StartX);
                ExtendRangeIfNeeded(x);
                ScrollTo(pan.StartScrollX - (position.X - pan.StartX), pan.StartScrollY - (position.Y - pan.StartY));
            }

            return;
        }

        if (_drag is { } drag)
        {
            UpdateDrag(drag, position);
            UpdateAutoScroll();
            RequestRender();
            return;
        }

        // マイルストーンの名前に重ねると、期限までの集計を示す
        if (MilestoneAt(position) is { } milestone)
        {
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
            ShowMilestoneHover(milestone, position);
            return;
        }

        // ホバー: 行の強調、ポインターの形、詳細の表示
        int hover = position.Y >= HeaderHeight ? RowAt(position.Y - HeaderHeight) : -1;
        if (hover != _hoverRow)
        {
            _hoverRow = hover;
            RequestRender();
        }

        var hit = HitTest(position);
        ProtectedCursor = InputSystemCursor.Create(hit?.Mode switch
        {
            DragMode.ResizeStart or DragMode.ResizeEnd => InputSystemCursorShape.SizeWestEast,
            DragMode.Move => InputSystemCursorShape.SizeAll,
            DragMode.Link => InputSystemCursorShape.Cross,
            _ => InputSystemCursorShape.Arrow,
        });

        if (hover >= 0 && BarExtent(_rows[hover]) is { } extent && position.X >= extent.Left - 4 && position.X <= extent.Right + 4)
        {
            if (_rows[hover].Milestone is { } summary)
            {
                ShowMilestoneHover(summary, position);
            }
            else
            {
                ShowHover(_rows[hover], position);
            }
        }
        else
        {
            HideHover();
        }
    }

    private async void OnChartPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not { } drag)
        {
            return;
        }

        _drag = null;
        _autoScrollTimer?.Stop();
        ChartInput.ReleasePointerCapture(e.Pointer);
        if (drag.Mode == DragMode.Pan)
        {
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
            return;
        }

        if (RowOf(drag) is not { } row)
        {
            // 再表示でタスクが表示範囲から消えた場合は、何もしない
            RequestRender();
            return;
        }

        if (drag.Mode == DragMode.Link)
        {
            RequestRender();
            if (_rows.FirstOrDefault(r => r.Key == drag.LinkTargetItemId) is { } target)
            {
                await AddDependencyAsync(row, target);
            }

            return;
        }

        if (!drag.Changed)
        {
            RequestRender();
            return;
        }

        _committed = drag;
        await CommitScheduleAsync(row, drag.NewStart, drag.NewEnd);
    }

    /// <summary>
    /// 予定の変更を反映する。後続を動かす必要があれば保存せずにプレビューへ入り、プレビュー中なら微調整として扱う。
    /// </summary>
    private async Task CommitScheduleAsync(GanttRow row, DateOnly start, DateOnly end)
    {
        var dates = PlanDatesFor(RealTask(row.Task.ItemId) ?? row.Task, start, end);
        if (IsPreviewing)
        {
            // プレビュー中の微調整: 動かしたタスクは利用者の指定として固定し、その後続を求め直す
            _committed = null;
            _pinned![row.Task.ItemId] = dates;
            Rebuild();
            UpdatePreviewBar();
            if (GanttModel.Extent(_rows) is { } extent && extent.End.DayNumber - _origin.DayNumber + 60 > _dayCount)
            {
                _dayCount = extent.End.DayNumber - _origin.DayNumber + 60;
                UpdateScrollBars();
            }

            LayoutRows(rebind: true);
            RequestRender();
            return;
        }

        // 後続を動かす必要があれば、保存せずにプレビューへ入る（要件 F-DEP-03）
        var pinned = new Dictionary<string, PlanDates>(StringComparer.Ordinal) { [row.Task.ItemId] = dates };
        if (DependencyScheduler.Resolve(_tree, Today, pinned, pinned.Keys).Count > 0)
        {
            _committed = null;
            BeginPreview(pinned, pinned.Keys.ToList());
            return;
        }

        RequestRender();
        await ApplyScheduleAsync(RealTask(row.Task.ItemId) ?? row.Task, dates);
    }

    // ================================================================ ショートカット（UI デザイン設計書 4.1 節）

    private void OnChartPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _drag = null;
        _autoScrollTimer?.Stop();
        RequestRender();
    }

    private void OnChartPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is null)
        {
            _hoverRow = -1;
            HideHover();
            RequestRender();
        }
    }

    private void OnChartDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var position = e.GetPosition(ChartInput);
        if (StickyDoubleTapped() is { } sticky)
        {
            OpenDetail(sticky);
            return;
        }

        int index = position.Y >= HeaderHeight ? RowAt(position.Y - HeaderHeight) : -1;
        if (index >= 0)
        {
            OpenDetail(_rows[index]);
        }
    }

    private void OnChartRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var position = e.GetPosition(ChartInput);

        // 日付の軸の右クリック: その日にマイルストーンを置く
        if (position.Y < HeaderHeight && _project is { IsTeam: true })
        {
            MilestoneRequested?.Invoke(this, MilestoneAt(position)?.Milestone.Due ?? DateAt(position.X));
            e.Handled = true;
            return;
        }

        int index = position.Y >= HeaderHeight ? RowAt(position.Y - HeaderHeight) : -1;
        if (index >= 0)
        {
            SelectForMenu(index, position.Y - HeaderHeight);
            ShowContextMenu(_rows[index], ChartInput, position);
            e.Handled = true;
        }
    }

    // ================================================================ 編集

    /// <summary>予定の組をそのまま保存する（入っていない側の日付は書き込まない）。</summary>
    private static async Task ApplyScheduleAsync(TaskItem task, PlanDates dates)
    {
        var changes = TaskRules.Set(task, TaskField.Start, TaskValues.Date(dates.Start))
            .Concat(TaskRules.Set(task, TaskField.Target, TaskValues.Date(dates.Target)))
            .ToList();
        if (changes.Count > 0)
        {
            await App.Current.Services.Edits.ApplyChangesAsync(task, changes);
        }
    }

    /// <summary>predecessor の完了後に successor を始める依存関係を追加する（要件 F-DEP-01、02）。</summary>
    private async Task AddDependencyAsync(GanttRow predecessor, GanttRow successor)
    {
        var pred = predecessor.Task;
        var succ = successor.Task;
        var check = DependencyScheduler.CanLink(_tree, pred, succ);
        if (check != LinkCheck.Ok)
        {
            if (DependencyLinks.Explain(check, pred, succ) is { } problem)
            {
                App.Current.Shell?.ShowInfo("依存関係を追加できません", problem);
            }

            return;
        }

        // 張ると後続に食い違いが出るなら、まだ保存せずに張った状態で調整のプレビューに入る（要件 F-DEP-03）。
        // 確定したときに、依存関係と後続の予定を 1 回の操作として保存する（Ctrl + Z で両方が戻る）。取り消せば何も保存しない
        var link = new PendingLink(pred.ItemId, succ.ItemId, pred.IssueId);
        var linked = WithLink(_tree, link);
        if (DependencyScheduler.Resolve(linked, Today, downstreamOf: [pred.ItemId]).Count == 0)
        {
            await App.Current.Services.Edits.SetBlockedByAsync(succ, succ.BlockedBy.Append(pred.IssueId));
            return;
        }

        _pendingLink = link;
        _tree = linked;
        if (!BeginPreview(downstreamOf: [pred.ItemId]))
        {
            _pendingLink = null;
            _tree = WithoutLink(_tree, link);
        }
    }

    /// <summary>まだ保存していない依存関係（先行と後続の ItemId、先行の Issue の ID）。</summary>
    private sealed record PendingLink(string Predecessor, string Successor, string PredecessorIssueId);

    /// <summary>後続に依存関係を足した木（保存前のプレビュー用）。</summary>
    private static TaskTree WithLink(TaskTree tree, PendingLink link) => TaskTree.Build(tree.All().Select(n =>
        n.Task.ItemId == link.Successor && !n.Task.BlockedBy.Contains(link.PredecessorIssueId)
            ? n.Task with { BlockedBy = [.. n.Task.BlockedBy, link.PredecessorIssueId] }
            : n.Task));

    /// <summary>保存前の依存関係を外した木（プレビューを取り消したとき）。</summary>
    private static TaskTree WithoutLink(TaskTree tree, PendingLink link) => TaskTree.Build(tree.All().Select(n =>
        n.Task.ItemId == link.Successor
            ? n.Task with { BlockedBy = [.. n.Task.BlockedBy.Where(b => b != link.PredecessorIssueId)] }
            : n.Task));
}
