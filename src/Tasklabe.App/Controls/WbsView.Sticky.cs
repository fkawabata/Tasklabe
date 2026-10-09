using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Tasklabe.App.Services;
using Tasklabe.App.ViewModels;
using Tasklabe.Core.Wbs;
using Windows.Foundation;

namespace Tasklabe.App.Controls;

/// <summary>
/// 縦にスクロールしたとき、見えている行の親を上端に残す（UI デザイン設計書 3.4.4 節）。
/// 残した行は表の行と同じテンプレートで描き、押すとその行を本来の位置へ戻して選ぶ。
/// </summary>
public sealed partial class WbsView
{
    private ScrollViewer? _scroller;

    /// <summary>表の上端から 1 行目までの余白（スクロールの範囲の座標）。</summary>
    private double _listPadTop;

    private IReadOnlyList<StickyRow> _sticky = [];

    private readonly List<StickyHost> _stickyHosts = [];

    /// <summary>上端に残した 1 行。下の行が透けないよう、表の地と同じ色を不透明に重ねて敷く。</summary>
    private sealed class StickyHost
    {
        public required Grid Root { get; init; }

        public required Border State { get; init; }

        public required ContentPresenter Presenter { get; init; }

        public bool IsPointerOver { get; set; }

        public WbsRowViewModel? Row => Presenter.Content as WbsRowViewModel;
    }

    private double RowHeight => (double)Application.Current.Resources["Size.RowCompact"];

    /// <summary>縦にスクロールしても、親の行を上端に残すか（ツールバーで切り替える）。</summary>
    public bool StickyParents
    {
        get;
        set
        {
            field = value;
            UpdateSticky();
        }
    } = true;

    /// <summary>上端に残す行の上限。表示範囲の半分までとし、残りで配下の行を読めるようにする。</summary>
    private int MaxSticky => StickyParents ? Math.Max((int)(StickyLayer.ActualHeight / RowHeight / 2), 0) : 0;

    private int[] Depths() => [.. Rows.Select(r => r.Node.Depth)];

