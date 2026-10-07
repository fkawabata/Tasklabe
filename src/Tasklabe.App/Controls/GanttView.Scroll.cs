using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Tasklabe.Animation;
using Tasklabe.App.Services;
using Windows.System;

namespace Tasklabe.App.Controls;

/// <summary>
/// ガントチャートのスクロールとズーム（要件 F-UI-GT-07）。ホイール、スクロールバー、表示範囲の広げ方、今日の位置への移動。
/// </summary>
public sealed partial class GanttView
{
    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateScrollBars();
        LayoutRows(rebind: false);
        RequestRender();
    }

    private void UpdateScrollBars()
    {
        _scrollY = Math.Clamp(_scrollY, 0, Math.Max(ContentHeight - BodyHeight, 0));
        _scrollX = Math.Clamp(_scrollX, 0, Math.Max(ContentWidth - Chart.ActualWidth, 0));

        _syncingScrollBars = true;
        VBar.ViewportSize = Math.Max(BodyHeight, 1);
        VBar.Maximum = Math.Max(ContentHeight - BodyHeight, 0);
        VBar.LargeChange = Math.Max(BodyHeight - RowHeight, RowHeight);
        VBar.SmallChange = RowHeight;
        VBar.Value = _scrollY;
        VBar.Visibility = VBar.Maximum > 0 ? Visibility.Visible : Visibility.Collapsed;

        HBar.ViewportSize = Math.Max(Chart.ActualWidth, 1);
        HBar.Maximum = Math.Max(ContentWidth - Chart.ActualWidth, 0);
        HBar.LargeChange = Math.Max(Chart.ActualWidth * 0.8, 1);
        HBar.SmallChange = _dayWidth * 7;
        HBar.Value = _scrollX;
        _syncingScrollBars = false;
    }

    private void OnVerticalScroll(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_syncingScrollBars)
        {
            ScrollTo(_scrollX, e.NewValue);
        }
    }

    private void OnHorizontalScroll(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_syncingScrollBars)
        {
            ScrollTo(e.NewValue, _scrollY);
        }
    }

    private void ScrollTo(double x, double y)
    {
        _scrollX = Math.Clamp(x, 0, Math.Max(ContentWidth - Chart.ActualWidth, 0));
        _scrollY = Math.Clamp(y, 0, Math.Max(ContentHeight - BodyHeight, 0));
        _syncingScrollBars = true;
        VBar.Value = _scrollY;
        HBar.Value = _scrollX;
        _syncingScrollBars = false;
        LayoutRows(rebind: false);
        HideHover();
        UpdateTodayJump();
        RequestRender();
    }

    /// <summary>
    /// 表示する日付の範囲の端まで来たら、範囲を広げる（画面の移動と、ドラッグ中の自動スクロールで、どこまでも動かせるようにする）。
    /// 左へ広げるときは、見えている位置が変わらないよう、スクロール位置とドラッグの基準も同じだけずらす。
    /// </summary>
    private void ExtendRangeIfNeeded(double wantedX)
    {
        const int Step = 28;
        if (wantedX < 0)
        {
            int days = Step * (int)Math.Ceiling(-wantedX / (_dayWidth * Step));
            _origin = _origin.AddDays(-days);
            _dayCount += days;
            double shift = days * _dayWidth;
            _scrollX += shift;
            if (_drag is { } drag)
            {
                drag.StartScrollX += shift;
            }

            UpdateScrollBars();
        }
        else if (wantedX > ContentWidth - Chart.ActualWidth)
        {
            _dayCount += Step * (int)Math.Ceiling((wantedX - (ContentWidth - Chart.ActualWidth)) / (_dayWidth * Step));
            UpdateScrollBars();
        }
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ChartInput);
        int delta = point.Properties.MouseWheelDelta;
        var modifiers = e.KeyModifiers;

        if (modifiers.HasFlag(VirtualKeyModifiers.Control))
        {
            // Ctrl + ホイール: ポインターの位置の日付を保ったまま横方向に拡大・縮小する
            Zoom(_dayWidth * Math.Pow(1.15, delta / 120.0), Math.Clamp(point.Position.X, 0, Chart.ActualWidth));
        }
        else if (point.Properties.IsHorizontalMouseWheel)
        {
            ScrollTo(_scrollX + delta, _scrollY);
        }
        else if (modifiers.HasFlag(VirtualKeyModifiers.Shift))
        {
            // Shift + ホイールは、ポインターの位置によらず横に動かす（ほかの画面と同じ）
            ScrollTo(_scrollX - delta, _scrollY);
        }
        else
        {
            // ホイールは、ポインターの位置によらず縦に動かす
            ScrollTo(_scrollX, _scrollY - delta * RowHeight * 3 / 120.0);
        }

        e.Handled = true;
    }

    /// <summary>拡大・縮小で、表示の単位（日・週・月）が変わった。</summary>
    public event EventHandler? TimeScaleChanged;

    private void Zoom(double dayWidth, double anchorX)
    {
        var scale = TimeScale;
        dayWidth = Math.Clamp(dayWidth, MinDayWidth, MaxDayWidth);
        double anchorDay = (_scrollX + anchorX) / _dayWidth;
        _dayWidth = dayWidth;
        _scrollX = anchorDay * _dayWidth - anchorX;
        UpdateScrollBars();
        ScrollTo(_scrollX, _scrollY);
        if (TimeScale != scale)
        {
            TimeScaleChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>今日の位置へスクロールする（T キー、「今日」「今日へ」のボタン）。離れているときは短く動かして、どこへ戻ったかを見せる。</summary>
    public void ScrollToToday()
    {
        double target = (Today.DayNumber - _origin.DayNumber) * _dayWidth - Chart.ActualWidth / 3;
        _scrollAnimation?.Stop();

        // 250 ms で減速しながら動かす（Fluent 2 のモーション）。すでに今日の位置なら動かさない
        double from = _scrollX;
        var duration = Math.Abs(target - from) < 1 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(250);
        _scrollAnimation = DispatcherQueue.Tween(duration, p => ScrollTo(from + (target - from) * p, _scrollY));
    }

    private void OnTodayJump(object sender, RoutedEventArgs e)
    {
        ScrollToToday();
        Focus(FocusState.Programmatic);
    }

    /// <summary>今日が表示範囲の外にあるときだけ、今日のある側に「今日へ」を出す。</summary>
    private void UpdateTodayJump()
    {
        if (Chart.ActualWidth <= 0)
        {
            return;
        }

        double todayX = (Today.DayNumber - _origin.DayNumber) * _dayWidth - _scrollX;
        bool left = todayX + _dayWidth < 0;
        bool right = todayX > Chart.ActualWidth;
        TodayJump.Visibility = left || right ? Visibility.Visible : Visibility.Collapsed;
        TodayJump.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        TodayJumpLeft.Visibility = left ? Visibility.Visible : Visibility.Collapsed;
        TodayJumpRight.Visibility = right ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(TodayJump, KeyHints.Tip("gantt.today", $"今日（{DateText.Short(Today)}）の位置へ戻る"));
    }

    // ================================================================ 閲覧モードと編集モード（UI デザイン設計書 3.3.4 節）

    private void ScrollRowIntoView(int index)
    {
        double top = index * RowHeight;
        if (top < _scrollY)
        {
            ScrollTo(_scrollX, top);
        }
        else if (top + RowHeight > _scrollY + BodyHeight)
        {
            ScrollTo(_scrollX, top + RowHeight - BodyHeight);
        }
    }
}
