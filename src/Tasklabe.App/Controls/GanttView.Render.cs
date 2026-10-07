using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Tasklabe.Core.Calendar;
using Tasklabe.Core.Gantt;
using Windows.Foundation;
using Windows.UI;

namespace Tasklabe.App.Controls;

/// <summary>
/// ガントチャートの描画（技術設計書 5 章）。Win2D でタイムラインの背景・バー・依存関係・イナズマ線・見出しを描く。
/// </summary>
public sealed partial class GanttView
{
    private sealed class ChartColors
    {
        public Color Progress, ProgressRemain, Delay, Today, Holiday, Grid, Plan, Done, Milestone, Accent, Text, TextSecondary, TextTertiary, Divider, Selection, Hover, OnAccent, SurfaceBase, SurfaceLayer;

        /// <summary>チャートの下地（不透明）。スワップチェーンは背景を自前で塗る。</summary>
        public Color Surface => Over(SurfaceLayer, SurfaceBase);

        private static Color Over(Color top, Color under)
        {
            float a = top.A / 255f;
            return Color.FromArgb(255,
                (byte)(top.R * a + under.R * (1 - a)),
                (byte)(top.G * a + under.G * (1 - a)),
                (byte)(top.B * a + under.B * (1 - a)));
        }
    }

    private ChartColors ReadPalette()
    {
        var colors = new ChartColors();
        foreach (var child in Palette.Children.OfType<Border>())
        {
            var brush = child.Background as SolidColorBrush;
            var c = brush?.Color ?? Microsoft.UI.Colors.Transparent;
            c.A = (byte)(c.A * (brush?.Opacity ?? 1));
            switch (child.Tag as string)
            {
                case "Progress": colors.Progress = c; break;
                case "ProgressRemain": colors.ProgressRemain = c; break;
                case "Delay": colors.Delay = c; break;
                case "Today": colors.Today = c; break;
                case "Holiday": colors.Holiday = c; break;
                case "Grid": colors.Grid = c; break;
                case "Plan": colors.Plan = c; break;
                case "Done": colors.Done = c; break;
                case "Milestone": colors.Milestone = c; break;
                case "Accent": colors.Accent = c; break;
                case "Text": colors.Text = c; break;
                case "TextSecondary": colors.TextSecondary = c; break;
                case "TextTertiary": colors.TextTertiary = c; break;
                case "Divider": colors.Divider = c; break;
                case "Selection": colors.Selection = c; break;
                case "Hover": colors.Hover = c; break;
                case "OnAccent": colors.OnAccent = c; break;
                case "SurfaceBase": colors.SurfaceBase = c; break;
                case "SurfaceLayer": colors.SurfaceLayer = c; break;
            }
        }

        return colors;
    }

    // ================================================================ 描画のスケジュール
    //
    // スワップチェーンへ直接描き、変更のあったフレームだけ描き直す（技術設計書 5 章、要件 NF-03）。

    private CanvasSwapChain? _swapChain;

    /// <summary>大きさや DPI の変化を購読している XamlRoot。</summary>
    private XamlRoot? _xamlRoot;
    private bool _renderRequested;
    private bool _renderingHooked;

    private void RequestRender()
    {
        _renderRequested = true;
        if (!_renderingHooked && IsLoaded)
        {
            _renderingHooked = true;
            CompositionTarget.Rendering += OnRenderingFrame;
        }
    }

    private void StopRendering()
    {
        if (_renderingHooked)
        {
            _renderingHooked = false;
            CompositionTarget.Rendering -= OnRenderingFrame;
        }
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => RequestRender();

    private void OnRenderingFrame(object? sender, object e)
    {
        if (!_renderRequested)
        {
            // 変更がなければ購読をやめ、アイドル時にフレームを回さない
            StopRendering();
            return;
        }

        _renderRequested = false;
        Render();
    }

    private void Render()
    {
        float width = (float)Chart.ActualWidth;
        float height = (float)Chart.ActualHeight;
        if (width < 1 || height < 1 || XamlRoot is null)
        {
            // まだ表示されていない（幅が決まっていない）。要求は残し、大きさが決まってから描く
            _renderRequested = true;
            StopRendering();
            return;
        }

        var device = CanvasDevice.GetSharedDevice();
        float dpi = (float)(XamlRoot.RasterizationScale * 96);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (_swapChain is null || _swapChain.Device != device)
            {
                _swapChain?.Dispose();
                _swapChain = new CanvasSwapChain(device, width, height, dpi,
                    Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, CanvasAlphaMode.Premultiplied);
                Chart.SwapChain = _swapChain;
            }
            else if (_swapChain.Size.Width != width || _swapChain.Size.Height != height || _swapChain.Dpi != dpi)
            {
                _swapChain.ResizeBuffers(width, height, dpi);
            }

            using (var ds = _swapChain.CreateDrawingSession(ReadPalette().Surface))
            {
                Draw(ds, device, width, height);
            }

            _swapChain.Present();
        }
        catch (Exception ex) when (device.IsDeviceLost(ex.HResult))
        {
            // GPU のリセットなどで描画先を失った場合は、作り直して描き直す
            device.RaiseDeviceLost();
            _swapChain = null;
            RequestRender();
        }
        finally
        {
            RecordFrame(started);
        }
    }

