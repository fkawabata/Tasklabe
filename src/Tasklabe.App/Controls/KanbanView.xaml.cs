using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Tasklabe.App.Services;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Editing;
using Tasklabe.Core.Kanban;
using Tasklabe.Core.Wbs;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Text;

namespace Tasklabe.App.Controls;

/// <summary>
/// カンバンのカード（UI デザイン設計書 3.5 節）。子を持たないタスクだけをカードにする。
/// 1 行目に番号と所属、担当者、2 行目に状態とタイトル、3 行目に値のチップを並べる。状態・担当者・チップは押すとその場で変えられる。
/// </summary>
public sealed class KanbanCard
{
    private readonly DateOnly today;

    /// <param name="caption">番号の横に添える所属（プロジェクト名や親タスク名）。</param>
    /// <param name="isTeam">チームのプロジェクトのタスクか（担当者を持つのはチームだけ）。</param>
    /// <param name="byCategory">列がカテゴリか（マイタスク）。そのときは、カテゴリ名と違うステータス名をチップで示す。</param>
    public KanbanCard(TaskItem task, string caption, DateOnly today, bool isTeam, bool byCategory = false)
    {
        Task = task;
        Caption = caption;
        IsTeam = isTeam;
        this.today = today;
        HasStatusName = byCategory && task.StatusName is { } name
            && !string.Equals(name.Trim(), StatusVisuals.Name(task.Category), StringComparison.OrdinalIgnoreCase);
    }

    public TaskItem Task { get; }

    public string Title => Task.Title;

    public string Caption { get; }

    public bool IsTeam { get; }

    /// <summary>タスクの番号（例: TLB-123）。GitHub にまだ送っていないタスクは番号を持たない。</summary>
    public string NumberText => TaskKeys.Of(Task);

    public bool HasNumber => NumberText.Length > 0;

    public bool HasCaption => Caption.Length > 0;

    /// <summary>計画に入っていない課題か（要件 F-TSK-08）。</summary>
    public bool IsIssue => Task.Kind == TaskKind.Issue;

    /// <summary>完了・中止したタスクは目立たなくする。</summary>
    public bool IsMuted => Task.IsDone;

    /// <summary>中止したタスクは取り消し線で示す。</summary>
    public TextDecorations TitleDecorations => Task.IsCanceled ? TextDecorations.Strikethrough : TextDecorations.None;

    public StatusCategory IconCategory => Task.IsCanceled ? StatusCategory.Canceled
        : Task.IsCompleted ? StatusCategory.Done
        : Task.Category;

    public double Progress => Task.EffectiveProgress;

    public string StatusText => Task.StatusName ?? StatusVisuals.Name(Task.Category);

    // ---- 担当者（先頭の 1 人の頭文字。2 人以上は人数を添える）

    public bool HasAssignee => Task.Assignees.Count > 0;

    public bool IsUnassigned => IsTeam && !HasAssignee;

    public string AssigneeName => Task.Assignees.FirstOrDefault() ?? "";

    /// <summary>アバターの頭文字（ログイン名の先頭の 1 文字）。</summary>
    public string AvatarInitial => Avatar.Initial(AssigneeName);

    /// <summary>アバターの色。同じ人は、どのカードでも同じ色にする。</summary>
    public SolidColorBrush AvatarBrush => Avatar.Brush(AssigneeName);

    public string MoreAssigneesText => Task.Assignees.Count > 1 ? $"+{Task.Assignees.Count - 1}" : "";

    public bool HasMoreAssignees => Task.Assignees.Count > 1;

    public string AssigneeText => string.Join(", ", Task.Assignees.Select(a => "@" + a));

    public string AssigneeTip => Controls.KeyHints.Tip("task.assign", HasAssignee ? $"担当: {AssigneeText}" : "担当者なし");

    public string StatusTip => Controls.KeyHints.Tip("task.status", StatusText);

    // ---- 値のチップ（値のあるものだけを出す。ないものは右クリックのメニューから決める）

    public string TargetText => Task.Target is { } d ? DateText.Short(d, today) : "";

    public bool HasTarget => TargetText.Length > 0;

    public bool IsOverdue => Task.IsOverdueOn(today);

    /// <summary>期日超過は警告の印で示すため、暦の印は出さない。</summary>
    public bool ShowsCalendarIcon => !IsOverdue;

    public string EstimateText => EffortText.Of(Task.EstimateHours);

    public bool HasEstimate => Task.EstimateHours is not null;

    /// <summary>プロジェクトでのステータス名を示すか（カテゴリの列で、名前がカテゴリ名と違うとき）。</summary>
    public bool HasStatusName { get; }

    public bool HasChips => HasStatusName || IsIssue || HasTarget || HasEstimate;

    public string StatusButtonName => $"ステータス: {StatusText}。押すと変更できます";

    public string AssigneeButtonName => $"担当者: {(HasAssignee ? AssigneeText : "なし")}。押すと変更できます";

    public string DueButtonName => $"期日: {TargetText}{(IsOverdue ? "（期日超過）" : "")}。押すと変更できます";

    public string EstimateButtonName => $"想定工数: {EstimateText}。押すと変更できます";

