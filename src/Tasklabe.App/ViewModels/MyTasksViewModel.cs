using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Tasklabe.App.Services;
using Tasklabe.Core.Domain;
using Tasklabe.Core.MyTasks;

namespace Tasklabe.App.ViewModels;

public sealed class TaskGroupViewModel(MyTaskGroupKind kind, IReadOnlyList<TaskRowViewModel> tasks, string? label = null)
    : List<TaskRowViewModel>(tasks)
{
    public MyTaskGroupKind Kind { get; } = kind;

    public string Header => $"{label ?? Label} ({Count})";

    private string Label => Kind == MyTaskGroupKind.Open ? "タスク" : "完了";
}

/// <param name="isTeam">チームのプロジェクトのタスクか（担当者を変えられるのはチームだけ）。</param>
public sealed class TaskRowViewModel(TaskItem task, string projectName, DateOnly today, string? parentTitle = null, bool isTeam = false)
{
    public TaskItem Task { get; } = task;

    public string Title => Task.Title;

    /// <summary>プロジェクト名。計画の中のタスクは、親タスク（フェーズ）も添える。</summary>
    public string ProjectName { get; } = parentTitle is { Length: > 0 }
        ? $"{projectName} / {parentTitle}"
        : projectName;

    public bool IsDoing => !Task.IsDone && Task.Category == StatusCategory.InProgress;

    public bool IsDone => Task.IsCompleted;

    /// <summary>完了・中止したタスクは目立たなくする。</summary>
    public bool IsMuted => Task.IsDone;

    public bool IsCanceled => Task.IsCanceled;

    /// <summary>中止したタスクは、カンバンのカードと同じく取り消し線で示す。</summary>
    public Windows.UI.Text.TextDecorations TitleDecorations =>
        IsCanceled ? Windows.UI.Text.TextDecorations.Strikethrough : Windows.UI.Text.TextDecorations.None;

    public double Progress => Task.EffectiveProgress;

    /// <summary>状態アイコンで示すカテゴリ。</summary>
    public StatusCategory IconCategory => IsCanceled ? StatusCategory.Canceled : IsDone ? StatusCategory.Done : Task.Category;

    public string StatusText => Task.StatusName ?? StatusVisuals.Name(Task.Category);

    public string StatusTip => Controls.KeyHints.Tip("task.status", StatusText);

    public string EstimateText => EffortText.Of(Task.EstimateHours, "—");

    public string ProgressText => $"{Task.EffectiveProgress:0}%";

    public string TargetText => Task.Target is { } d ? DateText.Short(d, today) : "";

    public bool IsOverdue => Task.IsOverdueOn(today);

    public bool IsLocal => Task.IsLocal;

    /// <summary>最後に更新した日時。今日なら時刻、それ以外は日付。</summary>
    public string UpdatedText
    {
        get
        {
            var local = Task.UpdatedAt.ToLocalTime();
            var day = DateOnly.FromDateTime(local.DateTime);
            return day == today ? local.ToString("HH:mm", Services.DateText.Japanese)
                : DateText.Short(day, today);
        }
    }

    /// <summary>課題の一覧でタイトルの前に出す番号（設定で出さないとき、送信待ちのときは空。要件 F-SET-06）。</summary>
    public string IssueNumberText => App.Current.Services.CurrentSettings.ShowNumberInIssues ? TaskKeys.Of(Task) : "";

    public bool HasIssueNumber => IssueNumberText.Length > 0;

    public string IssueAutomationName =>
        $"{Title}、{StatusText}、更新 {UpdatedText}" + (AssigneeText.Length > 0 ? $"、担当 {AssigneeText}" : "");

    public string AssigneeText => string.Join(", ", Task.Assignees.Select(a => "@" + a));

    // ---- 値のセル（押すとその場でピッカーを開く。空のときは「—」を出して押せることを示す）

    public bool CanAssign => isTeam;

    public string AssigneeCellText => AssigneeText.Length > 0 ? AssigneeText : isTeam ? "—" : "";

    public string DueCellText => TargetText.Length > 0 ? TargetText : "—";

    /// <summary>完了・中止したタスクの進捗率は変えられない。</summary>
    public bool CanChangeProgress => !Task.IsDone;

    public string StatusButtonName => $"{Title} のステータス: {StatusText}。押すとステータスを変更できます";

    public string AssigneeButtonName => $"{Title} の担当者: {(AssigneeText.Length > 0 ? AssigneeText : "なし")}。押すと変更できます";