    private void Draw(CanvasDrawingSession ds, CanvasDevice device, float width, float height)
    {
        if (!ReferenceEquals(_textDevice, device))
        {
            ClearTextCache();
            _textDevice = device;
        }

        var c = ReadPalette();
        var today = Today;

        int firstDay = (int)Math.Floor(_scrollX / _dayWidth);
        int lastDay = (int)Math.Ceiling((_scrollX + width) / _dayWidth);
        var firstDate = _origin.AddDays(Math.Max(firstDay, 0));
        var lastDate = _origin.AddDays(Math.Min(lastDay, _dayCount));
        int firstRow = Math.Max((int)Math.Floor(_scrollY / RowHeight), 0);
        int lastRow = Math.Min((int)Math.Ceiling((_scrollY + BodyHeight) / RowHeight), _rows.Count - 1);
        int selected = SelectedIndex;
        _frameSelected = selected;

        // 本体（見出しの下）だけに描く
        using (ds.CreateLayer(1, new Rect(0, HeaderHeight, width, Math.Max(height - HeaderHeight, 0))))
        {
            DrawBackground(ds, c, firstDate, lastDate, height, selected, firstRow, lastRow);
            for (int i = firstRow; i <= lastRow; i++)
            {
                DrawRow(ds, c, _rows[i]);
            }

            DrawDependencies(ds, c, firstRow, lastRow, selected);
            if (ShowInazuma)
            {
                DrawInazuma(ds, c, firstRow, lastRow, today);
            }

            float todayX = X(today);
            ds.DrawLine(todayX, (float)HeaderHeight, todayX, height, c.Today, 2);

            // マイルストーンは期限の区切り。警告の色は使わず、どの行にもかかる点線で示す
            foreach (var s in _milestones)
            {
                float mx = MilestoneX(s.Milestone.Due!.Value);
                ds.DrawLine(mx, (float)HeaderHeight, mx, height, WithAlpha(c.Text, 0.55), 1.5f, _milestoneDash);
            }

            DrawDragPreview(ds, c);
        }

        DrawHeader(ds, c, firstDate, lastDate, width, today);
    }

    private void DrawBackground(CanvasDrawingSession ds, ChartColors c, DateOnly first, DateOnly last, float height, int selected, int firstRow, int lastRow)
    {
        float top = (float)HeaderHeight;
        float dayWidth = (float)_dayWidth;

        // マイルストーンの行は淡く塗り、計画の中の区切りとして見せる
        for (int i = firstRow; i <= lastRow; i++)
        {
            if (_rows[i].IsMilestone)
            {
                ds.FillRectangle(0, RowTop(i), (float)Chart.ActualWidth, (float)RowHeight, WithAlpha(c.Milestone, 0.07));
            }
        }

        if (selected >= firstRow && selected <= lastRow)
        {
            ds.FillRectangle(0, RowTop(selected), (float)Chart.ActualWidth, (float)RowHeight, c.Selection);
        }
        else if (_hoverRow >= firstRow && _hoverRow <= lastRow && _drag is null)
        {
            ds.FillRectangle(0, RowTop(_hoverRow), (float)Chart.ActualWidth, (float)RowHeight, c.Hover);
        }

        for (var d = first; d <= last; d = d.AddDays(1))
        {
            float x = X(d);
            // 月表示では休日の網掛けが細かすぎて地模様になるため、週表示までにとどめる
            if (WorkCalendar.IsHoliday(d) && dayWidth >= 6)
            {
                ds.FillRectangle(x, top, dayWidth, height - top, c.Holiday);
            }

            // 月の境界は常に、週の境界と日の境界は幅に余裕があるときだけ引く
            if (d.Day == 1)
            {
                ds.DrawLine(x, top, x, height, c.Grid, 1);
            }
            else if (d.DayOfWeek == DayOfWeek.Monday && dayWidth >= 6)
            {
                ds.DrawLine(x, top, x, height, WithAlpha(c.Grid, 0.7), 1);
            }
            else if (dayWidth >= 18)
            {
                ds.DrawLine(x, top, x, height, WithAlpha(c.Grid, 0.35), 1);
            }
        }
    }

