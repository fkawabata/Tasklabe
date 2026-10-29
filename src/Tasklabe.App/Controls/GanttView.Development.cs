using System.Globalization;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Tasklabe.App.Services;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Gantt;
using Windows.Foundation;
using Windows.UI;

namespace Tasklabe.App.Controls;

/// <summary>
/// 開発の段階（要件 F-UI-GT-12、UI デザイン設計書 3.3.8 節）。ブランチか PR を持つタスクのバーの右にアイコンを 1 つ置き、
/// 形で段階を、右上の印（指摘の件数、承認の ✓）で状態を示す。
/// </summary>
public sealed partial class GanttView
{
    // GitHub の Octicons（MIT ライセンス）の 16 px の形。GitHub の画面と同じ見た目にする
    private const string BranchPath = "M9.5 3.25a2.25 2.25 0 1 1 3 2.122V6A2.5 2.5 0 0 1 10 8.5H6a1 1 0 0 0-1 1v1.128a2.251 2.251 0 1 1-1.5 0V5.372a2.25 2.25 0 1 1 1.5 0v1.836A2.493 2.493 0 0 1 6 7h4a1 1 0 0 0 1-1v-.628A2.25 2.25 0 0 1 9.5 3.25Zm-6 0a.75.75 0 1 0 1.5 0 .75.75 0 0 0-1.5 0Zm8.25-.75a.75.75 0 1 0 0 1.5.75.75 0 0 0 0-1.5ZM4.25 12a.75.75 0 1 0 0 1.5.75.75 0 0 0 0-1.5Z";
    internal const string PullRequestPath = "M1.5 3.25a2.25 2.25 0 1 1 3 2.122v5.256a2.251 2.251 0 1 1-1.5 0V5.372A2.25 2.25 0 0 1 1.5 3.25Zm5.677-.177L9.573.677A.25.25 0 0 1 10 .854V2.5h1A2.5 2.5 0 0 1 13.5 5v5.628a2.251 2.251 0 1 1-1.5 0V5a1 1 0 0 0-1-1h-1v1.646a.25.25 0 0 1-.427.177L7.177 3.427a.25.25 0 0 1 0-.354ZM3.75 2.5a.75.75 0 1 0 0 1.5.75.75 0 0 0 0-1.5Zm0 9.5a.75.75 0 1 0 0 1.5.75.75 0 0 0 0-1.5Zm8.25.75a.75.75 0 1 0 1.5 0 .75.75 0 0 0-1.5 0Z";
    private const string DraftPath = "M3.25 1A2.25 2.25 0 0 1 4 5.372v5.256a2.251 2.251 0 1 1-1.5 0V5.372A2.251 2.251 0 0 1 3.25 1Zm9.5 14a2.25 2.25 0 1 1 0-4.5 2.25 2.25 0 0 1 0 4.5ZM2.5 3.25a.75.75 0 1 0 1.5 0 .75.75 0 0 0-1.5 0ZM3.25 12a.75.75 0 1 0 0 1.5.75.75 0 0 0 0-1.5Zm9.5 0a.75.75 0 1 0 0 1.5.75.75 0 0 0 0-1.5ZM14 7.5a1.25 1.25 0 1 1-2.5 0 1.25 1.25 0 0 1 2.5 0Zm0-4.25a1.25 1.25 0 1 1-2.5 0 1.25 1.25 0 0 1 2.5 0Z";
    private const string MergePath = "M5.45 5.154A4.25 4.25 0 0 0 9.25 7.5h1.378a2.251 2.251 0 1 1 0 1.5H9.25A5.734 5.734 0 0 1 5 7.123v3.505a2.25 2.25 0 1 1-1.5 0V5.372a2.25 2.25 0 1 1 1.95-.218ZM4.25 13.5a.75.75 0 1 0 0-1.5.75.75 0 0 0 0 1.5Zm8.5-4.5a.75.75 0 1 0 0-1.5.75.75 0 0 0 0 1.5ZM5 3.25a.75.75 0 1 0 0 .005V3.25Z";
    private const string CheckPath = "M13.78 4.22a.75.75 0 0 1 0 1.06l-7.25 7.25a.75.75 0 0 1-1.06 0L2.22 9.28a.751.751 0 0 1 .018-1.042.751.751 0 0 1 1.042-.018L6 10.94l6.72-6.72a.75.75 0 0 1 1.06 0Z";

    private const float DevelopmentIconSize = 16;
    private const float BadgeHeight = 14;