    public string DueButtonName => $"{Title} の期日: {(TargetText.Length > 0 ? TargetText : "なし")}。押すと変更できます";

    public string EstimateButtonName => $"{Title} の想定工数: {EstimateText}。押すと変更できます";

    public string ProgressButtonName => $"{Title} の進捗率: {ProgressText}。押すと変更できます";

    public string AutomationName =>
        $"{Title}、{StatusText}、進捗 {ProgressText}、想定工数 {EstimateText}"
        + (TargetText.Length > 0 ? $"、期日 {TargetText}" + (IsOverdue ? "（期日超過）" : "") : "")
        + (AssigneeText.Length > 0 ? $"、担当 {AssigneeText}" : "");
}

/// <summary>マイタスク（要件 F-UI-MY-01、02、05、07、UI デザイン設計書 3.4 節）。</summary>
public sealed partial class MyTasksViewModel(AppServices services) : ObservableObject
{
    public ObservableCollection<TaskGroupViewModel> Groups { get; } = [];

    [ObservableProperty]
    public partial string DateText { get; set; } = "";

    [ObservableProperty]
    public partial string SummaryText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    /// <summary>カンバンに渡すタスクと、そのタスクが属するプロジェクト。</summary>
    public IReadOnlyList<TaskItem> Tasks { get; private set; } = [];

    /// <summary>カンバンに並べるタスク。完了・中止は直近に限らずすべて含め、カンバンの表示の設定で絞る。</summary>
    public IReadOnlyList<TaskItem> BoardTasks { get; private set; } = [];

    private Dictionary<string, string> _titleByIssue = [];

    /// <summary>計画の中のタスクの親タスク名。親がなければ null。</summary>
    public string? ParentTitleOf(TaskItem task) =>
        task.ParentIssueId is { } parent ? _titleByIssue.GetValueOrDefault(parent) : null;

    public IReadOnlyDictionary<string, Project> Projects { get; private set; } = new Dictionary<string, Project>();

    /// <summary>初めての同期が済んでおらず、まだ何も取り込んでいない。</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>リストの並び順。</summary>
    public Core.Kanban.TaskOrdering Ordering { get; set; }

    public async Task LoadAsync()
    {
        // プロジェクトをまたぐため、工数と稼働日は個人のプロジェクトの既定で表す
        ProjectPreferences.Apply(null);
        var today = AppClock.Today;
        var login = services.CurrentSettings.UserLogin;

        // 個人プロジェクトのタスクは自分のものとして扱い、チームは自分が担当するものだけを集める
        var projects = (await services.Store.GetProjectsAsync()).ToDictionary(p => p.Id);
        var allTasks = await services.Store.GetTasksAsync();
        var tasks = allTasks
            .Where(t => projects.TryGetValue(t.ProjectId, out var p)
                && (!p.IsTeam || (login is not null && t.Assignees.Contains(login, StringComparer.OrdinalIgnoreCase))))
            .ToList();

        var groups = MyTaskView.Build(tasks, Ordering);
        Projects = projects;
        Tasks = [.. groups.SelectMany(g => g.Tasks)];
        BoardTasks = [.. MyTaskView.ForBoard(tasks)];

        // 親タスク（フェーズ）の名前を引けるようにする
        var titleByIssue = allTasks.GroupBy(t => t.IssueId)
            .ToDictionary(g => g.Key, g => g.First().Title, StringComparer.Ordinal);
        _titleByIssue = titleByIssue;

        Groups.Clear();
        foreach (var g in groups)
        {
            Groups.Add(new TaskGroupViewModel(g.Kind, [.. g.Tasks.Select(t => new TaskRowViewModel(
                t,
                ProjectDisplay.Name(projects[t.ProjectId]),
                today,
                t.ParentIssueId is { } parent ? titleByIssue.GetValueOrDefault(parent) : null,
                projects[t.ProjectId].IsTeam))]));
        }

        DateText = Services.DateText.Short(today);

        var remaining = groups.Where(g => g.Kind != MyTaskGroupKind.Done)
            .SelectMany(g => g.Tasks)
            .Sum(t => t.RemainingHours ?? 0);
        SummaryText = $"残工数 {EffortText.Of(remaining)}";
        IsEmpty = Groups.Count == 0;
        IsLoading = projects.Count == 0 && services.Sync.Status.State is not (Core.Abstractions.SyncState.Offline or Core.Abstractions.SyncState.Error);
    }
}