    public string AutomationName => $"{NumberText} {Title}、{StatusText}、進捗 {Progress:0}%".TrimStart()
        + (TargetText.Length > 0 ? $"、期日 {TargetText}" + (IsOverdue ? "（期日超過）" : "") : "")
        + (HasAssignee ? $"、担当 {AssigneeText}" : "")
        + (IsIssue ? "、課題" : "");
}

/// <summary>
/// カンバン（要件 F-UI-KB-01〜05）。ステータスを列とし、カードのドラッグ＆ドロップまたはコンテキストメニューでステータスを変更する。
/// 並び順・区切り（スイムレーン）・完了の表示は「表示」から選び、ビューごとに覚える。
/// </summary>
public sealed partial class KanbanView : UserControl
{
    private const string DragFormat = "Tasklabe.ItemId";
    private const double ColumnWidth = 280;

    /// <summary>1 回の読み込みで決まる、カンバンの中身と振る舞い。</summary>
    private sealed record Source(
        string ViewKey,
        IReadOnlyList<TaskItem> Tasks,
        IReadOnlyList<KanbanColumn> Columns,
        Func<TaskItem, string?> ColumnKeyOf,
        Func<Project, KanbanColumn, StatusOption?> StatusIn,
        IReadOnlyList<(KanbanGrouping Grouping, string Label)> Groupings,
        Func<KanbanGrouping, Func<TaskItem, KanbanLaneKey>?> LaneOf,
        Func<TaskItem, KanbanGrouping, string> CaptionOf);

    private static readonly (KanbanCompleted Value, string Label)[] CompletedChoices =
    [
        (KanbanCompleted.All, "すべて"),
        (KanbanCompleted.PastWeek, "直近 1 週間"),
        (KanbanCompleted.PastMonth, "直近 1 か月"),
        (KanbanCompleted.None, "表示しない"),
    ];

    private Source? _source;
    private KanbanDisplay _display = KanbanDisplay.Default;
    private Dictionary<string, TaskItem> _tasks = [];
    private Dictionary<string, Project> _projects = [];
    private readonly List<ListView> _lists = [];
    private KanbanBoard? _board;
    private readonly HashSet<string> _collapsedLanes = [];
    private string? _dragLane;

    public KanbanView()
    {
        InitializeComponent();
    }

    // ---------------------------------------------------------------- 読み込み

    /// <summary>プロジェクトのカンバン。列はプロジェクトのステータスの選択肢とする。</summary>
    /// <param name="issues">計画に入っていない課題。計画のタスクと同じ列に並べる。</param>
    internal void Load(Project project, TaskTree tree, Func<TaskNode, bool>? isVisible = null, IEnumerable<TaskItem>? issues = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(tree);

        var leaves = tree.All()
            .Where(n => !n.HasChildren && n.Task.Kind == TaskKind.Task && (isVisible?.Invoke(n) ?? true))
            .ToList();
        var parents = leaves.ToDictionary(n => n.Task.ItemId, n => n.Parent, StringComparer.Ordinal);
        var order = tree.All().Select((n, i) => (n.Task.ItemId, i)).ToDictionary(p => p.ItemId, p => p.i, StringComparer.Ordinal);
        List<TaskItem> tasks = [.. leaves.Select(n => n.Task), .. issues ?? []];
        _projects = new Dictionary<string, Project>(StringComparer.Ordinal) { [project.Id] = project };

        List<(KanbanGrouping, string)> groupings = [(KanbanGrouping.None, "なし")];
        if (project.IsTeam)
        {
            groupings.Add((KanbanGrouping.Assignee, "担当者"));
        }

        groupings.Add((KanbanGrouping.Parent, "親タスク"));
        if (project.IsTeam)
        {
            groupings.Add((KanbanGrouping.Kind, "区分"));
        }

        string ParentTitle(TaskItem t) => parents.GetValueOrDefault(t.ItemId)?.Task.Title ?? "";

        Func<TaskItem, KanbanLaneKey>? LaneOf(KanbanGrouping grouping) => grouping switch
        {
            KanbanGrouping.Assignee => t => AssigneeLane(t),
            KanbanGrouping.Parent => t => parents.GetValueOrDefault(t.ItemId) is { } parent
                ? new KanbanLaneKey(parent.Task.ItemId, parent.Task.Title, order.GetValueOrDefault(parent.Task.ItemId))
                : new KanbanLaneKey("", "親タスクなし", double.MaxValue),
            KanbanGrouping.Kind => t => t.Kind == TaskKind.Issue
                ? new KanbanLaneKey("issue", KindVisuals.Name(TaskKind.Issue), 1)
                : new KanbanLaneKey("plan", KindVisuals.Name(TaskKind.Task), 0),
            _ => null,
        };

        Show(new Source(
            "project:" + project.Id,
            tasks,
            KanbanLayout.ColumnsOf(project),
            t => t.StatusOptionId,
            (p, column) => p.StatusOptions.FirstOrDefault(o => o.Id == column.Key),
            groupings,
            LaneOf,
            (t, grouping) => grouping == KanbanGrouping.Parent ? "" : ParentTitle(t)));
    }