    /// <summary>承認の ✓ の大きさ（アイコンの右上に重ねる）。</summary>
    private const float CheckScale = 0.625f;

    /// <summary>アイコンの形。描画のたびに作らないよう、デバイスごとに 1 回だけ作る（技術設計書 5 章）。</summary>
    private sealed record DevelopmentShapes(CanvasGeometry Branch, CanvasGeometry PullRequest, CanvasGeometry Draft, CanvasGeometry Merge, CanvasGeometry Check);

    private DevelopmentShapes? _developmentShapes;
    private CanvasDevice? _developmentShapesDevice;

    /// <summary>描いたアイコンの範囲（押したとき・重ねたときに使う）。</summary>
    private readonly List<(Rect Bounds, GanttRow Row, DevelopmentStatus Status)> _developmentHits = [];

    /// <summary>開発の段階を表示するか（要件 F-UI-GT-12）。</summary>
    public bool ShowDevelopment
    {
        get;
        set
        {
            field = value;
            HideHover();
            RequestRender();
        }
    } = true;

    /// <summary>取り直した段階を描き直す。</summary>
    public void RefreshDevelopment() => RequestRender();

    private DevelopmentShapes ShapesFor(CanvasDevice device)
    {
        if (_developmentShapes is null || !ReferenceEquals(_developmentShapesDevice, device))
        {
            using var check = SvgPath.Parse(device, CheckPath);
            _developmentShapes = new DevelopmentShapes(SvgPath.Parse(device, BranchPath), SvgPath.Parse(device, PullRequestPath),
                SvgPath.Parse(device, DraftPath), SvgPath.Parse(device, MergePath), check.Transform(Matrix3x2.CreateScale(CheckScale)));
            _developmentShapesDevice = device;
        }

        return _developmentShapes;
    }

    /// <summary>アイコンの左端。編集モードでは、依存関係を追加する点（バーの右）を避けて置く。</summary>
    private float DevelopmentLeft(float barRight) => barRight + (_editing ? LinkHandleOffset + 8 : 4);

    /// <summary>
    /// 見えている行のアイコンを描く。下を通る依存関係の矢印と分けるよう、行の下地の色で縁取ってから塗る。
    /// </summary>
    private void DrawDevelopment(CanvasDrawingSession ds, CanvasDevice device, ChartColors c, int firstRow, int lastRow)
    {
        _developmentHits.Clear();
        if (!ShowDevelopment)
        {
            return;
        }

        DevelopmentShapes? shapes = null;
        for (int i = firstRow; i <= lastRow; i++)
        {
            var row = _rows[i];
            if (row.Kind != GanttRowKind.Task || TaskDevelopment.Cached(row.Task.IssueId) is not { Stage: not DevelopmentStage.None } status
                || BarExtent(row) is not { } extent)
            {
                continue;
            }

            shapes ??= ShapesFor(device);
            float cy = RowCenter(i);
            float left = DevelopmentLeft(extent.Right);
            var origin = new Vector2(left, cy - DevelopmentIconSize / 2);
            var halo = i == _frameSelected ? ChartColors.Over(c.Selection, c.Surface)
                : i == _hoverRow && _drag is null ? ChartColors.Over(c.Hover, c.Surface)
                : c.Surface;

            var (shape, color) = status.Stage switch
            {
                DevelopmentStage.Working => (shapes.Branch, c.TextSecondary),
                DevelopmentStage.Draft => (shapes.Draft, c.TextSecondary),
                DevelopmentStage.Merged => (shapes.Merge, c.Done),
                _ => (shapes.PullRequest, c.TextSecondary),
            };
            ds.DrawGeometry(shape, origin, halo, 4);
            ds.FillGeometry(shape, origin, color);
            float right = left + DevelopmentIconSize;

            if (status.Stage == DevelopmentStage.Commented)
            {
                right = DrawBadge(ds, c, status.UnresolvedThreads, left + DevelopmentIconSize - 4, origin.Y - 5, halo);
            }
            else if (status.Stage == DevelopmentStage.Approved)
            {
                var at = new Vector2(left + DevelopmentIconSize - 6, origin.Y - 4);
                ds.DrawGeometry(shapes.Check, at, halo, 3);
                ds.FillGeometry(shapes.Check, at, c.Progress);
                right = at.X + 16 * CheckScale;
            }

            _developmentHits.Add((new Rect(left - 2, cy - RowHeight / 2, right - left + 4, RowHeight), row, status));
        }
    }

