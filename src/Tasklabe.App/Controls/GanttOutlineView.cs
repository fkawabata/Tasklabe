using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Tasklabe.App.Services;
using Tasklabe.Core.Export;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI;

namespace Tasklabe.App.Controls;

/// <summary>
/// 共有ビュー（UI デザイン設計書 3.3.6 節）。計画を他の人に説明するための、画面の幅いっぱいに収めたガントチャート。
/// Mermaid に書き出すものと同じ形（<see cref="GanttOutline"/>）を、Win2D でネイティブに描く。画像（PNG）の書き出しにも同じ描き方を使う。
/// </summary>
public sealed partial class GanttOutlineView : UserControl
{
    private const float Pad = 24;
    private const float TitleHeight = 40;
    private const float AxisHeight = 44;
    private const float RowHeight = 30;
    private const float BarHeight = 20;

    private readonly CanvasControl _canvas = new();
    private GanttOutline? _outline;
    private DateOnly _today;

    public GanttOutlineView()
    {
        // 高さは行の数で決まるため、上から詰めて描く
        VerticalAlignment = VerticalAlignment.Top;
        _canvas.VerticalAlignment = VerticalAlignment.Top;
        Content = _canvas;
        _canvas.Draw += (sender, args) => Draw(args.DrawingSession, (float)sender.ActualWidth, Colors());
        ActualThemeChanged += (_, _) => _canvas.Invalidate();
        Unloaded += (_, _) => _canvas.RemoveFromVisualTree();
    }

    /// <summary>描く形を替える。高さは形から決め、幅は置かれた場所いっぱいに広げる。</summary>
    public void Show(GanttOutline outline, DateOnly today)
    {
        _outline = outline;
        _today = today;
        _canvas.Height = HeightOf(outline);
        AutomationProperties.SetName(this, outline.ItemCount == 0
            ? "共有ビュー: 表示するタスクがありません"
            : $"共有ビュー: {outline.Title} {outline.ItemCount} 件（内容は「コード」で読めます）");
        _canvas.Invalidate();
    }

