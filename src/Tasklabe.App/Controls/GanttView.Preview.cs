using Microsoft.UI.Xaml;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Editing;
using Tasklabe.Core.Gantt;
using Tasklabe.Core.Scheduling;
using Tasklabe.Core.Wbs;

namespace Tasklabe.App.Controls;

/// <summary>
/// 日程の案のプレビュー（要件 F-DEP-05）。動かす前と後の予定を並べて示し、確定するとまとめて保存する。
/// </summary>
public sealed partial class GanttView
{
    /// <summary>利用者が決めた予定（ItemId ごと）。null ならプレビュー中ではない。</summary>
    private Dictionary<string, PlanDates>? _pinned;

    /// <summary>このタスクの変更が及ぶ後続だけを動かす。null なら計画全体の食い違いを解消する。</summary>
    private IReadOnlyCollection<string>? _previewScope;

    private IReadOnlyList<ScheduleShift> _shifts = [];

    /// <summary>プレビューの予定を当てはめた木。プレビュー中でなければ null。</summary>
    private TaskTree? _previewTree;

    /// <summary>調整前の予定（ItemId ごと）。元の位置を淡く描くために使う。</summary>
    private Dictionary<string, (DateOnly Start, DateOnly End)> _originalPlans = [];

    /// <summary>プレビューで確定を待っている、まだ保存していない依存関係。</summary>
    private PendingLink? _pendingLink;

    public bool IsPreviewing => _pinned is not null;

    /// <summary>プレビューを始めた。</summary>
    public event EventHandler? PreviewStarted;

    /// <summary>プレビューを終えた（true なら確定、false なら取り消し）。</summary>
    public event EventHandler<bool>? PreviewEnded;