    private void DrawRow(CanvasDrawingSession ds, ChartColors c, GanttRow row)
    {
        float cy = RowCenter(row.Index);
        if (row.IsMilestone)
        {
            DrawMilestoneRow(ds, c, row, cy);
            return;
        }

        var plan = PlanOf(row);

        if (row.Kind == GanttRowKind.Parent)
        {
            DrawParentBar(ds, c, row, cy);
        }
        else
        {
            DrawTaskBar(ds, c, row, plan, cy);
        }

        // 調整のプレビュー: 元の位置を淡く残し、動かした先を強調する
        if (IsPreviewing && _originalPlans.TryGetValue(row.Task.ItemId, out var original) && plan is { } moved && moved != original)
        {
            float top = cy - BarHeight / 2 - 2.5f;
            float height = BarHeight + 5;
            ds.DrawRoundedRectangle(X(original.Start) - 0.5f, top, X(original.End.DayNumber + 1) - X(original.Start) + 1, height, 5, 5,
                WithAlpha(c.TextTertiary, 0.8), 1, _dashStyle);
            ds.DrawRoundedRectangle(X(moved.Start) - 0.5f, top, X(moved.End.DayNumber + 1) - X(moved.Start) + 1, height, 5, 5, c.Accent, 2);
        }

        // 編集モードでは、選択中・ホバー中のタスクに、依存関係を追加するための点を表示する（親タスクも起点にできる）
        if (_editing && !IsPreviewing && (row.Index == _frameSelected || row.Index == _hoverRow) && BarExtent(row) is { } extent)
        {
            ds.FillCircle(extent.Right + LinkHandleOffset, cy, 3.5f, c.Accent);
        }
    }

    // 描画は毎フレーム行うため、レイヤー（クリップ）や図形の生成を避け、線と矩形の描画で表す。
    // 図形を作るのは、イナズマ線と、予定から延ばした部分を持つバー（片側だけ角を丸める）に限る

    private readonly CanvasStrokeStyle _dashStyle = new() { CustomDashStyle = [3, 2] };
    private readonly CanvasStrokeStyle _roundJoin = new() { LineJoin = CanvasLineJoin.Round };

    /// <summary>
    /// タスクのバー（UI デザイン設計書 3.3.2 節）。予定の期間を状態の淡い色で塗り、進捗の位置まで濃い色で塗る。
    /// 予定を超えた実績（見込み）は、遅れの淡い色で右へ延ばす。予定開始日より遅れて始めた分（未着手のまま過ぎた分を含む）は、
    /// 同じ表現で予定のバーの左側に示し、進捗は始めた位置から塗る。早く始めた分は、バーを左へ延ばす。
    /// 予定がなければ、実績の期間を同じ形で描く。
    /// </summary>
    private void DrawTaskBar(CanvasDrawingSession ds, ChartColors c, GanttRow row, (DateOnly Start, DateOnly End)? plan, float cy)
    {
        const float radius = 4;
        float top = cy - BarHeight / 2;
        var (light, dark) = BarColors(c, row, plan);

        // 延ばした分とつながる側は、角を丸めず継ぎ目の辺も描かない（半透明の色が重なって継ぎ目が透けないようにする）
        bool roundLeft = true, roundRight = true;
        float left, right;
        if (plan is { } p)
        {
            left = X(p.Start);
            right = Math.Max(X(p.End.DayNumber + 1), left + 2);

            // 予定を超えた実績は、予定のバーの右へつなげて延ばす
            if (!row.IsDone && row.ActualEnd is { } actualEnd && X(actualEnd.DayNumber + 1) is var over && over > right + 0.5f)
            {
                FillSegment(ds, right, over, top, BarHeight, radius, false, true, WithAlpha(c.Delay, 0.16));
                StrokeOpenSegment(ds, right, over - 0.5f, top + 0.5f, BarHeight - 1, radius, openLeft: true, WithAlpha(c.Delay, 0.7), null);

                // 色だけに頼らず、遅れの部分は斜線でも示す（UI デザイン設計書 4.2 節）
                DrawHatch(ds, right, over - 1, top + 1, top + BarHeight - 1, WithAlpha(c.Delay, 0.6));
                roundRight = false;
            }

            if (WorkStart(row, p) is { } work)
            {
                float begin = Math.Min(X(work), right);
                if (begin < left)
                {
                    left = begin;
                }
                else if (begin > left + 0.5f)
                {
                    // 始めるのが遅れた分は、予定を超えた分（濃い斜線）と見分けられるよう、薄い破線の斜線と枠にして、バーの左へつなげる
                    bool whole = begin >= right - 0.5f;
                    FillSegment(ds, left, begin, top, BarHeight, radius, true, whole && roundRight, WithAlpha(c.Delay, 0.06));
                    StrokeOpenSegment(ds, left + 0.5f, begin, top + 0.5f, BarHeight - 1, radius, openLeft: false, WithAlpha(c.Delay, 0.4), _dashStyle);
                    DrawHatch(ds, left + 1, begin, top + 1, top + BarHeight - 1, WithAlpha(c.Delay, 0.4), 1, _dashStyle);
                    if (whole)
                    {
                        return;
                    }

                    left = begin;
                    roundLeft = false;
                }
            }
        }
        else if (row.ActualStart is { } a && row.ActualEnd is { } b)
        {
            left = X(a);
            right = Math.Max(X(b.DayNumber + 1), left + 2);
        }
        else
        {
            return;
        }

        FillSegment(ds, left, right, top, BarHeight, radius, roundLeft, roundRight, light);
        float split = left + (right - left) * (float)(Math.Clamp(row.ProgressPercent, 0, 100) / 100);
        if (!row.IsDone && split > left + 0.5f)
        {
            // 左端はバーに合わせ、進捗の位置は直線で区切る（濃い色は不透明なので、重ねて塗っても色は変わらない）
            if (roundLeft)
            {
                ds.FillRoundedRectangle(left, top, split - left, BarHeight, radius, radius, dark);
            }

            float square = roundLeft ? Math.Max(left + radius, split - radius) : left;
            if (split < right - radius || !roundRight)
            {
                ds.FillRectangle(square, top, split - square, BarHeight, dark);
            }
        }
    }