    private void InitializeSticky()
    {
        List.Loaded += (_, _) =>
        {
            if (_scroller is null && VisualTree.Descendants<ScrollViewer>(List).FirstOrDefault() is { } scroller)
            {
                _scroller = scroller;
                _scroller.ViewChanged += (_, _) => UpdateSticky();
            }

            UpdateSticky();
        };
        Rows.CollectionChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateSticky);
        StickyLayer.SizeChanged += (_, e) =>
        {
            StickyLayer.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
            UpdateSticky();
        };
        ActualThemeChanged += (_, _) =>
        {
            // 下地の色はテーマの色をコードで当てているため、作り直す
            foreach (var host in _stickyHosts)
            {
                StickyLayer.Children.Remove(host.Root);
            }

            _stickyHosts.Clear();
            UpdateSticky();
        };
    }

    /// <summary>行の位置から見た、表示範囲の上端（内容の座標）。</summary>
    private double ScrollTop => (_scroller?.VerticalOffset ?? 0) - _listPadTop;

    /// <summary>実際に並んでいる行の位置から、1 行目までの余白を測る。</summary>
    private void MeasureListPad()
    {
        if (_scroller is null || List.ItemsPanelRoot is not { } panel)
        {
            return;
        }

        foreach (var child in panel.Children)
        {
            if (child is ListViewItem { Content: WbsRowViewModel row } container && Rows.IndexOf(row) is var index and >= 0)
            {
                double y = container.TransformToVisual(StickyLayer).TransformPoint(new Point(0, 0)).Y;
                _listPadTop = y + _scroller.VerticalOffset - index * RowHeight;
                return;
            }
        }
    }

    private void UpdateSticky()
    {
        MeasureListPad();
        _sticky = Rows.Count == 0 ? [] : StickyRows.Compute(Depths(), ScrollTop, RowHeight, MaxSticky);

        while (_stickyHosts.Count < _sticky.Count)
        {
            _stickyHosts.Add(CreateStickyHost());
        }

        var (left, width) = RowBounds();
        for (int k = 0; k < _stickyHosts.Count; k++)
        {
            var host = _stickyHosts[k];
            if (k >= _sticky.Count)
            {
                host.Root.Visibility = Visibility.Collapsed;
                host.Presenter.Content = null;
                continue;
            }

            var row = Rows[_sticky[k].Index];
            host.Root.Visibility = Visibility.Visible;
            host.Root.Width = width;
            host.Root.Height = RowHeight;
            Canvas.SetLeft(host.Root, left);
            Canvas.SetTop(host.Root, _sticky[k].Top);

            // 外側の親を手前に重ね、押し上げられた内側の親が外側の下へ隠れていくようにする
            Canvas.SetZIndex(host.Root, 100 - k);
            if (!ReferenceEquals(host.Presenter.Content, row))
            {
                host.Presenter.Content = row;
            }

            ApplyStickyState(host);
        }
    }

    /// <summary>表の行の左端と幅（スクロールバーの分を除いた、行が並ぶ範囲）。</summary>
    private (double Left, double Width) RowBounds()
    {
        if (List.ItemsPanelRoot is { } panel && panel.ActualWidth > 0)
        {
            double left = panel.TransformToVisual(StickyLayer).TransformPoint(new Point(0, 0)).X;
            return (left, panel.ActualWidth);
        }

        return (0, StickyLayer.ActualWidth);
    }

    private StickyHost CreateStickyHost()
    {
        // 押した位置の判定と開閉の矢印は、残した行の側で受ける（中のセルは押せないようにし、値のピッカーを隠れた行に開かない）
        var presenter = new ContentPresenter
        {
            ContentTemplate = List.ItemTemplate,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false,
        };
        var state = new Border();
        var root = new Grid
        {
            Background = ThemeResources.Brush("SolidBackgroundFillColorBaseBrush"),
            Children =
            {
                new Border { Background = ThemeResources.Brush("LayerFillColorDefaultBrush") },
                state,
                presenter,
            },
        };
        var host = new StickyHost { Root = root, State = state, Presenter = presenter };

        // 列の幅は、表の行と同じく隠した列を詰める
        presenter.Loaded += (_, _) => ApplyWidths(StickyTemplateRoot(host));
        presenter.RegisterPropertyChangedCallback(ContentPresenter.ContentProperty, (_, _) =>
            DispatcherQueue.TryEnqueue(() => ApplyWidths(StickyTemplateRoot(host))));

        root.PointerEntered += (_, _) =>
        {
            host.IsPointerOver = true;
            ApplyStickyState(host);
        };
        root.PointerExited += (_, _) =>
        {
            host.IsPointerOver = false;
            ApplyStickyState(host);
        };
        root.PointerPressed += (_, e) => OnStickyPressed(host, e);
        root.DoubleTapped += (_, _) =>
        {
            if (StickyDoubleTapped() is { } row)
            {
                App.Current.Shell?.SelectTask(row.Task);
            }
        };
        root.RightTapped += (_, e) => OnStickyRightTapped(host, e);

        StickyLayer.Children.Add(root);
        return host;
    }

    private static Grid? StickyTemplateRoot(StickyHost host) =>
        VisualTreeHelper.GetChildrenCount(host.Presenter) > 0 ? VisualTreeHelper.GetChild(host.Presenter, 0) as Grid : null;

    /// <summary>選んでいる行・ポインターを載せた行の面（表の行と同じ色）。</summary>
    private void ApplyStickyState(StickyHost host)
    {
        bool selected = host.Row is { } row && List.SelectedItems.Contains(row);
        host.State.Background = selected ? ThemeResources.Brush("ListViewItemBackgroundSelected")
            : host.IsPointerOver ? ThemeResources.Brush("ListViewItemBackgroundPointerOver")
            : null;
    }

    private void RefreshStickyStates()
    {
        foreach (var host in _stickyHosts)
        {
            ApplyStickyState(host);
        }
    }

    private void OnStickyPressed(StickyHost host, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(host.Root);
        if (host.Row is not { } row || !point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        if (row.HasChildren && IsOnStickyChevron(host, point.Position))
        {
            ToggleExpanded(row, !row.IsExpanded);
            return;
        }

        _stickyPress = (row.ItemId, Environment.TickCount64);
        Select(row, focus: true);
    }

    /// <summary>上端に残している行を押した（押すと本来の位置へ戻すため、続けて押した 2 回目は別の行に当たる）。</summary>
    private (string ItemId, long At)? _stickyPress;

    /// <summary>ダブルクリックの 1 回目で上端に残している行を押していれば、その行（2 回目の位置の行ではなく）。</summary>
    private WbsRowViewModel? StickyDoubleTapped()
    {
        if (_stickyPress is not { } press || Environment.TickCount64 - press.At > new Windows.UI.ViewManagement.UISettings().DoubleClickTime)
        {
            return null;
        }

        _stickyPress = null;
        return Rows.FirstOrDefault(r => r.ItemId == press.ItemId);
    }

    private bool IsOnStickyChevron(StickyHost host, Point position)
    {
        if (StickyTemplateRoot(host) is not { } root
            || VisualTree.Descendants<Button>(root).FirstOrDefault(b => b.Visibility == Visibility.Visible) is not { } chevron)
        {
            return false;
        }

        var bounds = chevron.TransformToVisual(host.Root).TransformBounds(new Rect(0, 0, chevron.ActualWidth, chevron.ActualHeight));
        return position.X >= bounds.Left && position.X <= bounds.Right;
    }

    /// <summary>残した行の右クリック: その場で行を選び、表の行と同じメニューを開く（スクロールさせるとメニューの位置がずれる）。</summary>
    private async void OnStickyRightTapped(StickyHost host, RightTappedRoutedEventArgs e)
    {
        if (host.Row is not { } row || _project is null)
        {
            return;
        }

        e.Handled = true;
        var selected = List.SelectedItems.OfType<WbsRowViewModel>().ToList();
        if (!selected.Contains(row))
        {
            List.SelectedItem = row;
            _anchor = row;
            selected = [row];
        }

        var position = e.GetPosition(host.Root);
        var target = new TaskTarget(row.Task, host.Root, position, this, [.. selected.Select(r => r.Task)]);
        var extras = TaskMenu.DependencyEntries(target, row.HasChildren, _tree?.All().Select(n => n.Task) ?? []);
        await TaskMenu.ShowAsync(target, host.Root, position, extras, isParent: row.HasChildren && selected.Count == 1);
    }

    /// <summary>
    /// 行を見える位置へスクロールする。上端に残した親に隠れるなら、親の分を空けて見せる。
    /// 残している親そのものを選んだときは、本来の位置へ戻す。
    /// </summary>
    private void ScrollRowIntoView(WbsRowViewModel row)
    {
        int index = Rows.IndexOf(row);
        if (_scroller is null || index < 0)
        {
            List.ScrollIntoView(row);
            return;
        }

        if (index * RowHeight < ScrollTop + StickyRows.Covered(_sticky, RowHeight))
        {
            _scroller.ChangeView(null, StickyRows.RevealTop(Depths(), index, RowHeight, MaxSticky) + _listPadTop, null, disableAnimation: true);
            return;
        }

        List.ScrollIntoView(row);
    }

    /// <summary>キーの操作で選んだ行が、上端に残した親に隠れていれば見せる（ListView は表示範囲の上端にそろえて止める）。</summary>
    private void RevealIfCovered(WbsRowViewModel row)
    {
        int index = Rows.IndexOf(row);
        if (index >= 0 && StickyRows.TopOf(_sticky, index) is null
            && index * RowHeight < ScrollTop + StickyRows.Covered(_sticky, RowHeight))
        {
            ScrollRowIntoView(row);
        }
    }

    /// <summary>上端に残している行を開閉した後、その行が同じ位置に見えるようにする（配下が消えて別の行へ跳ばないように）。</summary>
    private void KeepStickyPosition(WbsRowViewModel row, double stickyTop)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            int index = Rows.ToList().FindIndex(r => r.ItemId == row.ItemId);
            if (_scroller is not null && index >= 0)
            {
                List.UpdateLayout();
                _scroller.ChangeView(null, index * RowHeight - Math.Max(stickyTop, 0) + _listPadTop, null, disableAnimation: true);
            }
        });
    }
}