    /// <summary>いま見えている形のまま、PNG の画像にする（幅はビューと同じ、解像度は 2 倍）。</summary>
    public async Task<IRandomAccessStream?> RenderPngAsync()
    {
        if (_outline is null || _canvas.ActualWidth <= 0)
        {
            return null;
        }

        float width = (float)_canvas.ActualWidth;
        float height = HeightOf(_outline);
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 192);
        var colors = Colors();
        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(colors.Background);
            Draw(ds, width, colors);
        }

        var stream = new InMemoryRandomAccessStream();
        await target.SaveAsync(stream, CanvasBitmapFileFormat.Png);
        stream.Seek(0);
        return stream;
    }

    private static float HeightOf(GanttOutline outline) =>
        Pad * 2 + TitleHeight + AxisHeight + Math.Max(outline.ItemCount, 1) * RowHeight;

    // ================================================================ 色

    private sealed record Palette(
        Color Background, Color Text, Color TextSecondary, Color TextTertiary, Color Grid, Color Band,
        Color Progress, Color ProgressSoft, Color Delay, Color Done, Color Today, Color Holiday, Color OnAccent);

    /// <summary>テーマの色（アプリで選んだテーマに従う）。</summary>
    private static Palette Colors() => new(
        ThemeColor("SolidBackgroundFillColorBaseBrush"),
        ThemeColor("TextFillColorPrimaryBrush"),
        ThemeColor("TextFillColorSecondaryBrush"),
        ThemeColor("TextFillColorTertiaryBrush"),
        ThemeColor("Viz.Grid"),
        ThemeColor("SubtleFillColorSecondaryBrush"),
        ThemeColor("Viz.Progress"),
        ThemeColor("Viz.ProgressRemain"),
        ThemeColor("Viz.Delay"),
        ThemeColor("Viz.Done"),
        ThemeColor("Viz.Today"),
        ThemeColor("Viz.Holiday"),
        ThemeColor("TextOnAccentFillColorPrimaryBrush"));

    private static Color ThemeColor(string key)
    {
        if (ThemeResources.Brush(key) is not SolidColorBrush brush)
        {
            return Microsoft.UI.Colors.Transparent;
        }

        var c = brush.Color;
        return Windows.UI.Color.FromArgb((byte)(c.A * brush.Opacity), c.R, c.G, c.B);
    }

    // ================================================================ 描画

    private static readonly CanvasTextFormat TitleFormat = new() { FontFamily = "Segoe UI Variable Display, BIZ UDPGothic", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.Bold, WordWrapping = CanvasWordWrapping.NoWrap };
    private static readonly CanvasTextFormat SectionFormat = new() { FontFamily = "Segoe UI Variable Text, BIZ UDPGothic", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Bold, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character };
    private static readonly CanvasTextFormat LabelFormat = new() { FontFamily = "Segoe UI Variable Text, BIZ UDPGothic", FontSize = 12, WordWrapping = CanvasWordWrapping.NoWrap };
    private static readonly CanvasTextFormat AxisFormat = new() { FontFamily = "Segoe UI Variable Text, BIZ UDPGothic", FontSize = 12, WordWrapping = CanvasWordWrapping.NoWrap };

    private void Draw(CanvasDrawingSession ds, float width, Palette c)
    {
        if (_outline is not { } outline)
        {
            return;
        }

        float y = Pad;
        if (outline.Title.Length > 0)
        {
            using var title = new CanvasTextLayout(ds, outline.Title, TitleFormat, width - Pad * 2, TitleHeight);
            ds.DrawTextLayout(title, (float)(width - title.LayoutBounds.Width) / 2, y, c.Text);
        }

        y += TitleHeight;
        if (outline.Span is not { } span || outline.ItemCount == 0)
        {
            using var empty = new CanvasTextLayout(ds, "表示するタスクがありません。期間や含めるものを見直してください。", LabelFormat, width - Pad * 2, RowHeight);
            ds.DrawTextLayout(empty, (float)(width - empty.LayoutBounds.Width) / 2, y + AxisHeight, c.TextSecondary);
            return;
        }

        // 区切りの名前の列は、いちばん長い名前に合わせる（最大 220）
        float labelWidth = 0;
        if (outline.ShowSections)
        {
            foreach (var section in outline.Sections)
            {
                using var layout = new CanvasTextLayout(ds, SectionName(section), SectionFormat, 1000, RowHeight);
                labelWidth = Math.Max(labelWidth, (float)layout.LayoutBounds.Width);
            }

            labelWidth = Math.Min(labelWidth + 24, 220);
        }

        float left = Pad + labelWidth;
        float right = width - Pad;
        int days = span.End.DayNumber - span.Start.DayNumber + 1;
        float dayWidth = Math.Max((right - left) / days, 0.5f);
        float X(DateOnly d) => left + (d.DayNumber - span.Start.DayNumber) * dayWidth;

        float top = y + AxisHeight;
        float bottom = top + outline.ItemCount * RowHeight;

        // 区切りの帯（交互に塗る）と名前
        float rowTop = top;
        for (int i = 0; i < outline.Sections.Count; i++)
        {
            var section = outline.Sections[i];
            float h = section.Items.Count * RowHeight;
            if (i % 2 == 0)
            {
                ds.FillRectangle(Pad, rowTop, width - Pad * 2, h, c.Band);
            }

            if (outline.ShowSections)
            {
                using var name = new CanvasTextLayout(ds, SectionName(section), SectionFormat, labelWidth - 16, h)
                {
                    VerticalAlignment = CanvasVerticalAlignment.Center,
                    Options = CanvasDrawTextOptions.Clip,
                    TrimmingSign = CanvasTrimmingSign.Ellipsis,
                };
                ds.DrawTextLayout(name, Pad + 8, rowTop, c.Text);
            }

            rowTop += h;
        }

        // 休日の網掛けと、目盛り
        foreach (var holiday in outline.Holidays)
        {
            ds.FillRectangle(X(holiday), top, dayWidth, bottom - top, c.Holiday);
        }

        DrawAxis(ds, c, span.Start, span.End, dayWidth, X, y, top, bottom);

        // バーとマイルストーン
        rowTop = top;
        foreach (var item in outline.Sections.SelectMany(s => s.Items))
        {
            DrawItem(ds, c, item, X(item.Start), X(item.End.AddDays(1)), rowTop + RowHeight / 2, right);
            rowTop += RowHeight;
        }

        // 今日の線
        if (outline.TodayMarker && _today >= span.Start && _today <= span.End)
        {
            float tx = X(_today) + dayWidth / 2;
            ds.DrawLine(tx, top - 6, tx, bottom, c.Today, 2);
            using var label = new CanvasTextLayout(ds, "今日", AxisFormat, 100, 20);
            float lw = (float)label.LayoutBounds.Width + 10;
            ds.FillRoundedRectangle(tx - lw / 2, top - 22, lw, 17, 4, 4, c.Today);
            ds.DrawTextLayout(label, tx - lw / 2 + 5, top - 22, c.OnAccent);
        }
    }

    private static string SectionName(OutlineSection section) => section.Name.Length > 0 ? section.Name : PlanOutline.OtherSection;

    /// <summary>
    /// 目盛り。日の幅に応じて、日ごと（22 px 以上）、週の始まりごと（5 px 以上）、月の初めごとに出す。上段には月を出す。
    /// </summary>
    private static void DrawAxis(CanvasDrawingSession ds, Palette c, DateOnly first, DateOnly last, float dayWidth,
        Func<DateOnly, float> x, float y, float top, float bottom)
    {
        float tier2 = y + 22;
        var lastLabelEnd = float.MinValue;
        for (var d = first; d <= last; d = d.AddDays(1))
        {
            float dx = x(d);
            if (d == first || d.Day == 1)
            {
                using var month = new CanvasTextLayout(ds, $"{d.Year}年{d.Month}月", AxisFormat, 200, 20);
                if (dx >= lastLabelEnd)
                {
                    ds.DrawTextLayout(month, dx + 2, y, c.TextSecondary);
                    lastLabelEnd = dx + (float)month.LayoutBounds.Width + 12;
                }
            }

            bool tick = dayWidth >= 22 || (dayWidth >= 5 ? d.DayOfWeek == DayOfWeek.Monday : d.Day == 1);
            if (!tick)
            {
                continue;
            }

            ds.DrawLine(dx, top, dx, bottom, c.Grid, 1);
            string text = dayWidth >= 22 ? d.Day.ToString(DateText.Japanese) : dayWidth >= 5 ? $"{d.Month}/{d.Day}" : $"{d.Month}月";
            using var layout = new CanvasTextLayout(ds, text, AxisFormat, 100, 20);
            float lx = dayWidth >= 22 ? dx + (dayWidth - (float)layout.LayoutBounds.Width) / 2 : dx + 3;
            ds.DrawTextLayout(layout, lx, tier2, c.TextTertiary);
        }

        ds.DrawLine(x(first), top - 0.5f, x(last.AddDays(1)), top - 0.5f, c.Grid, 1);
    }

    /// <summary>
    /// バー 1 本。完了は灰色、進行中は強調色、遅れは Viz.Delay で塗り、未着手は淡い強調色に枠を付ける。
    /// 名前はバーに収まれば中に、収まらなければバーの右（右端を越えるなら左）に置く。
    /// </summary>
    private static void DrawItem(CanvasDrawingSession ds, Palette c, OutlineItem item, float x1, float x2, float cy, float right)
    {
        using var label = new CanvasTextLayout(ds, item.Name, LabelFormat, 2000, BarHeight);
        float textWidth = (float)label.LayoutBounds.Width;
        float textY = cy - (float)label.LayoutBounds.Height / 2;

        if (item.Milestone)
        {
            var fill = item.Critical ? c.Delay : item.Done ? c.Done : c.Text;
            float mx = x1 + (x2 - x1) / 2;
            using var diamond = Microsoft.Graphics.Canvas.Geometry.CanvasGeometry.CreatePolygon(ds,
                [new Vector2(mx, cy - 7), new Vector2(mx + 7, cy), new Vector2(mx, cy + 7), new Vector2(mx - 7, cy)]);
            ds.FillGeometry(diamond, fill);
            bool fitsRight = mx + 12 + textWidth <= right;
            ds.DrawTextLayout(label, fitsRight ? mx + 12 : mx - 12 - textWidth, textY, c.Text);
            return;
        }

        float w = Math.Max(x2 - x1, 2);
        var rect = new Rect(x1, cy - BarHeight / 2, w, BarHeight);
        (Color barFill, Color inside) = item.Critical ? (c.Delay, c.OnAccent)
            : item.Done ? (c.Done, c.Text)
            : item.Active ? (c.Progress, c.OnAccent)
            : (c.ProgressSoft, c.Text);
        ds.FillRoundedRectangle(rect, 4, 4, barFill);
        if (!item.Critical && !item.Done && !item.Active)
        {
            ds.DrawRoundedRectangle(rect, 4, 4, c.Progress, 1);
        }

        if (textWidth + 12 <= w)
        {
            ds.DrawTextLayout(label, x1 + (w - textWidth) / 2, textY, inside);
        }
        else if (x1 + w + 6 + textWidth <= right)
        {
            ds.DrawTextLayout(label, x1 + w + 6, textY, c.Text);
        }
        else
        {
            ds.DrawTextLayout(label, Math.Max(x1 - 6 - textWidth, 0), textY, c.Text);
        }
    }
}
