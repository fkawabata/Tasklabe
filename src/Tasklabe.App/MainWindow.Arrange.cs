using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Tasklabe.Animation;
using Tasklabe.App.ViewModels;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace Tasklabe.App;

/// <summary>
/// ナビゲーションの幅の変更と、プロジェクトのドラッグでの並べ替え（UI デザイン設計書 3.1 節、要件 F-UI-NAV-01、02）。
/// めったに使わない見た目の調整のため、キーの操作は置かない（UX 規約 UX-21）。
/// </summary>
public sealed partial class MainWindow
{
    private const double DefaultPaneWidth = 240;
    private const double MaxPaneWidth = 400;

    /// <summary>
    /// いちばん狭い幅。閉じてアイコンだけにしたときに NavigationView が描く幅（CompactPaneLength）とし、
    /// ここまで縮めたときに閉じた形へ切り替える。見た目の幅が変わらないまま閉じるため、急に閉じたようには見えない。
    /// </summary>
    private double MinPaneWidth => NavView.CompactPaneLength;
    private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(220);

    private double? _resizeBase;

    /// <summary>既定の幅へ戻している途中の動き。</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _paneAnimation;

    /// <summary>覚えた幅を戻し、つまみと落とす位置の線を用意する。コンストラクターから呼ぶ。</summary>
    private void InitializeArrange()
    {
        NavView.OpenPaneLength = Math.Clamp(_services.CurrentSettings.NavPaneWidth, MinPaneWidth + 1, MaxPaneWidth);
        NavSizer.Resizing += (_, delta) =>
        {
            // 閉じているときは、アイコンだけの幅から右へ引き出して開く
            _resizeBase ??= NavView.IsPaneOpen ? NavView.OpenPaneLength : MinPaneWidth;
            double width = Math.Round(Math.Clamp(_resizeBase.Value + delta, MinPaneWidth, MaxPaneWidth));
            if (width <= MinPaneWidth)
            {
                // 閉じたときと同じ幅まで縮めたら、ハンバーガーボタンで閉じたのと同じ扱いにする
                if (NavView.IsPaneOpen)
                {
                    CollapsePane();

                    // つまみはアイコンだけの帯の右端へ移る。押したまま右へ戻せば、その位置からまた開く
                    _resizeBase = MinPaneWidth - delta;
                }

                return;
            }

            SetPaneWidth(width);
            if (!NavView.IsPaneOpen)
            {
                NavView.IsPaneOpen = true;
            }
        };
        NavSizer.Resized += (_, _) =>
        {
            _resizeBase = null;
            if (NavView.IsPaneOpen)
            {
                SavePaneWidth();
            }
        };
        NavSizer.ResetRequested += (_, _) =>
        {
            if (!NavView.IsPaneOpen)
            {
                NavView.OpenPaneLength = DefaultPaneWidth;
                NavView.IsPaneOpen = true;
                SavePaneWidth();
                return;
            }

            AnimatePaneWidth(DefaultPaneWidth, SavePaneWidth);
        };
        UpdateSizer();

        NavView.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnNavPointerMoved), true);
        NavView.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnNavPointerReleased), true);
        // ボタンを離すと、離した通知より先に捕まえたポインターを失う。離したのなら落とし、それ以外（ウィンドウを離れたなど）は取りやめる
        NavView.PointerCaptureLost += (_, e) => EndProjectDrag(drop: !e.GetCurrentPoint(NavView).Properties.IsLeftButtonPressed);

        // 線は、位置を変えると滑らかに移り、出し入れは淡く切り替わる
        var line = ElementCompositionPreview.GetElementVisual(NavDropLine);
        line.ImplicitAnimations = SlideAndFade(line.Compositor);
    }

    // ---------------------------------------------------------------- 幅

    /// <summary>
    /// 閉じた（アイコンだけの）形にする。次にハンバーガーボタンで開いたときは、既定の幅で開く。
    /// </summary>
    private void CollapsePane()
    {
        NavView.IsPaneOpen = false;
        NavView.OpenPaneLength = DefaultPaneWidth;
        SavePaneWidth();
        UpdateSizer();
    }

    private void SetPaneWidth(double width)
    {
        NavView.OpenPaneLength = Math.Round(Math.Clamp(width, MinPaneWidth, MaxPaneWidth));
        UpdateSizer();
    }

    /// <summary>既定の幅へ戻すときは、少しずつ動かして戻ったことが分かるようにする。</summary>
    private void AnimatePaneWidth(double target, Action done)
    {
        double from = NavView.OpenPaneLength;
        _paneAnimation?.Stop();
        _paneAnimation = DispatcherQueue.Tween(SlideDuration, p => SetPaneWidth(from + (target - from) * p), done);
    }

    private void SavePaneWidth()
    {
        _services.CurrentSettings.NavPaneWidth = NavView.OpenPaneLength;
        _services.SaveSettings();
    }

    /// <summary>
    /// つまみは、ナビゲーションを横に並べて置いているとき（Expanded）に、ナビゲーションの右端に置く。
    /// 閉じてアイコンだけのときは、その帯の右端に置き、右へ引き出して開けるようにする。
    /// </summary>
    private void UpdateSizer()
    {
        bool docked = NavView.DisplayMode == NavigationViewDisplayMode.Expanded && NavView.Visibility == Visibility.Visible;
        NavSizer.Visibility = docked ? Visibility.Visible : Visibility.Collapsed;
        double edge = NavView.IsPaneOpen ? NavView.OpenPaneLength : MinPaneWidth;
        NavSizer.Margin = new Thickness(edge - NavSizer.Width / 2, 0, 0, 0);
    }

    private void OnNavDisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args) => UpdateSizer();

    private void OnNavPaneChanged(NavigationView sender, object args) => UpdateSizer();

    // ---------------------------------------------------------------- プロジェクトの並べ替え

    // NavigationViewItem は押したポインターを自分で捕まえるため、システムのドラッグ＆ドロップは始まらない。
    // ポインターの動きから自前でドラッグを組み立て、つかんだ項目をポインターに付いて動かす。

    private const double DragThreshold = 6;

    private readonly Dictionary<NavigationViewItem, ProjectNavItem> _navProjects = [];
    private NavigationViewItem? _pressedItem;
    private Point _pressPoint;
    private NavigationViewItem? _dragItem;
    private double _dragDelta;
    private (NavigationViewItem Item, bool After)? _dropAt;

    /// <summary>プロジェクトの項目をドラッグで動かせるようにする。</summary>
    private void EnableProjectDrag(NavigationViewItem item, ProjectNavItem project)
    {
        _navProjects[item] = project;

        // 並びが変わると、ほかの項目は新しい位置へ滑るように動く
        var visual = ElementCompositionPreview.GetElementVisual(item);
        visual.ImplicitAnimations = SlideAndFade(visual.Compositor);
        ElementCompositionPreview.SetIsTranslationEnabled(item, true);

        item.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (e.GetCurrentPoint(item).Properties.IsLeftButtonPressed)
            {
                _pressedItem = item;
                _pressPoint = e.GetCurrentPoint(NavView).Position;
            }
        }), true);
    }

    private void OnNavPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_pressedItem is not { } pressed)
        {
            return;
        }

        var point = e.GetCurrentPoint(NavView);
        if (!point.Properties.IsLeftButtonPressed)
        {
            EndProjectDrag(drop: false);
            return;
        }

        double dy = point.Position.Y - _pressPoint.Y;
        if (_dragItem is null)
        {
            if (Math.Abs(dy) < DragThreshold && Math.Abs(point.Position.X - _pressPoint.X) < DragThreshold)
            {
                return;
            }

            // つかんだ項目を持ち上げる。ポインターをナビゲーションに移し、離したときに項目が選ばれないようにする
            _dragItem = pressed;
            NavView.CapturePointer(e.Pointer);
            var lifted = ElementCompositionPreview.GetElementVisual(pressed);
            lifted.CenterPoint = new((float)pressed.ActualWidth / 2, (float)pressed.ActualHeight / 2, 0);
            lifted.Scale = new(1.03f, 1.03f, 1);
            lifted.Opacity = 0.85f;
        }

        // 同じ見出しの範囲の中だけで、ポインターに付いて上下に動く
        _dragDelta = ClampToGroup(_dragItem, dy);
        ElementCompositionPreview.GetElementVisual(_dragItem).Properties.InsertVector3("Translation", new(0, (float)_dragDelta, 0));

        _dropAt = DropTarget(_dragItem, point.Position.Y);
        if (_dropAt is { } at)
        {
            ShowDropLine(at.Item, at.After, isSelf: ReferenceEquals(at.Item, _dragItem));
        }
        else
        {
            HideDropLine();
        }

        e.Handled = true;
    }

    private void OnNavPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        bool dragging = _dragItem is not null;

        // 捕まえたポインターを放すと取りやめの通知が来るため、先に下ろしてから放す
        EndProjectDrag(drop: true);
        if (dragging)
        {
            NavView.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    /// <summary>ドラッグを終える。落とすときは並びを変え、項目を新しい位置へ滑らせて下ろす。</summary>
    private void EndProjectDrag(bool drop)
    {
        var item = _dragItem;
        var at = _dropAt;
        _pressedItem = null;
        _dragItem = null;
        _dropAt = null;
        HideDropLine();
        if (item is null)
        {
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(item);
        double shownTop = TopOf(item) + _dragDelta;

        if (drop && at is { } target && !ReferenceEquals(target.Item, item))
        {
            // 下ろす項目は、滑らせる動きを自分で付けるため、暗黙のアニメーションを外しておく
            visual.ImplicitAnimations = null;
            ViewModel.MoveProject(_navProjects[item].Id, _navProjects[target.Item].Id, target.After);
            NavView.UpdateLayout();
        }

        // いま見えている位置から、並びの中の位置へ滑らせて下ろす
        visual.Properties.InsertVector3("Translation", new(0, (float)(shownTop - TopOf(item)), 0));
        Motion.EaseTo(visual, "Translation", System.Numerics.Vector3.Zero, SlideDuration, SlideEasing(visual.Compositor));
        visual.Scale = new(1, 1, 1);
        visual.Opacity = 1;
        visual.ImplicitAnimations = SlideAndFade(visual.Compositor);
        _dragDelta = 0;
    }

    /// <summary>Esc でドラッグを取りやめる（UX 規約 UX-21）。ドラッグしていなければ false。</summary>
    private bool CancelProjectDrag()
    {
        if (_dragItem is null)
        {
            return false;
        }

        EndProjectDrag(drop: false);
        return true;
    }

    private double TopOf(FrameworkElement item) => item.TransformToVisual(NavView).TransformPoint(new Point(0, 0)).Y;

    /// <summary>同じ見出し（個人、チーム）の項目。</summary>
    private List<NavigationViewItem> GroupOf(NavigationViewItem item) =>
        [.. _projectItems.OfType<NavigationViewItem>().Where(i => _navProjects.TryGetValue(i, out var p)
            && p.IsTeam == _navProjects[item].IsTeam && i.ActualHeight > 0)];

    /// <summary>つかんだ項目が、同じ見出しの最初と最後の項目の範囲から出ないようにする。</summary>
    private double ClampToGroup(NavigationViewItem item, double dy)
    {
        var group = GroupOf(item);
        double top = TopOf(item);
        return Math.Clamp(dy, TopOf(group[0]) - top, TopOf(group[^1]) - top);
    }

    /// <summary>ポインターの高さにある同じ見出しの項目と、その上下どちらに落とすか。</summary>
    private (NavigationViewItem Item, bool After)? DropTarget(NavigationViewItem dragged, double y)
    {
        var group = GroupOf(dragged);
        foreach (var item in group)
        {
            double top = TopOf(item);
            if (y < top + item.ActualHeight || ReferenceEquals(item, group[^1]))
            {
                bool after = y > top + item.ActualHeight / 2;

                // 自分のすぐ上の項目の下端・すぐ下の項目の上端は、動かないのと同じなので自分の上とみなす
                int self = group.IndexOf(dragged), index = group.IndexOf(item);
                if ((index == self - 1 && after) || (index == self + 1 && !after))
                {
                    return (dragged, false);
                }

                return (item, after);
            }
        }

        return null;
    }

    /// <summary>落とす位置（項目の上端か下端）に線を出す。自分自身の上では出さない。</summary>
    private void ShowDropLine(FrameworkElement item, bool after, bool isSelf)
    {
        if (isSelf)
        {
            HideDropLine();
            return;
        }

        var origin = item.TransformToVisual(NavOverlay).TransformPoint(new Windows.Foundation.Point(0, 0));
        Canvas.SetLeft(NavDropLine, origin.X + 12);
        Canvas.SetTop(NavDropLine, origin.Y + (after ? item.ActualHeight : 0) - 1);
        NavDropLine.Width = Math.Max(0, item.ActualWidth - 24);
        ElementCompositionPreview.GetElementVisual(NavDropLine).Opacity = 1;
    }

    private void HideDropLine() => ElementCompositionPreview.GetElementVisual(NavDropLine).Opacity = 0;

    /// <summary>並びを変えたプロジェクトの項目を、作り直さずに新しい位置へ動かす。</summary>
    private void MoveProjectItem(string projectId)
    {
        var tag = $"project:{projectId}";
        if (FindProjectItem(tag) is not { } item)
        {
            return;
        }

        var projects = ViewModel.Projects.ToList();
        int index = projects.FindIndex(p => p.Id == projectId);
        var neighbor = index + 1 < projects.Count && projects[index + 1].IsTeam == projects[index].IsTeam ? projects[index + 1] : null;
        var previous = index > 0 && projects[index - 1].IsTeam == projects[index].IsTeam ? projects[index - 1] : null;

        bool selected = ReferenceEquals(NavView.SelectedItem, item);
        _syncingSelection = true;
        try
        {
            NavView.MenuItems.Remove(item);
            _projectItems.Remove(item);
            if (neighbor is not null && FindProjectItem($"project:{neighbor.Id}") is { } next)
            {
                NavView.MenuItems.Insert(NavView.MenuItems.IndexOf(next), item);
                _projectItems.Insert(_projectItems.IndexOf(next), item);
            }
            else if (previous is not null && FindProjectItem($"project:{previous.Id}") is { } prev)
            {
                NavView.MenuItems.Insert(NavView.MenuItems.IndexOf(prev) + 1, item);
                _projectItems.Insert(_projectItems.IndexOf(prev) + 1, item);
            }

            if (selected)
            {
                NavView.SelectedItem = item;
            }
        }
        finally
        {
            _syncingSelection = false;
        }

        // マイタスクのカンバンのプロジェクトのグループも、同じ順に並べ直す（要件 F-UI-KB-06）
        ViewModel.NotifyDataChanged();
    }

    /// <summary>並びの中を滑る項目の減速。</summary>
    private static CompositionEasingFunction SlideEasing(Compositor compositor) =>
        compositor.CreateCubicBezierEasingFunction(new(0.1f, 0.9f), new(0.2f, 1f));

    /// <summary>
    /// 位置の変化は滑らかに、不透明度の変化は淡く切り替える暗黙のアニメーション。
    /// Windows の設定でアニメーションが無効なら付けない（null）。
    /// </summary>
    private static ImplicitAnimationCollection? SlideAndFade(Compositor compositor)
    {
        if (!Motion.IsEnabled)
        {
            return null;
        }

        var offset = compositor.CreateVector3KeyFrameAnimation();
        offset.Target = "Offset";
        offset.InsertExpressionKeyFrame(1, "this.FinalValue", SlideEasing(compositor));
        offset.Duration = SlideDuration;

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.Target = "Opacity";
        opacity.InsertExpressionKeyFrame(1, "this.FinalValue");
        opacity.Duration = TimeSpan.FromMilliseconds(150);

        var animations = compositor.CreateImplicitAnimationCollection();
        animations["Offset"] = offset;
        animations["Opacity"] = opacity;
        return animations;
    }
}