    /// <summary>左右の端ごとに角を丸めるかを選んで塗る。片側だけ丸めるときは、重ね塗りで色が濃くならないよう 1 つの図形で塗る。</summary>
    private static void FillSegment(CanvasDrawingSession ds, float x0, float x1, float top, float height, float radius,
        bool roundLeft, bool roundRight, Color color)
    {
        float r = Math.Min(radius, (x1 - x0) / 2);
        if (r <= 0.5f || (!roundLeft && !roundRight))
        {
            ds.FillRectangle(x0, top, x1 - x0, height, color);
        }
        else if (roundLeft && roundRight)
        {
            ds.FillRoundedRectangle(x0, top, x1 - x0, height, r, r, color);
        }
        else
        {
            using var builder = new CanvasPathBuilder(ds);
            float rl = roundLeft ? r : 0, rr = roundRight ? r : 0;
            builder.BeginFigure(x0 + rl, top);
            builder.AddLine(x1 - rr, top);
            AddCorner(builder, rr, x1, top + rr);
            builder.AddLine(x1, top + height - rr);
            AddCorner(builder, rr, x1 - rr, top + height);
            builder.AddLine(x0 + rl, top + height);
            AddCorner(builder, rl, x0, top + height - rl);
            builder.AddLine(x0, top + rl);
            AddCorner(builder, rl, x0 + rl, top);
            builder.EndFigure(CanvasFigureLoop.Closed);
            using var geometry = CanvasGeometry.CreatePath(builder);
            ds.FillGeometry(geometry, color);
        }
    }

    /// <summary>上下の辺と、角を丸めた片側の端の枠を描く。もう片側（バーとつながる側）は開けておく。</summary>
    private static void StrokeOpenSegment(CanvasDrawingSession ds, float x0, float x1, float top, float height, float radius,
        bool openLeft, Color color, CanvasStrokeStyle? style)
    {
        float r = Math.Min(radius, Math.Max(x1 - x0, 0));
        using var builder = new CanvasPathBuilder(ds);
        if (openLeft)
        {
            builder.BeginFigure(x0, top);
            builder.AddLine(x1 - r, top);
            AddCorner(builder, r, x1, top + r);
            builder.AddLine(x1, top + height - r);
            AddCorner(builder, r, x1 - r, top + height);
            builder.AddLine(x0, top + height);
        }
        else
        {
            builder.BeginFigure(x1, top + height);
            builder.AddLine(x0 + r, top + height);
            AddCorner(builder, r, x0, top + height - r);
            builder.AddLine(x0, top + r);
            AddCorner(builder, r, x0 + r, top);
            builder.AddLine(x1, top);
        }

        builder.EndFigure(CanvasFigureLoop.Open);
        using var geometry = CanvasGeometry.CreatePath(builder);
        ds.DrawGeometry(geometry, color, 1, style);
    }

