using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Tasklabe.App.Services;
using Tasklabe.Core.Domain;
using Tasklabe.Core.MyTasks;
using Tasklabe.Core.Wbs;

namespace Tasklabe.App.ViewModels;

/// <summary>プロジェクト画面（WBS、カンバン）。</summary>
public sealed partial class ProjectViewModel(AppServices services) : ObservableObject
{
    public Project? Project { get; private set; }

    /// <summary>計画のタスク（課題を除く）の階層。</summary>
    public TaskTree Tree { get; private set; } = TaskTree.Build([]);

    /// <summary>計画に入っていない課題（要件 F-TSK-08）。</summary>
    public ObservableCollection<TaskGroupViewModel> Issues { get; } = [];

    /// <summary>課題のタスク。カンバンなど、一覧以外で使う。</summary>
    public IReadOnlyList<TaskItem> IssueTasks { get; private set; } = [];

    [ObservableProperty]
    public partial int IssueCount { get; set; }

    [ObservableProperty]
    public partial bool IsIssuesEmpty { get; set; } = true;

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial string SummaryText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    /// <summary>担当者の絞り込み。null はすべて、空文字は未割り当て、それ以外はログイン名。</summary>
    public string? AssigneeFilter { get; set; }

    /// <summary>絞り込みの候補（ログイン名）。チームプロジェクトのみ。</summary>
    public IReadOnlyList<string> AssigneeCandidates { get; private set; } = [];

    /// <summary>
    /// 絞り込みに一致するノード。一致したタスクの祖先も、階層を保つために表示する。
    /// </summary>
    public Func<TaskNode, bool>? Visible { get; private set; }

    public async Task LoadAsync(string projectId)
    {
        Project = await services.Store.GetProjectAsync(projectId);
        if (Project is null)
        {
            return;
        }

        Title = Project.Title;
        ProjectPreferences.Apply(Project);
        var tasks = await services.Store.GetTasksAsync(projectId);

        // 個人プロジェクトは計画と課題を分けないため、区分によらずすべてを一覧に出す（要件 F-UI-PR-01）
        var planned = tasks.Where(t => t.IsPlanned || !Project.IsTeam).ToList();
        Tree = TaskTree.Build(planned);

        var today = AppClock.Today;
        var allIssues = Project.IsTeam
            ? tasks.Where(t => !t.IsPlanned).OrderByDescending(t => t.UpdatedAt).ToList()
            : [];

        // 担当者の絞り込みは、計画と同じく課題にも効かせる（UX 規約 UX-24）
        var issues = allIssues.Where(MatchesFilter).ToList();
        IssueTotal = allIssues.Count;
        IssueTasks = issues;
        IssueCount = issues.Count;
        IsIssuesEmpty = issues.Count == 0;
        Issues.Clear();

        // 未完了の課題と、完了・中止した課題を分けて並べる（完了は末尾にまとめる）
        var openIssues = issues.Where(t => !t.IsDone).ToList();
        var closedIssues = issues.Where(t => t.IsDone).ToList();
        if (openIssues.Count > 0)
        {
            Issues.Add(new TaskGroupViewModel(
                MyTaskGroupKind.Open,
                [.. openIssues.Select(t => new TaskRowViewModel(t, Project.Title, today, isTeam: Project.IsTeam))],
                "課題"));
        }

        if (closedIssues.Count > 0)
        {
            Issues.Add(new TaskGroupViewModel(
                MyTaskGroupKind.Done,
                [.. closedIssues.Select(t => new TaskRowViewModel(t, Project.Title, today, isTeam: Project.IsTeam))],
                "完了・中止"));
        }

        if (Project.IsTeam && Project.RepositoryNameWithOwner is { } repo)
        {
            var users = await services.Store.GetAssignableUsersAsync(repo);
            AssigneeCandidates = users.Select(u => u.Login).Union(tasks.SelectMany(t => t.Assignees), StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase).ToList();
        }

        Visible = BuildFilter();
        PlannedTotal = Tree.All().Count();
        PlannedShown = Visible is { } visible ? Tree.All().Count(visible) : PlannedTotal;

        // 要約は、計画を持つチームでは進み具合を、個人では残っている量を示す（要件 F-PRG-04）
        var summary = Tree.Summary;
        int open = planned.Count(t => !t.IsDone);
        SummaryText = planned.Count == 0
            ? ""
            : Project.IsTeam
                ? $"進捗 {summary.ProgressPercent:0}%   残 {EffortText.Of(summary.RemainingHours)} / {EffortText.Of(summary.EstimateHours)}"
                    + (summary.UnestimatedCount > 0 ? $"   未見積り {summary.UnestimatedCount} 件" : "")
                : $"未完了 {open} 件" + (summary.RemainingHours > 0 ? $"   残 {EffortText.Of(summary.RemainingHours)}" : "");
        IsEmpty = planned.Count == 0;
    }

    /// <summary>計画のタスクの数と、絞り込みで表示している数。</summary>
    public int PlannedTotal { get; private set; }

    public int PlannedShown { get; private set; }

    /// <summary>絞り込む前の課題の数（表示している数は <see cref="IssueCount"/>）。</summary>
    public int IssueTotal { get; private set; }

    /// <summary>担当者の絞り込みに一致するか。絞り込んでいなければ常に一致する。</summary>
    public bool MatchesFilter(TaskItem task) => AssigneeFilter switch
    {
        null => true,
        "" => task.Assignees.Count == 0,
        var login => task.Assignees.Contains(login, StringComparer.OrdinalIgnoreCase),
    };

    private Func<TaskNode, bool>? BuildFilter()
    {
        if (AssigneeFilter is null)
        {
            return null;
        }

        var visible = new HashSet<string>();
        foreach (var node in Tree.All().Where(n => !n.HasChildren && MatchesFilter(n.Task)))
        {
            for (var current = node; current is not null; current = current.Parent)
            {
                visible.Add(current.Task.ItemId);
            }
        }

        return n => visible.Contains(n.Task.ItemId);
    }
}