    /// <summary>
    /// マイタスクのカンバン。プロジェクトをまたぐため、ステータスのカテゴリごとに列を置く（要件 F-STS-05）。
    /// 落としたタスクは、そのタスクのプロジェクトでそのカテゴリの代表のステータス（なければ近いカテゴリの代表）へ移す。
    /// </summary>
    /// <param name="parentTitle">計画の中のタスクに添える親タスク名（なければ null）。</param>
    internal void LoadForMyTasks(IEnumerable<TaskItem> tasks, IReadOnlyDictionary<string, Project> projects,
        Func<TaskItem, string?> parentTitle)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(parentTitle);

        var list = tasks.ToList();
        _projects = projects.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        var columns = KanbanLayout.CategoryColumns(StatusVisuals.Name);

        string ProjectName(TaskItem t) => _projects.GetValueOrDefault(t.ProjectId) is { } p ? ProjectDisplay.Name(p) : "";

        Func<TaskItem, KanbanLaneKey>? LaneOf(KanbanGrouping grouping) => grouping == KanbanGrouping.Project
            ? t => _projects.GetValueOrDefault(t.ProjectId) is { } p
                ? new KanbanLaneKey(p.Id, ProjectDisplay.Name(p), ProjectRank(p))
                : new KanbanLaneKey("", "", double.MaxValue)
            : null;

        string Caption(TaskItem t, KanbanGrouping grouping)
        {
            var parent = parentTitle(t);
            if (grouping == KanbanGrouping.Project)
            {
                return parent ?? "";
            }

            return parent is { Length: > 0 } ? $"{ProjectName(t)} / {parent}" : ProjectName(t);
        }