    /// <summary>時計回りに 90° の角を足す。半径が 0 なら直角のまま。</summary>
    private static void AddCorner(CanvasPathBuilder builder, float radius, float x, float y)
    {
        if (radius > 0)
        {
            builder.AddArc(new Vector2(x, y), radius, radius, 0, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
        }
    }

    /// <summary>
    /// 予定と違う日に始めた（始めている）タスクの、作業の始まり。早く・遅れて始めたなら実績開始日、未着手のまま予定開始日を
    /// 過ぎたなら今日（まだ始めていない分も遅れとして示す）。完了したタスクは目立たせないため示さない。予定どおりなら null。
    /// </summary>
    private static DateOnly? WorkStart(GanttRow row, (DateOnly Start, DateOnly End) plan)
    {
        if (row.IsDone || row.IsMilestone)
        {
            return null;
        }

        if (row.ActualStart is { } actual)
        {
            return actual != plan.Start ? actual : null;
        }

        return row.ProgressPercent <= 0 && plan.Start < Today ? Today : null;
    }

    /// <summary>バーの淡い色と濃い色。完了は目立たせず、遅れは赤、取りかかったものは進捗の色、未着手は予定の色とする。</summary>
    private static (Color Light, Color Dark) BarColors(ChartColors c, GanttRow row, (DateOnly Start, DateOnly End)? plan)
    {
        if (row.IsDone)
        {
            return (c.Done, c.Done);
        }

        bool late = row.DelayDays > 0 || (plan is { } p && row.ActualEnd is { } end && end > p.End);
        if (late)
        {
            return (WithAlpha(c.Delay, 0.22), c.Delay);
        }

        bool started = row.ActualStart is not null || row.ProgressPercent > 0;
        return started ? (c.ProgressRemain, c.Progress) : (WithAlpha(c.Plan, 0.16), c.Plan);
    }

    /// <summary>
    /// マイルストーンの行（UI デザイン設計書 3.3.7 節）。入っているタスクの最初の開始から期日の終わりまでを細いバーにし、
    /// 進捗率の位置まで塗る。今日までに進んでいるはずの位置に縦の印を付け、右端に ◆ と進捗率を置く。期日を過ぎても色は変えない。
    /// </summary>
    private void DrawMilestoneRow(CanvasDrawingSession ds, ChartColors c, GanttRow row, float cy)
    {
        if (row.PlanStart is not { } start || row.PlanEnd is not { } due)
        {
            return;
        }

        const float height = 8;
        float left = X(start);
        float right = Math.Max(MilestoneX(due), left + height);
        float top = cy - height / 2;
        ds.FillRoundedRectangle(left, top, right - left, height, height / 2, height / 2, WithAlpha(c.Milestone, 0.22));
        float filled = (right - left) * (float)(Math.Clamp(row.ProgressPercent, 0, 100) / 100);
        if (filled > 0.5f)
        {
            ds.FillRoundedRectangle(left, top, Math.Max(filled, height), height, height / 2, height / 2, c.Milestone);
        }

        // 期日超え: 入っているタスクの最も遅い予定終了日まで、期日の先へタスクの遅れと同じ淡い色と斜線で延ばす
        float lateRight = right;
        if (row.Milestone?.LateEnd is { } lateEnd && X(lateEnd.DayNumber + 1) is var over && over > right + 0.5f)
        {
            ds.FillRoundedRectangle(right - height / 2, top, over - right + height / 2, height, height / 2, height / 2, WithAlpha(c.Delay, 0.16));
            ds.DrawRoundedRectangle(right - height / 2 + 0.5f, top + 0.5f, over - right + height / 2 - 1, height - 1, height / 2, height / 2, WithAlpha(c.Delay, 0.7), 1);
            DrawHatch(ds, right, over - 1, top + 1, top + height - 1, WithAlpha(c.Delay, 0.6));
            lateRight = over;
        }

        if (row.ExpectedPercent is { } expected)
        {
            float x = left + (right - left) * (float)(Math.Clamp(expected, 0, 100) / 100);
            ds.DrawLine(x, top - 4, x, top + height + 4, c.Text, 2);
        }

        // 右端の ◆（正方形を 45° 回して描く）
        const float half = 5;
        var saved = ds.Transform;
        ds.Transform = Matrix3x2.CreateRotation(MathF.PI / 4, new Vector2(right, cy)) * saved;
        ds.FillRectangle(right - half, cy - half, half * 2, half * 2, c.Milestone);
        ds.Transform = saved;

        var label = TextLayout(ds, $"{row.ProgressPercent:0}%", _labelFormat);
        ds.DrawTextLayout(label, lateRight + 10, cy - (float)label.LayoutBounds.Height / 2, c.Milestone);
    }

    /// <summary>矩形の範囲に、左下から右上への斜線を引く（線分を矩形で切り取って描く）。</summary>
    private static void DrawHatch(CanvasDrawingSession ds, float left, float right, float top, float bottom, Color color,
        float width = 1.5f, CanvasStrokeStyle? style = null)
    {
        float height = bottom - top;
        for (float x = left - height; x < right; x += 5)
        {
            // 線分 (x, bottom) → (x + height, top) を左右の端で切り取る
            float x0 = x, y0 = bottom, x1 = x + height, y1 = top;
            if (x0 < left)
            {
                y0 -= left - x0;
                x0 = left;
            }

            if (x1 > right)
            {
                y1 += x1 - right;
                x1 = right;
            }

            if (x1 > x0)
            {
                ds.DrawLine(x0, y0, x1, y1, color, width, style);
            }
        }
    }

    /// <summary>
    /// 親タスク（UI デザイン設計書 3.3.2 節）。展開しているときは子のバーが主役のため、範囲だけを淡い線で示す。
    /// 折りたたんでいるときは子が見えないため、子をまとめた 1 本のバーをタスクと同じ見方（状態の色、進捗、遅れ）で描く。
    /// </summary>
    private void DrawParentBar(CanvasDrawingSession ds, ChartColors c, GanttRow row, float cy)
    {
        if (_collapsed.Contains(row.Key))
        {
            DrawTaskBar(ds, c, row, PlanOf(row), cy);
            return;
        }

        if (BarExtent(row) is not { } extent)
        {
            return;
        }

        float left = extent.Left;
        float right = Math.Max(extent.Right, left + 2);
        var color = WithAlpha(c.Plan, 0.45);
        ds.FillRectangle(left, cy - 1, right - left, 2, color);

        // 両端は短い縦の線にして、範囲の始まりと終わりを示す
        ds.FillRectangle(left, cy - 4, 1.5f, 8, color);
        ds.FillRectangle(right - 1.5f, cy - 4, 1.5f, 8, color);

        // 枠超え: 子の予定が親の枠（予定終了日）を超えた部分を、タスクの遅れと同じ淡い色と斜線で示す
        if (row.FrameEnd is { } frame && X(frame.DayNumber + 1) is var limit && limit < right - 0.5f)
        {
            ds.FillRectangle(limit, cy - 4, right - limit, 8, WithAlpha(c.Delay, 0.16));
            DrawHatch(ds, limit, right, cy - 4, cy + 4, WithAlpha(c.Delay, 0.6));
            ds.FillRectangle(limit - 0.75f, cy - 5, 1.5f, 10, c.Delay);
        }

        // 枠の始まりより前に子が始まる部分も、同じ表現で示す
        if (row.FrameStart is { } frameStart && X(frameStart) is var begin && begin > left + 0.5f)
        {
            ds.FillRectangle(left, cy - 4, begin - left, 8, WithAlpha(c.Delay, 0.16));
            DrawHatch(ds, left, begin, cy - 4, cy + 4, WithAlpha(c.Delay, 0.6));
            ds.FillRectangle(begin - 0.75f, cy - 5, 1.5f, 10, c.Delay);
        }
    }

    private void DrawDependencies(CanvasDrawingSession ds, ChartColors c, int firstRow, int lastRow, int selected)
    {
        const float gap = 8;
        foreach (var row in _rows)
        {
            foreach (int predIndex in row.Predecessors)
            {
                // 展開している親タスクは、その終わり・始まりを決めている子から出し、子で受ける
                int fromIndex = _rows[predIndex].ArrowFrom;
                int toIndex = row.ArrowTo;

                // 表示範囲をまたぐ矢印だけを描く
                if (Math.Max(fromIndex, toIndex) < firstRow || Math.Min(fromIndex, toIndex) > lastRow)
                {
                    continue;
                }

                if (BarExtent(_rows[fromIndex]) is not { } from || BarExtent(_rows[toIndex]) is not { } to)
                {
                    continue;
                }

                bool highlight = predIndex == selected || row.Index == selected || fromIndex == selected || toIndex == selected;
                var color = highlight ? c.Accent : c.TextTertiary;
                float stroke = highlight ? 1.5f : 1;
                float y1 = RowCenter(fromIndex);
                float y2 = RowCenter(toIndex);
                float x1 = from.Right;
                float x2 = to.Left;

                ds.DrawLine(x1, y1, x1 + gap, y1, color, stroke);
                if (x2 - gap >= x1 + gap)
                {
                    ds.DrawLine(x1 + gap, y1, x1 + gap, y2, color, stroke);
                    ds.DrawLine(x1 + gap, y2, x2 - 1, y2, color, stroke);
                }
                else
                {
                    // 後続タスクが先に始まる場合は、行の境目で折り返す
                    float yMid = y2 + (y2 > y1 ? -1 : 1) * (float)RowHeight / 2;
                    ds.DrawLine(x1 + gap, y1, x1 + gap, yMid, color, stroke);
                    ds.DrawLine(x1 + gap, yMid, x2 - gap, yMid, color, stroke);
                    ds.DrawLine(x2 - gap, yMid, x2 - gap, y2, color, stroke);
                    ds.DrawLine(x2 - gap, y2, x2 - 1, y2, color, stroke);
                }

                // 矢じり
                ds.DrawLine(x2 - 5, y2 - 3.5f, x2, y2, color, 1.5f);
                ds.DrawLine(x2 - 5, y2 + 3.5f, x2, y2, color, 1.5f);
            }
        }

        DrawInheritedDependencies(ds, c, selected);
    }

    /// <summary>
    /// 選んでいるタスクが親から受け継いでいる依存関係を、先行から選んでいるタスクへの点線で示す（要件 F-DEP-10）。
    /// すべての子に引くと線が増えすぎるため、選んでいる行だけに引く。
    /// </summary>
    private void DrawInheritedDependencies(CanvasDrawingSession ds, ChartColors c, int selected)
    {
        if (selected < 0 || selected >= _rows.Count || BarExtent(_rows[selected]) is not { } to)
        {
            return;
        }

        var rowOf = _rows.Where(r => r.Node is not null).ToDictionary(r => r.Node!, r => r.Index);
        float y2 = RowCenter(selected);
        foreach (var ancestor in _rows[selected].Node?.Ancestors() ?? [])
        {
            if (!rowOf.TryGetValue(ancestor, out int ancestorIndex))
            {
                continue;
            }

            // 祖先の矢印をこの行で受けているなら、実線の矢印が既に届いている
            if (_rows[ancestorIndex].ArrowTo == selected)
            {
                continue;
            }

            foreach (int predIndex in _rows[ancestorIndex].Predecessors)
            {
                int fromIndex = _rows[predIndex].ArrowFrom;
                if (BarExtent(_rows[fromIndex]) is not { } from)
                {
                    continue;
                }

                float y1 = RowCenter(fromIndex);
                float x1 = from.Right;
                float x2 = to.Left;
                float xm = x1 + 8;
                ds.DrawLine(x1, y1, xm, y1, c.Accent, 1, _dashStyle);
                ds.DrawLine(xm, y1, xm, y2, c.Accent, 1, _dashStyle);
                ds.DrawLine(xm, y2, x2 - 1, y2, c.Accent, 1, _dashStyle);
                ds.DrawLine(x2 - 5, y2 - 3.5f, x2, y2, c.Accent, 1.5f);
                ds.DrawLine(x2 - 5, y2 + 3.5f, x2, y2, c.Accent, 1.5f);
            }
        }
    }

    private void DrawInazuma(CanvasDrawingSession ds, ChartColors c, int firstRow, int lastRow, DateOnly today)
    {
        if (!_hasInazuma || lastRow < firstRow)
        {
            return;
        }

        float todayX = X(today);
        int from = Math.Max(firstRow - 1, 0);
        int to = Math.Min(lastRow + 1, _rows.Count - 1);

        using var builder = new CanvasPathBuilder(ds);
        builder.BeginFigure(todayX, from == 0 ? (float)HeaderHeight : RowTop(from));
        for (int i = from; i <= to; i++)
        {
            // マイルストーンと中止したタスクの行は進み具合の点を持たないため、前後のタスクの点を直接結ぶ
            if (_rows[i].IsOnInazuma)
            {
                builder.AddLine(_rows[i].Inazuma is { } p ? X(p) : todayX, RowCenter(i));
            }
        }

        builder.AddLine(todayX, to == _rows.Count - 1 ? RowTop(to) + (float)RowHeight : RowTop(to + 1));
        builder.EndFigure(CanvasFigureLoop.Open);
        using var path = CanvasGeometry.CreatePath(builder);
        ds.DrawGeometry(path, c.Delay, 2, _roundJoin);
    }

    private void DrawDragPreview(CanvasDrawingSession ds, ChartColors c)
    {
        if (_drag is not { } drag)
        {
            return;
        }

        if (RowOf(drag) is not { } dragged)
        {
            return;
        }

        if (drag.Mode == DragMode.Link)
        {
            if (BarExtent(dragged) is { } from)
            {
                float y1 = RowCenter(dragged.Index);
                ds.DrawLine(from.Right + LinkHandleOffset, y1, (float)drag.Pointer.X, (float)drag.Pointer.Y, c.Accent, 1.5f);
                if (_rows.FirstOrDefault(r => r.Key == drag.LinkTargetItemId) is { } target && BarExtent(target) is { } to)
                {
                    ds.DrawRoundedRectangle(to.Left - 3, RowCenter(target.Index) - BarHeight / 2 - 4, to.Right - to.Left + 6, BarHeight + 8, 6, 6, c.Accent, 2);
                }
            }

            return;
        }

        if (!drag.Changed)
        {
            return;
        }

        float cy = RowCenter(dragged.Index);
        float left = X(drag.NewStart);
        float right = X(drag.NewEnd.DayNumber + 1);
        ds.DrawRoundedRectangle(left - 0.5f, cy - BarHeight / 2 - 2.5f, right - left + 1, BarHeight + 5, 5, 5, c.Accent, 2);

        // 変更後の日付と稼働日数
        var label = $"{FormatDate(drag.NewStart)} 〜 {FormatDate(drag.NewEnd)}  {WorkCalendar.CountWorkingDays(drag.NewStart, drag.NewEnd)} 稼働日";
        using var layout = new CanvasTextLayout(ds, label, _labelFormat, 400, 20);
        var bounds = layout.LayoutBounds;
        float lx = Math.Clamp(left, 4, Math.Max((float)Chart.ActualWidth - (float)bounds.Width - 16, 4));
        float ly = cy - BarHeight / 2 - 26;
        if (ly < HeaderHeight + 2)
        {
            ly = cy + BarHeight / 2 + 6;
        }

        ds.FillRoundedRectangle(lx, ly, (float)bounds.Width + 12, 20, 4, 4, c.Accent);
        ds.DrawTextLayout(layout, lx + 6, ly + 3, c.OnAccent);
    }

    private void DrawHeader(CanvasDrawingSession ds, ChartColors c, DateOnly first, DateOnly last, float width, DateOnly today)
    {
        const float tier1 = 22, tier2 = 40;
        float dayWidth = (float)_dayWidth;
        ds.DrawLine(0, (float)HeaderHeight - 0.5f, width, (float)HeaderHeight - 0.5f, c.Divider, 1);

        // 上段: 月（月表示では年）。左端で切れる見出しは左端に寄せて表示する
        bool monthScale = dayWidth < 6;
        float stickyLimit = float.MaxValue;
        var boundaries = new List<(float X, string Label)>();
        for (var d = first; d <= last; d = d.AddDays(1))
        {
            if (d == first || (monthScale ? d.DayOfYear == 1 : d.Day == 1))
            {
                boundaries.Add((X(d), monthScale ? $"{d.Year}年" : $"{d.Year}年{d.Month}月"));
            }
        }

        for (int i = boundaries.Count - 1; i >= 0; i--)
        {
            var (x, label) = boundaries[i];
            float lx = Math.Max(x, 0) + 6;
            if (lx + 60 > stickyLimit && i == 0)
            {
                lx = Math.Min(lx, stickyLimit - 70);
            }

            ds.DrawTextLayout(TextLayout(ds, label, _headerFormat), lx, 3, c.TextSecondary);
            stickyLimit = x;
            if (i > 0 || x > 0)
            {
                ds.DrawLine(x, 0, x, tier1, c.Grid, 1);
            }
        }

        // 下段: 日（日表示）、週の始まり（週表示）、月（月表示）
        float lastLabelEnd = float.MinValue;
        for (var d = first; d <= last; d = d.AddDays(1))
        {
            float x = X(d);
            bool holiday = WorkCalendar.IsHoliday(d);
            if (dayWidth >= 18)
            {
                var number = TextLayout(ds, DayNumbers[d.Day], _dayFormat);
                ds.DrawTextLayout(number, x + (dayWidth - (float)number.LayoutBounds.Width) / 2, tier1 + 1, holiday ? c.TextTertiary : c.TextSecondary);
                if (JapaneseHolidays.NameOf(d) is { } name && x >= lastLabelEnd)
                {
                    var layout = TextLayout(ds, name, _captionFormat);
                    float lx = x + 2;
                    ds.DrawTextLayout(layout, lx, tier2, c.TextTertiary);
                    lastLabelEnd = lx + (float)layout.LayoutBounds.Width + 4;
                }
            }
            else if (!monthScale && d.DayOfWeek == DayOfWeek.Monday)
            {
                ds.DrawTextLayout(TextLayout(ds, $"{d.Month}/{d.Day}", _headerFormat), x + 3, tier1 + 1, c.TextSecondary);
            }
            else if (monthScale && d.Day == 1 && dayWidth * 28 >= 24)
            {
                ds.DrawTextLayout(TextLayout(ds, $"{d.Month}月", _captionFormat), x + 3, tier1 + 1, c.TextSecondary);
            }
        }

        // マイルストーンの名前（上段の右寄せで、縦線で終わる丸い札にする）
        _milestoneHits.Clear();
        foreach (var s in _milestones)
        {
            float mx = MilestoneX(s.Milestone.Due!.Value);
            if (mx < -400 || mx > width + 400)
            {
                continue;
            }

            var layout = TextLayout(ds, "◆ " + s.Milestone.Title, _headerFormat);
            float w = (float)layout.LayoutBounds.Width + 14;
            float lx = mx - w;
            ds.FillRoundedRectangle(lx, 2, w, 18, 9, 9, c.Surface);
            ds.DrawRoundedRectangle(lx, 2, w, 18, 9, 9, WithAlpha(c.Text, 0.55), 1);
            ds.DrawTextLayout(layout, lx + 7, 2, c.Text);
            ds.DrawLine(mx, 20, mx, (float)HeaderHeight, WithAlpha(c.Text, 0.55), 1.5f, _milestoneDash);
            _milestoneHits.Add((new Rect(lx, 2, w, 18), s));
        }

        // 今日の日付ラベル
        float todayX = X(today);
        if (todayX >= -40 && todayX <= width + 40)
        {
            string label = $"{today.Month}/{today.Day}";
            var layout = TextLayout(ds, label, _headerFormat);
            float w = (float)layout.LayoutBounds.Width + 10;
            float lx = Math.Clamp(todayX - w / 2, 0, width - w);
            ds.FillRoundedRectangle(lx, tier1 + 1, w, 17, 4, 4, c.Today);
            ds.DrawTextLayout(layout, lx + 5, tier1 + 1, c.OnAccent);
        }
    }

    private CanvasTextLayout TextLayout(CanvasDrawingSession ds, string text, CanvasTextFormat format)
    {
        if (!_textCache.TryGetValue((text, format), out var layout))
        {
            if (_textCache.Count > 512)
            {
                ClearTextCache();
            }

            layout = new CanvasTextLayout(ds, text, format, 1000, 40);
            _textCache[(text, format)] = layout;
        }

        return layout;
    }

    private void ClearTextCache()
    {
        foreach (var layout in _textCache.Values)
        {
            layout.Dispose();
        }

        _textCache.Clear();
    }

    private static Color WithAlpha(Color color, double factor) => Color.FromArgb((byte)(color.A * factor), color.R, color.G, color.B);

    // ================================================================ ポインター操作
}