    /// <summary>
    /// 依存関係に合わせた日程の調整をプレビューする。動かすものがなければ何もしない。
    /// </summary>
    /// <param name="downstreamOf">このタスクの変更が及ぶ後続だけを動かす。null なら計画全体の食い違いを解消する。</param>
    /// <returns>プレビューに入ったか。</returns>
    public bool BeginPreview(IReadOnlyDictionary<string, PlanDates>? pinned = null, IReadOnlyCollection<string>? downstreamOf = null)
    {
        _pinned = new Dictionary<string, PlanDates>(pinned ?? new Dictionary<string, PlanDates>(), StringComparer.Ordinal);
        _previewScope = downstreamOf;
        _committed = null;
        RefreshPreview();
        if (_pinned.Count == 0 && _shifts.Count == 0)
        {
            _pinned = null;
            RefreshPreview();
            return false;
        }

        Rebuild();
        UpdatePreviewBar();
        RevealPreview();
        LayoutRows(rebind: true);
        RequestRender();
        Focus(FocusState.Programmatic);
        PreviewStarted?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// 動かした先が見えるようにする。表示する日付の範囲を広げ、最初に動かすバーが画面の左寄りに来るまでスクロールする。
    /// </summary>
    private void RevealPreview()
    {
        if (GanttModel.Extent(_rows) is { } extent && extent.End.DayNumber - _origin.DayNumber + 60 > _dayCount)
        {
            _dayCount = extent.End.DayNumber - _origin.DayNumber + 60;
        }

        // 後続の移動先を優先し、なければ利用者が動かしたバーを見せる
        var first = _shifts.Select(s => s.After.Start ?? s.After.Target).Where(d => d is not null).Min()
            ?? _pinned?.Values.Select(p => p.Start ?? p.Target).Where(d => d is not null).Min();
        if (first is { } date)
        {
            double x = (date.DayNumber - _origin.DayNumber) * _dayWidth;
            double width = Math.Max(Chart.ActualWidth, 1);
            if (x < _scrollX || x > _scrollX + width * 0.8)
            {
                _scrollX = Math.Max(x - width / 3, 0);
            }
        }

        UpdateScrollBars();
    }

    /// <summary>プレビューの予定（利用者の指定と、それに合わせた後続の移動）を求め直す。</summary>
    private void RefreshPreview()
    {
        if (_pinned is null)
        {
            _previewTree = null;
            _shifts = [];
            _originalPlans = [];
            return;
        }

        _shifts = DependencyScheduler.Resolve(_tree, Today, _pinned, _previewScope);
        var overrides = new Dictionary<string, PlanDates>(_pinned, StringComparer.Ordinal);
        foreach (var shift in _shifts)
        {
            overrides[shift.Task.ItemId] = shift.After;
        }

        _previewTree = TaskTree.Build(_tree.All().Select(n => overrides.TryGetValue(n.Task.ItemId, out var d)
            ? n.Task with { Start = d.Start, Target = d.Target }
            : n.Task));
        _originalPlans = GanttModel.Build(_tree.All(), Today)
            .Where(r => r.HasPlan)
            .ToDictionary(r => r.Task.ItemId, r => (r.PlanStart!.Value, r.PlanEnd!.Value), StringComparer.Ordinal);
    }

    private void UpdatePreviewBar()
    {
        PreviewBar.IsOpen = IsPreviewing;
        if (!IsPreviewing)
        {
            return;
        }

        int moved = _shifts.Count;
        int adjusted = _pinned!.Count;
        var cause = _shifts.FirstOrDefault()?.Cause;
        string reason = _shifts.Any(s => s.CauseDelayed) ? "先行タスクの遅れ" : "先行タスクの予定";
        if (_pendingLink is { } link && RealTask(link.Predecessor) is { } pred && RealTask(link.Successor) is { } succ)
        {
            PreviewBar.Message = $"「{pred.Title}」→「{succ.Title}」の依存関係を張ると、後続 {moved} 件を後ろへずらします。確定で依存関係と合わせて保存します。";
            return;
        }

        PreviewBar.Message = (moved, adjusted) switch
        {
            (0, _) => $"動かしたバー {adjusted} 件の予定を保存します。後続との食い違いはありません。",
            (_, 0) => $"{reason}に合わせて、後続 {moved} 件を後ろへずらします（「{cause!.Title}」の後）。バーをドラッグして調整できます。",
            _ => $"{reason}に合わせて、後続 {moved} 件を後ろへずらします（「{cause!.Title}」の後）。動かしたバー {adjusted} 件と合わせて保存します。",
        };
    }

    private async void OnConfirmPreview(object sender, RoutedEventArgs e)
    {
        if (_pinned is null)
        {
            return;
        }

        // 利用者が動かしたタスクと、それに合わせて動かした後続をまとめて保存する
        var targets = new Dictionary<string, PlanDates>(_pinned, StringComparer.Ordinal);
        foreach (var shift in _shifts)
        {
            targets[shift.Task.ItemId] = shift.After;
        }

        var edits = new List<(TaskItem, IReadOnlyList<TaskChange>)>();
        foreach (var (itemId, dates) in targets)
        {
            if (RealTask(itemId) is not { } task)
            {
                continue;
            }

            var changes = TaskRules.Set(task, TaskField.Start, TaskValues.Date(dates.Start))
                .Concat(TaskRules.Set(task, TaskField.Target, TaskValues.Date(dates.Target)))
                .ToList();
            edits.Add((task, changes));
        }

        // まだ保存していない依存関係も、同じ 1 回の操作に含める（元に戻すと依存関係と予定の両方が戻る）
        if (_pendingLink is { } link && RealTask(link.Successor) is { } linkedSucc)
        {
            var unlinked = linkedSucc with { BlockedBy = [.. linkedSucc.BlockedBy.Where(b => b != link.PredecessorIssueId)] };
            var blocked = TaskRules.Set(unlinked, TaskField.BlockedBy, TaskValues.IssueIds(linkedSucc.BlockedBy));
            int at = edits.FindIndex(e => e.Item1.ItemId == link.Successor);
            if (at >= 0)
            {
                edits[at] = (unlinked, [.. blocked, .. edits[at].Item2]);
            }
            else
            {
                edits.Insert(0, (unlinked, blocked));
            }
        }

        // 保存後の再表示までのあいだも、確定した位置を表示しておく
        var confirmedTree = _previewTree;
        EndPreview(confirmed: true);
        if (confirmedTree is not null)
        {
            _tree = confirmedTree;
            Rebuild();
            LayoutRows(rebind: true);
            RequestRender();
        }

        await App.Current.Services.Edits.ApplyManyAsync(edits);
    }

    private void OnCancelPreview(object sender, RoutedEventArgs e) => EndPreview(confirmed: false);

    /// <summary>プレビューを終える。取り消した場合は何も保存していないため、元の予定に戻る。</summary>
    public void EndPreview(bool confirmed)
    {
        if (_pinned is null)
        {
            return;
        }

        _pinned = null;
        _previewScope = null;

        // 依存関係を張ったプレビューを取り消したら、張る前に戻す（何も保存していない）
        if (_pendingLink is { } link && !confirmed)
        {
            _tree = WithoutLink(_tree, link);
        }

        _pendingLink = null;
        Rebuild();
        UpdatePreviewBar();
        LayoutRows(rebind: true);
        RequestRender();
        PreviewEnded?.Invoke(this, confirmed);
    }

    /// <summary>
    /// ドラッグやキーでの変更を予定の組にする。日付が片方しかないタスクは、
    /// 1 日のまま動かす限りその日付だけを動かし、もう一方の日付を書き足さない。
    /// </summary>
    private static PlanDates PlanDatesFor(TaskItem task, DateOnly start, DateOnly end)
    {
        bool singleDay = start == end;
        if (singleDay && task.Start is null && task.Target is not null)
        {
            return new PlanDates(null, start);
        }

        if (singleDay && task.Target is null && task.Start is not null)
        {
            return new PlanDates(start, null);
        }

        return new PlanDates(start, end);
    }

    // ================================================================ ホバーの詳細と読み上げ
}