        Show(new Source(
            "mytasks",
            list,
            columns,
            t => KanbanLayout.CategoryKey(t.Category),
            (p, column) => column.Category is { } category ? ProjectConventions.DefaultStatus(p.StatusOptions, category) : null,
            [(KanbanGrouping.None, "なし"), (KanbanGrouping.Project, "プロジェクト")],
            LaneOf,
            Caption));
    }

    /// <summary>マイタスクでのプロジェクトの並び（ナビゲーションと同じく、マイタスク → 個人 → チーム）。</summary>
    /// <remarks>同じ種類の中は、ナビゲーションで並べ替えた順とする（要件 F-UI-KB-06）。</remarks>
    private static double ProjectRank(Project project)
    {
        double kind = project.Kind switch
        {
            ProjectKind.Inbox => 0,
            ProjectKind.Personal => 1,
            _ => 2,
        };
        var nav = App.Current.Shell?.Projects.Select(p => p.Id).ToList() ?? [];
        int index = nav.IndexOf(project.Id);
        return kind + (index < 0 ? 0.999 : index / (nav.Count + 1.0));
    }

    private static KanbanLaneKey AssigneeLane(TaskItem task)
    {
        var login = App.Current.Services.CurrentSettings.UserLogin;
        var assignees = task.Assignees.Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (assignees.Count == 0)
        {
            return new KanbanLaneKey("", "担当者なし", double.MaxValue);
        }

        // 自分の担当を先頭に置く
        bool mine = assignees.Count == 1 && string.Equals(assignees[0], login, StringComparison.OrdinalIgnoreCase);
        var key = string.Join(',', assignees).ToUpperInvariant();
        return new KanbanLaneKey(key, string.Join(", ", assignees.Select(a => "@" + a)), mine ? 0 : 1);
    }

    private void Show(Source source)
    {
        if (_source?.ViewKey != source.ViewKey)
        {
            // 区切りの折りたたみは、ビューごとに覚える（UX 規約 UX-23）
            _collapsedLanes.Clear();
            _collapsedLanes.UnionWith(ViewState.GetSet("kanban-lanes:" + source.ViewKey));
        }

        _source = source;
        _display = LoadDisplay(source.ViewKey, source);
        _tasks = source.Tasks.GroupBy(t => t.ItemId).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        Render();
    }

    // ---------------------------------------------------------------- 表示の設定

    private static KanbanDisplay LoadDisplay(string viewKey, Source source)
    {
        var saved = App.Current.Services.CurrentSettings.KanbanDisplays.GetValueOrDefault(viewKey);
        var display = KanbanDisplay.Parse(saved);

        // このビューで使えない区切りは外す（例: 個人のプロジェクトで担当者）
        return source.Groupings.Any(g => g.Grouping == display.Grouping) ? display : display with { Grouping = KanbanGrouping.None };
    }

    private void ChangeDisplay(KanbanDisplay display)
    {
        if (_source is null || display == _display)
        {
            return;
        }

        _display = display;
        var settings = App.Current.Services.CurrentSettings;
        if (display == KanbanDisplay.Default)
        {
            settings.KanbanDisplays.Remove(_source.ViewKey);
        }
        else
        {
            settings.KanbanDisplays[_source.ViewKey] = display.ToString();
        }

        App.Current.Services.SaveSettings();

        var focused = CaptureFocus();
        Render();
        RestoreFocus(focused);
    }

    // ---------------------------------------------------------------- 並び順とグループ（UI デザイン設計書 4.4 節）

    /// <summary>並び順の候補。プロジェクトは計画の順を、マイタスクは期日の順を既定とする。</summary>
    private IReadOnlyList<ViewOption<TaskOrdering>> OrderingChoices => ViewOptions.Orderings(hasPlanOrder: _source?.ViewKey != "mytasks");

    /// <summary>グループの候補（このビューで使えるもの）。</summary>
    private IReadOnlyList<ViewOption<KanbanGrouping>> GroupingChoices =>
        [.. (_source?.Groupings ?? []).Select(g => new ViewOption<KanbanGrouping>(g.Grouping, g.Label, null, ViewOptions.GlyphOf(g.Grouping)))];

    /// <summary>いまの並び順。</summary>
    public (string Label, string Glyph) OrderingValue =>
        OrderingChoices.FirstOrDefault(o => o.Value == _display.Ordering) is { } o ? (o.Label, o.Glyph) : ("", "");

    /// <summary>いまのグループ。</summary>
    public (string Label, string Glyph) GroupingValue =>
        GroupingChoices.FirstOrDefault(g => g.Value == _display.Grouping) is { } g ? (g.Label, g.Glyph) : ("なし", ViewOptions.GlyphOf(KanbanGrouping.None));

    /// <summary>並び順をピッカーで選ぶ（Y キーまたはボタン）。</summary>
    public async Task PickOrderingAsync(FrameworkElement anchor)
    {
        if (await ViewOptions.PickAsync(anchor, "並び順", OrderingChoices, _display.Ordering) is { } picked)
        {
            ChangeDisplay(_display with { Ordering = picked.Value });
        }
    }

    /// <summary>グループをピッカーで選ぶ（G キーまたはボタン）。</summary>
    public async Task PickGroupingAsync(FrameworkElement anchor)
    {
        if (await ViewOptions.PickAsync(anchor, "グループ", GroupingChoices, _display.Grouping) is { } picked)
        {
            ChangeDisplay(_display with { Grouping = picked.Value });
        }
    }

    // ---------------------------------------------------------------- 絞り込み（UX 規約 UX-24）

    /// <summary>表示の設定。</summary>
    public KanbanDisplay Display => _display;

    /// <summary>表示の設定を変える（絞り込みのチップやピッカーから）。</summary>
    public void SetDisplay(KanbanDisplay display) => ChangeDisplay(display);

    /// <summary>表示の設定か、表示している件数が変わった。</summary>
    public event EventHandler? DisplayChanged;

    /// <summary>表示しているカードの数と、すべての数。</summary>
    public (int Shown, int Total) Counts { get; private set; }

    /// <summary>完了・中止のカードの表示範囲の選択肢。</summary>
    public static IReadOnlyList<(KanbanCompleted Value, string Label)> CompletedOptions => CompletedChoices;

    // ---------------------------------------------------------------- 描画

    private void Render()
    {
        if (_source is not { } source)
        {
            return;
        }

        var today = AppClock.Today;
        var laneOf = source.LaneOf(_display.Grouping);
        // 空の列を出すかは、ビューごとではなくアプリの設定で決める
        var display = _display with { ShowEmptyColumns = App.Current.Services.CurrentSettings.KanbanShowEmptyColumns };
        var board = KanbanLayout.Build(source.Tasks, source.Columns, source.ColumnKeyOf, display, today, laneOf);

        _lists.Clear();
        _listInfo.Clear();
        _board = board;
        Counts = (board.Lanes.Sum(l => l.Count), source.Tasks.Count);
        Bar.Update(0);
        BoardHost.Child = laneOf is null ? BuildColumns(board, source, today) : BuildLanes(board, source, today);
        DisplayChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>区切らない表示。列ごとに縦へスクロールする。</summary>
    private FrameworkElement BuildColumns(KanbanBoard board, Source source, DateOnly today)
    {
        var columns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = AppResources.Get<double>("Space.M") };
        var lane = board.Lanes.FirstOrDefault();
        for (int i = 0; i < board.Columns.Count; i++)
        {
            var column = board.Columns[i];
            var cards = lane?.Cells[i] ?? [];
            var panel = ColumnPanel();
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            panel.Children.Add(ColumnHeader(column, cards, source));
            var list = CreateList(column, i, source, KanbanLaneKey.All.Key, [.. cards.Select(t => Card(t, source, today))]);
            ScrollViewer.SetVerticalScrollMode(list, ScrollMode.Disabled);
            Grid.SetRow(list, 1);
            panel.Children.Add(list);
            columns.Children.Add(panel);
        }

        return HorizontalScroller(columns);
    }

    /// <summary>区切る表示。列の見出しを上に並べ、区切りごとに横一列のセルを置いて、全体を縦へスクロールする。</summary>
    private FrameworkElement BuildLanes(KanbanBoard board, Source source, DateOnly today)
    {
        var spacing = AppResources.Get<double>("Space.M");
        // 列の見出しは、区切らない表示の列と同じ背景・余白・角で包み、どちらの表示でも同じ見え方にする
        var headers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        for (int i = 0; i < board.Columns.Count; i++)
        {
            var header = ColumnPanel();
            header.Padding = new Thickness(8, 12, 8, 0);
            header.Children.Add(ColumnHeader(board.Columns[i], board.CellsOf(i), source));
            headers.Children.Add(header);
        }

        var lanes = new StackPanel { Spacing = spacing };
        foreach (var lane in board.Lanes)
        {
            var laneId = source.ViewKey + "|" + lane.Key.Key;
            var cells = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
            for (int i = 0; i < board.Columns.Count; i++)
            {
                var panel = ColumnPanel();
                panel.MinHeight = 56;
                var list = CreateList(board.Columns[i], i, source, lane.Key.Key, [.. lane.Cells[i].Select(t => Card(t, source, today))]);
                ScrollViewer.SetVerticalScrollMode(list, ScrollMode.Disabled);
                ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
                panel.Children.Add(list);
                cells.Children.Add(panel);
            }

            bool collapsed = _collapsedLanes.Contains(laneId);
            cells.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            var chevron = new FontIcon { FontSize = 12, Glyph = collapsed ? "" : "" };
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            title.Children.Add(chevron);
            title.Children.Add(new TextBlock { Text = lane.Key.Title, Style = AppResources.Style("Text.BodyStrong") });
            title.Children.Add(new TextBlock { Text = $"{lane.Count} 件", Style = AppResources.Style("Text.Caption"), VerticalAlignment = VerticalAlignment.Center });
            var toggle = new Button
            {
                Content = title,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 2, 8, 2),
            };
            AutomationProperties.SetName(toggle, $"{lane.Key.Title}（{lane.Count} 件）を{(collapsed ? "展開" : "折りたたむ")}");
            toggle.Click += (_, _) =>
            {
                bool nowCollapsed = cells.Visibility == Visibility.Visible;
                cells.Visibility = nowCollapsed ? Visibility.Collapsed : Visibility.Visible;
                chevron.Glyph = nowCollapsed ? "" : "";
                if (nowCollapsed)
                {
                    _collapsedLanes.Add(laneId);
                }
                else
                {
                    _collapsedLanes.Remove(laneId);
                }

                ViewState.SetSet("kanban-lanes:" + source.ViewKey, _collapsedLanes);
            };

            var section = new StackPanel { Spacing = 6 };
            section.Children.Add(toggle);
            section.Children.Add(cells);
            lanes.Children.Add(section);
        }

        if (board.Lanes.Count == 0)
        {
            lanes.Children.Add(new TextBlock
            {
                Text = "表示するカードはありません",
                Style = AppResources.Style("Text.Caption"),
                Margin = new Thickness(4, 8, 0, 0),
            });
        }

        var body = new Grid { RowSpacing = 4 };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(headers);
        var vertical = new ScrollViewer
        {
            Content = lanes,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 0, 8),
        };
        Grid.SetRow(vertical, 1);
        body.Children.Add(vertical);
        return HorizontalScroller(body, vertical);
    }

    /// <summary>
    /// 列を横に並べる入れ物。ホイールは縦に、Shift + ホイールとタッチパッドの横の操作は横に動かす（ガントチャートと同じ）。
    /// ScrollViewer はホイールを入力のイベントより下で受けて動かし、処理済みにしても止められないため、
    /// 中の ScrollViewer はどれも手で動かせないようにして（スクロールバーは使える）、ホイールはすべてここで動かす。
    /// </summary>
    /// <param name="vertical">全体を縦に動かす入れ物（区切る表示）。列の見出しの上でホイールを回したときにも動かす。</param>
    private static ScrollViewer HorizontalScroller(UIElement content, ScrollViewer? vertical = null)
    {
        var scroller = new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
        };
        scroller.AddHandler(PointerWheelChangedEvent, new PointerEventHandler((_, e) =>
        {
            var properties = e.GetCurrentPoint(scroller).Properties;
            int delta = properties.MouseWheelDelta;
            if (properties.IsHorizontalMouseWheel)
            {
                scroller.ChangeView(scroller.HorizontalOffset + delta, null, null);
            }
            else if (e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift))
            {
                scroller.ChangeView(scroller.HorizontalOffset - delta, null, null);
            }
            else if ((VerticalScrollerAt(e.OriginalSource as DependencyObject, scroller) ?? vertical) is { } target)
            {
                // ポインターの下で縦に動かせる入れ物（区切らない表示では列、区切る表示では全体）を動かす
                target.ChangeView(null, target.VerticalOffset - delta, null);
            }

            e.Handled = true;
        }), true);
        return scroller;
    }

    /// <summary>要素から外側（<paramref name="outer"/> の手前）へたどり、縦に動かせる最初の ScrollViewer を返す。</summary>
    private static ScrollViewer? VerticalScrollerAt(DependencyObject? element, ScrollViewer outer)
    {
        for (var node = element; node is not null && node != outer; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollViewer { ScrollableHeight: > 0 } viewer)
            {
                return viewer;
            }
        }

        return null;
    }

    private static Grid ColumnPanel() => new()
    {
        Width = ColumnWidth,
        Padding = new Thickness(8, 12, 8, 8),
        Background = ThemeResources.Brush("LayerOnMicaBaseAltFillColorSecondaryBrush"),
        CornerRadius = AppResources.CornerRadius("Radius.Card"),
    };

    private KanbanCard Card(TaskItem task, Source source, DateOnly today) =>
        new(task, source.CaptionOf(task, _display.Grouping), today, _projects.GetValueOrDefault(task.ProjectId)?.IsTeam == true,
            byCategory: source.ViewKey == "mytasks");

    /// <summary>列の見出し。状態のアイコン、名前、件数と工数、その列のステータスでタスクを追加する「+」を並べる。</summary>
    private static Grid ColumnHeader(KanbanColumn column, IReadOnlyList<TaskItem> cards, Source source)
    {
        var caption = AppResources.Style("Text.Caption");
        var hours = cards.Where(t => !t.IsCanceled).Sum(t => t.EstimateHours ?? 0);

        var header = new Grid { ColumnSpacing = 8, Margin = new Thickness(4, 0, 0, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (column.Key != KanbanLayout.UnsetKey)
        {
            var add = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 12 },
                Style = AppResources.Style("SubtleButtonStyle"),
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(add, $"「{column.Name}」にタスクを追加");
            AutomationProperties.SetName(add, $"「{column.Name}」にタスクを追加");
            add.Click += async (_, _) => await App.Current.MainWindow!.AddTaskAsync(new NewTaskContext { Status = p => source.StatusIn(p, column) });
            Grid.SetColumn(add, 3);
            header.Children.Add(add);
        }
        header.Children.Add(new FontIcon
        {
            Glyph = column.Category is { } c ? StatusVisuals.Glyph(c) : "",
            FontSize = 14,
            Foreground = ThemeResources.Brush(StatusVisuals.BrushKey(column.Category)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var heading = new TextBlock
        {
            Text = column.Name,
            Style = AppResources.Style("Text.BodyStrong"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(heading, 1);
        header.Children.Add(heading);
        var summary = new TextBlock
        {
            Text = hours > 0 ? $"{cards.Count} 件 · {EffortText.Of(hours)}" : $"{cards.Count} 件",
            Style = caption,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(summary, 2);
        header.Children.Add(summary);
        return header;
    }

    /// <summary>一覧ごとの列と区切り（キーで隣の列へ移すときに使う）。</summary>
    private readonly Dictionary<ListView, (int Column, string Lane)> _listInfo = [];

    /// <summary>作り直している最中（選択の変化を、利用者の操作として扱わない）。</summary>
    private bool _rendering;

    /// <param name="index">列の位置。</param>
    /// <param name="laneKey">この一覧が属する区切り。カードは同じ区切りの中でだけ動かせる。</param>
    private ListView CreateList(KanbanColumn column, int index, Source source, string laneKey, List<KanbanCard> cards)
    {
        bool droppable = column.Key != KanbanLayout.UnsetKey;
        var list = new ListView
        {
            ItemsSource = cards,
            ItemTemplate = (DataTemplate)Resources["CardTemplate"],
            SelectionMode = ListViewSelectionMode.Extended,
            CanDragItems = true,
            AllowDrop = droppable,
            Padding = new Thickness(0),
            ItemContainerStyle = CardContainerStyle(),
        };
        AutomationProperties.SetName(list, column.Name);
        _listInfo[list] = (index, laneKey);

        // カードを押すと選ぶだけにし、ダブルクリックで詳細を開く（計画の表・ガントと同じ。Enter は OnListPreviewKeyDown）
        list.DoubleTapped += (_, e) =>
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is KanbanCard card)
            {
                App.Current.Shell?.SelectTask(card.Task);
            }
        };
        list.SelectionChanged += (_, e) => OnListSelectionChanged(list, e);
        list.PreviewKeyDown += OnListPreviewKeyDown;
        list.DragItemsStarting += (_, e) =>
        {
            if (e.Items.FirstOrDefault() is KanbanCard card)
            {
                _dragLane = laneKey;
                e.Data.SetData(DragFormat, card.Task.ItemId);
                e.Data.RequestedOperation = DataPackageOperation.Move;
            }
        };
        list.DragItemsCompleted += (_, _) => _dragLane = null;
        list.DragOver += (_, e) => e.AcceptedOperation = droppable && _dragLane == laneKey
            ? DataPackageOperation.Move
            : DataPackageOperation.None;
        list.Drop += async (_, e) =>
        {
            if (droppable && _dragLane == laneKey && e.DataView.Contains(DragFormat)
                && await e.DataView.GetDataAsync(DragFormat) is string itemId
                && _tasks.TryGetValue(itemId, out var task))
            {
                await MoveToColumnAsync([task], column);
            }
        };
        list.ContextRequested += OnCardContextRequested;

        _lists.Add(list);
        return list;
    }

    // ---------------------------------------------------------------- 選択（UX 規約 UX-06、UX-07）

    /// <summary>選んでいるカードのタスク（列をまたいで選べる）。</summary>
    public IReadOnlyList<TaskItem> SelectedTasks =>
        [.. _lists.SelectMany(l => l.SelectedItems.OfType<KanbanCard>()).Select(c => c.Task)];

    private void OnListSelectionChanged(ListView list, SelectionChangedEventArgs e)
    {
        // 修飾キーなしで選んだときは、ほかの列の選択を外す（列をまたぐ選択は Ctrl か Shift で足す）
        if (!_rendering && e.AddedItems.Count > 0 && !KeyInput.IsDown(VirtualKey.Control) && !KeyInput.IsDown(VirtualKey.Shift))
        {
            _rendering = true;
            try
            {
                foreach (var other in _lists.Where(l => !ReferenceEquals(l, list) && l.SelectedItems.Count > 0))
                {
                    other.SelectedItems.Clear();
                }
            }
            finally
            {
                _rendering = false;
            }
        }

        int count = _lists.Sum(l => l.SelectedItems.Count);
        Bar.Update(count);
        if (_rendering)
        {
            return;
        }

        if (count > 1 && App.Current.Shell is { IsDetailOpen: true, IsCreating: false } shell)
        {
            shell.SelectTask(null);
        }
        else if (count == 1 && e.AddedItems.FirstOrDefault() is KanbanCard card && App.Current.Shell is { IsDetailOpen: true, IsCreating: false } open)
        {
            // 詳細パネルを開いたまま別のカードを選ぶと、パネルの内容を切り替える（UX-10）
            open.SelectTask(card.Task);
        }
    }

    private async void OnListPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not ListView list)
        {
            return;
        }

        // 複数の選択を解き、フォーカスのある 1 件だけにする（UX-07、UX-14）
        if (e.Key == VirtualKey.Escape && SelectedTasks.Count > 1)
        {
            e.Handled = true;
            RestoreFocus(CaptureFocus());
            return;
        }

        // Enter でフォーカスのあるカードの詳細を開く
        if (e.Key == VirtualKey.Enter && !KeyInput.IsDown(VirtualKey.Control) && !KeyInput.IsDown(VirtualKey.Shift)
            && FocusManager.GetFocusedElement(list.XamlRoot) is ListViewItem { Content: KanbanCard focusedCard })
        {
            e.Handled = true;
            App.Current.Shell?.SelectTask(focusedCard.Task);
            return;
        }

        // ドラッグの代わりに、キーで隣の列へ移す（UX-21）
        if (KeyInput.From(e.Key) is { } gesture
            && App.Current.Services.Keymap.Find(gesture, Tasklabe.Core.Keyboard.ShortcutScope.Kanban) is { } action
            && _board is { } board && _listInfo.TryGetValue(list, out var info))
        {
            e.Handled = true;
            int step = action.Id == "kanban.moveNext" ? 1 : -1;
            int next = info.Column + step;
            while (next >= 0 && next < board.Columns.Count && board.Columns[next].Key == KanbanLayout.UnsetKey)
            {
                next += step;
            }

            if (next < 0 || next >= board.Columns.Count)
            {
                return;
            }

            var focused = CaptureFocus();
            var tasks = SelectedTasks.Count > 0 ? SelectedTasks : list.SelectedItem is KanbanCard c ? [c.Task] : [];
            _pendingFocus = focused;
            await MoveToColumnAsync(tasks, board.Columns[next]);
        }
    }

    /// <summary>キーで移したカードに、作り直した後もフォーカスを戻す。</summary>
    private string? _pendingFocus;

    /// <summary>カードを別の列へ移す（その列のステータスにする）。複数なら 1 回の操作として元に戻せる。</summary>
    private async Task MoveToColumnAsync(IReadOnlyList<TaskItem> tasks, KanbanColumn column)
    {
        if (_source is not { } source)
        {
            return;
        }

        // 子を持つタスク（親タスク）のステータスは子から決まるため、列を移しても変えない
        var own = await TaskShortcuts.WithoutParentsAsync(tasks);
        TaskShortcuts.ReportParents(tasks.Count - own.Count, own.Count == 0);
        tasks = own;

        var today = AppClock.Today;
        var targets = tasks
            .Where(t => _projects.ContainsKey(t.ProjectId))
            .Select(t => (Task: t, Option: source.StatusIn(_projects[t.ProjectId], column)))
            .ToList();
        var edits = targets
            .Select(x => (x.Task, x.Option is { } option && option.Id != x.Task.StatusOptionId
                ? TaskRules.ChangeStatus(x.Task, option, today)
                : (IReadOnlyList<TaskChange>)[]))
            .ToList();

        // そのカテゴリのステータスがないプロジェクトでは、近いカテゴリに入れたことを知らせる
        var substituted = column.Category is { } category
            ? targets.Select(x => x.Option?.Category).OfType<StatusCategory>().Where(c => c != category).Distinct().ToList()
            : [];
        var note = substituted.Count > 0
            ? $"（{column.Name} のないプロジェクトは {string.Join("・", substituted.Select(StatusVisuals.Name))}）"
            : "";
        if (edits.All(e => e.Item2.Count == 0))
        {
            if (note.Length > 0)
            {
                App.Current.Shell?.ShowToast($"{column.Name} のステータスがないため、移しませんでした");
            }

            return;
        }

        await TaskCommands.ApplyAsync(edits, edits.Count > 1 || note.Length > 0 ? $"{edits.Count} 件を「{column.Name}」へ移しました{note}" : null);
    }

    /// <summary>指定したタスクのカードの、同じ列の上下のカードを選ぶ（詳細パネルの ↑↓。UX-10）。</summary>
    internal TaskItem? Step(string itemId, int delta)
    {
        foreach (var list in _lists)
        {
            var cards = list.Items.OfType<KanbanCard>().ToList();
            int index = cards.FindIndex(c => c.Task.ItemId == itemId);
            if (index < 0)
            {
                continue;
            }

            if (index + delta < 0 || index + delta >= cards.Count)
            {
                return null;
            }

            var next = cards[index + delta];
            list.SelectedItem = next;
            list.ScrollIntoView(next);
            return next.Task;
        }

        return null;
    }

    // ---------------------------------------------------------------- キーボードのフォーカス（UX 規約 UX-15）

    private int _focusedColumn = -1;
    private int _focusedIndex = -1;

    /// <summary>作り直す前に、キーボードのフォーカスがあったカードのタスクを覚える。</summary>
    public string? CaptureFocus()
    {
        if (_pendingFocus is { } pending)
        {
            return pending;
        }

        foreach (var e in VisualTree.FocusedAncestors(XamlRoot))
        {
            if (e is ListViewItem { Content: KanbanCard card })
            {
                // その一覧の中の位置も覚えておく（カードが消えたときに隣のカードへ移すため）
                var list = _lists.FirstOrDefault(l => l.Items.Contains(card));
                _focusedColumn = list is null ? -1 : _lists.IndexOf(list);
                _focusedIndex = list?.Items.IndexOf(card) ?? -1;
                return card.Task.ItemId;
            }
        }

        return null;
    }

    /// <summary>
    /// 作り直したカンバンで、同じタスクのカードにフォーカスを戻す。削除などでカードがなくなったときは、同じ一覧の同じ位置のカードへ移す。
    /// </summary>
    public void RestoreFocus(string? itemId)
    {
        _pendingFocus = null;
        if (itemId is null)
        {
            return;
        }

        foreach (var list in _lists)
        {
            if (list.Items.OfType<KanbanCard>().FirstOrDefault(c => c.Task.ItemId == itemId) is { } card)
            {
                FocusCard(list, card);
                return;
            }
        }

        if (_focusedColumn >= 0 && _focusedColumn < _lists.Count && _lists[_focusedColumn].Items.OfType<KanbanCard>().ToList() is { Count: > 0 } cards)
        {
            FocusCard(_lists[_focusedColumn], cards[Math.Min(Math.Max(_focusedIndex, 0), cards.Count - 1)]);
        }
    }

    /// <summary>カンバンへフォーカスを移す（選んでいるカード、なければ最初のカード）。</summary>
    public void FocusContent()
    {
        foreach (var list in _lists)
        {
            if (list.SelectedItem is KanbanCard selected)
            {
                FocusCard(list, selected);
                return;
            }
        }

        foreach (var list in _lists)
        {
            if (list.Items.OfType<KanbanCard>().FirstOrDefault() is { } card)
            {
                FocusCard(list, card);
                return;
            }
        }
    }

    private void FocusCard(ListView list, KanbanCard card)
    {
        _rendering = true;
        try
        {
            foreach (var other in _lists.Where(l => !ReferenceEquals(l, list)))
            {
                other.SelectedItems.Clear();
            }

            list.SelectedItems.Clear();
            list.SelectedItem = card;
        }
        finally
        {
            _rendering = false;
        }

        Bar.Update(1);
        list.UpdateLayout();
        if (list.ContainerFromItem(card) is ListViewItem container)
        {
            container.StartBringIntoView();
            container.Focus(FocusState.Programmatic);
        }
    }

    // ---------------------------------------------------------------- カードの操作

    /// <summary>カードを対象にした操作の対象。選択に含まれていれば、選んでいるすべてを対象にする。</summary>
    private TaskTarget TargetFor(KanbanCard card, FrameworkElement anchor, Windows.Foundation.Point? position = null)
    {
        var selected = SelectedTasks;
        if (!selected.Any(t => t.ItemId == card.Task.ItemId))
        {
            if (_lists.FirstOrDefault(l => l.Items.Contains(card)) is { } list)
            {
                FocusCard(list, card);
            }

            selected = [card.Task];
        }

        return new TaskTarget(card.Task, anchor, position, Selection: selected);
    }

    /// <summary>一覧・計画の表と同じメニュー（UX 規約 UX-20）。ドラッグを使わなくても、ステータスをここから変えられる。</summary>
    private async void OnCardContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not KanbanCard card)
        {
            return;
        }

        e.Handled = true;
        if (e.TryGetPosition(sender, out var position))
        {
            await TaskMenu.ShowAsync(TargetFor(card, (FrameworkElement)sender, position), sender, position);
        }
        else
        {
            var anchor = (FrameworkElement)e.OriginalSource;
            await TaskMenu.ShowAsync(TargetFor(card, anchor), anchor, null);
        }
    }

    // カードの中の状態・担当者・チップは、押すとキーボードの操作と同じピッカーをその場に開く（UX 規約 UX-19）

    private void OnCardStatusClick(object sender, RoutedEventArgs e) => RunOnCard(sender, "task.status");

    private void OnCardAssigneeClick(object sender, RoutedEventArgs e) => RunOnCard(sender, "task.assign");

    private void OnCardDueClick(object sender, RoutedEventArgs e) => RunOnCard(sender, "task.due");

    private void OnCardEstimateClick(object sender, RoutedEventArgs e) => RunOnCard(sender, "task.estimate");

    private void OnCardIssueClick(object sender, RoutedEventArgs e) => RunOnCard(sender, "task.plan");

    private async void RunOnCard(object sender, string actionId)
    {
        if (sender is FrameworkElement { Tag: KanbanCard card } anchor)
        {
            await TaskShortcuts.RunAsync(actionId, TargetFor(card, anchor));
        }
    }

    private static Style CardContainerStyle()
    {
        var style = new Style(typeof(ListViewItem)) { BasedOn = AppResources.Style("DefaultListViewItemStyle") };
        style.Setters.Add(new Setter(ListViewItem.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 0, 8)));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        return style;
    }
}