    /// <summary>未解決の指摘の件数のバッジ（9 を超えたら 9+）。右端を返す。</summary>
    private float DrawBadge(CanvasDrawingSession ds, ChartColors c, int count, float left, float top, Color halo)
    {
        var layout = TextLayout(ds, count > 9 ? "9+" : count.ToString(CultureInfo.InvariantCulture), _headerFormat);
        float width = Math.Max(BadgeHeight, (float)layout.LayoutBounds.Width + 8);
        const float radius = BadgeHeight / 2;
        ds.FillRoundedRectangle(left - 1.5f, top - 1.5f, width + 3, BadgeHeight + 3, radius + 1.5f, radius + 1.5f, halo);
        ds.FillRoundedRectangle(left, top, width, BadgeHeight, radius, radius, c.Caution);
        ds.DrawTextLayout(layout, left + (width - (float)layout.LayoutBounds.Width) / 2 - (float)layout.LayoutBounds.X,
            top + (BadgeHeight - (float)layout.LayoutBounds.Height) / 2 - (float)layout.LayoutBounds.Y, c.OnAccent);
        return left + width;
    }

    private (GanttRow Row, DevelopmentStatus Status)? DevelopmentAt(Point position) =>
        _developmentHits.Where(h => h.Bounds.Contains(position)).Select(h => ((GanttRow, DevelopmentStatus)?)(h.Row, h.Status)).LastOrDefault();

    /// <summary>アイコンを押したら、示している段階の PR（PR がなければブランチ）を GitHub で開く。</summary>
    private static void OpenDevelopment(DevelopmentStatus status)
    {
        if ((status.Lead?.Url ?? status.Branches.FirstOrDefault()?.Url) is { Length: > 0 } url)
        {
            Browser.Open(url);
        }
    }

    /// <summary>PR ごとの段階・番号・タイトル・未解決の指摘の件数。PR がなければブランチを並べる。</summary>
    private void ShowDevelopmentHover(GanttRow row, DevelopmentStatus status, Point position)
    {
        HoverTitle.Text = row.Task.Title;
        var lines = status.PullRequests.Count > 0
            ? status.PullRequests.Select(p => p.UnresolvedThreads > 0 && p.Stage == DevelopmentStage.Commented
                ? Texts.Format("Gantt_DevPullRequestUnresolved", StageName(p.Stage), p.Number, p.Title, Texts.Count("Gantt_DevUnresolved", p.UnresolvedThreads))
                : Texts.Format("Gantt_DevPullRequest", StageName(p.Stage), p.Number, p.Title))
            : status.Branches.Select(b => Texts.Format("Gantt_DevBranch", StageName(DevelopmentStage.Working), b.Name));
        HoverDetail.Text = string.Join('\n', lines);
        HoverDelay.Visibility = Visibility.Collapsed;
        HoverCard.Visibility = Visibility.Visible;
        HoverCard.Measure(new Size(320, 400));
        var size = HoverCard.DesiredSize;
        Canvas.SetLeft(HoverCard, Math.Clamp(position.X + 16, 0, Math.Max(Chart.ActualWidth - size.Width - 4, 0)));
        double top = position.Y + 20;
        Canvas.SetTop(HoverCard, top + size.Height > Chart.ActualHeight ? position.Y - size.Height - 8 : top);
    }

    internal static string StageName(DevelopmentStage stage) => stage switch
    {
        DevelopmentStage.Working => Texts.Get("Gantt_DevWorking"),
        DevelopmentStage.Draft => Texts.Get("Gantt_DevDraft"),
        DevelopmentStage.InReview => Texts.Get("Gantt_DevInReview"),
        DevelopmentStage.Commented => Texts.Get("Gantt_DevCommented"),
        DevelopmentStage.Approved => Texts.Get("Gantt_DevApproved"),
        DevelopmentStage.Merged => Texts.Get("Gantt_DevMerged"),
        _ => "",
    };

    /// <summary>読み上げで伝える段階（「レビュー待ち、未解決の指摘 3 件」）。</summary>
    private static string? DescribeDevelopment(GanttRow row)
    {
        if (row.Kind != GanttRowKind.Task || TaskDevelopment.Cached(row.Task.IssueId) is not { Stage: not DevelopmentStage.None } status)
        {
            return null;
        }

        return status.Stage == DevelopmentStage.Commented
            ? string.Join(Texts.Get("Common_ListSeparator"), StageName(status.Stage), Texts.Count("Gantt_DevUnresolved", status.UnresolvedThreads))
            : StageName(status.Stage);
    }
}
