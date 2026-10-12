using System.Globalization;
using Microsoft.UI.Xaml;
using Tasklabe.App.Services;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Kanban;
using Tasklabe.Core.MyTasks;
using Tasklabe.Core.Scheduling;
using Tasklabe.Core.Wbs;
using Tasklabe.GitHub;
using Windows.Foundation;

namespace Tasklabe.App.Controls;

/// <summary>ピッカーで選んだ値。何も選ばずに閉じたときは、これ自体が null になる。</summary>
internal readonly record struct Picked<T>(T Value);

/// <summary>複数を選ぶピッカー（担当者、リポジトリ）の結果。選択肢ごとに、選ぶ・外す・変えない（混在のまま）を持つ。</summary>
internal sealed record ToggleChoice(IReadOnlyList<string> Names, IReadOnlyList<PickState> States)
{
    /// <summary>あるタスクの値に、この選択を当てはめる。</summary>
    public IReadOnlyList<string> ApplyTo(IEnumerable<string> current)
    {
        var result = current.ToList();
        for (int i = 0; i < Names.Count; i++)
        {
            var login = Names[i];
            bool has = result.Contains(login, StringComparer.OrdinalIgnoreCase);
            if (States[i] == PickState.Checked && !has)
            {
                result.Add(login);
            }
            else if (States[i] == PickState.Unchecked && has)
            {
                result.RemoveAll(l => string.Equals(l, login, StringComparison.OrdinalIgnoreCase));
            }
        }

        return result;
    }
}

/// <summary>
/// ステータスの候補。1 つのプロジェクトではそのステータスを、プロジェクトをまたぐときは 6 つのカテゴリを候補にする。
/// カテゴリを選んだときは、各プロジェクトでそのカテゴリの代表のステータスにする。
/// </summary>
internal sealed record StatusChoice(string Name, StatusCategory Category, bool ByCategory = false)
{
    public static IReadOnlyList<StatusChoice> For(IReadOnlyCollection<Project> projects) => projects.Count == 1
        ? [.. projects.First().StatusOptions.Select(o => new StatusChoice(o.Name, o.Category))]
        : [.. StatusCategories.All.Select(c => new StatusChoice(StatusVisuals.Name(c), c, ByCategory: true))];

    /// <summary>そのプロジェクトで対応する選択肢（カテゴリなら代表、名前なら同じ名前、なければ同じカテゴリの代表）。</summary>
    public StatusOption? In(Project project) => ByCategory
        ? ProjectConventions.DefaultStatus(project.StatusOptions, Category)
        : project.StatusOptions.FirstOrDefault(o => KanbanLayout.NameKey(o.Name) == KanbanLayout.NameKey(Name))
            ?? ProjectConventions.DefaultStatus(project.StatusOptions, Category);

    /// <summary>いまの値がこの候補に当たるか。</summary>
    public bool Matches(string? statusName, StatusCategory category) => ByCategory
        ? category == Category
        : statusName is not null && KanbanLayout.NameKey(statusName) == KanbanLayout.NameKey(Name);

    /// <summary>
    /// 変えた結果の知らせ。複数のタスクを変えたときと、カテゴリのステータスがないプロジェクトで近いカテゴリに入れたときに出す。
    /// </summary>
    public string? Message(int count, IEnumerable<Project> projects)
    {
        var substituted = ByCategory
            ? projects.Select(p => In(p)?.Category).OfType<StatusCategory>().Where(c => c != Category).Distinct().ToList()
            : [];
        var head = count > 1 ? Texts.Format("Status_ChangedMany", count, Name) : Texts.Format("Status_ChangedOne", Name);
        return substituted.Count > 0
            ? Texts.Format("Status_Substituted", head, Name, string.Join(Texts.Get("Common_NameSeparator"), substituted.Select(StatusVisuals.Name)))
            : count > 1 ? head : null;
    }
}

/// <summary>
/// 値ごとのピッカー（UX 規約 UX-02）。キー、値のセル、カード、右クリックのメニュー、ダイアログ、詳細パネルの
/// どこから開いても、同じ候補・同じ並び・同じ形にする。いまの値は、対象が複数のときは混在も示す（UX-07）。
/// </summary>
internal static class ValuePickers
{
    private static DateOnly Today => AppClock.Today;

    /// <summary>いまの値の印。対象のすべてが一致すれば選択、一部だけなら混在。</summary>
    private static (bool Selected, bool Mixed) Mark<T>(IReadOnlyCollection<T> current, Func<T, bool> match)
    {
        int count = current.Count(match);
        return (count > 0 && count == current.Count, count > 0 && count < current.Count);
    }

    // ---------------------------------------------------------------- ステータス

    public static async Task<Picked<StatusChoice>?> StatusAsync(FrameworkElement anchor, Point? position,
        IReadOnlyCollection<Project> projects, IReadOnlyCollection<(string? Name, StatusCategory Category)> current)
    {
        var choices = StatusChoice.For(projects);
        var options = choices.Select(c =>
        {
            var (selected, mixed) = Mark(current, v => c.Matches(v.Name, v.Category));
            return new PickerOption(c.Name, c.ByCategory ? "" : StatusVisuals.Name(c.Category), StatusVisuals.Glyph(c.Category), selected,
                StatusVisuals.BrushKey(c.Category), mixed);
        }).ToList();

        return await new QuickPicker(projects.Count > 1 ? Texts.Get("Picker_StatusCategory") : Texts.Get("Picker_Status"), options).ShowAsync(anchor, position) is { Index: >= 0 } r
            ? new Picked<StatusChoice>(choices[r.Index])
            : null;
    }

    // ---------------------------------------------------------------- 進捗率

    public static async Task<Picked<double>?> ProgressAsync(FrameworkElement anchor, Point? position, IReadOnlyCollection<double> current)
    {
        double[] values = [0, 25, 50, 75, 100];
        var options = values.Select(v =>
        {
            var (selected, mixed) = Mark(current, c => Math.Abs(c - v) < 0.5);
            return new PickerOption($"{v:0}%", IsSelected: selected, IsMixed: mixed);
        }).ToList();
        var picker = new QuickPicker(Texts.Get("Picker_Progress"), options, input: Texts.Get("Picker_ProgressInput"), validate: text =>
            ParseNumber(text.TrimEnd('%')) is { } v && v is >= 0 and <= 100 ? null : Texts.Get("Picker_ProgressInvalid"));
        return await picker.ShowAsync(anchor, position) switch
        {
            { Index: >= 0 } r => new Picked<double>(values[r.Index]),
            { Text: { } text } => new Picked<double>(ParseNumber(text.TrimEnd('%'))!.Value),
            _ => null,
        };
    }

    // ---------------------------------------------------------------- 担当者

    /// <summary>
    /// 担当者を選ぶ（複数）。候補は、リポジトリに割り当てられる GitHub の利用者と、
    /// プロジェクトの設定に加えた GitHub を使わないメンバー。
    /// </summary>
    /// <param name="projects">候補を取るチームのプロジェクト。</param>
    public static async Task<ToggleChoice?> AssigneesAsync(FrameworkElement anchor, Point? position,
        IEnumerable<Project> projects, IReadOnlyCollection<IReadOnlyList<string>> current)
    {
        var services = App.Current.Services;
        var me = services.CurrentSettings.UserLogin;
        var users = new List<Assignee>();
        var guests = new List<string>();
        foreach (var project in projects.DistinctBy(p => p.Id))
        {
            if (project.RepositoryNameWithOwner is { } repo)
            {
                users.AddRange(await services.Store.GetAssignableUsersAsync(repo));
            }

            guests.AddRange(project.Settings.Guests.Select(People.Guest));
        }

        // GitHub の利用者（自分を先頭）、名前だけのメンバーの順に並べる
        var assignees = users.Select(u => u.Login)
            .Union(current.SelectMany(c => c), StringComparer.OrdinalIgnoreCase)
            .Union(guests, StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => People.IsGuest(a))
            .ThenByDescending(a => string.Equals(a, me, StringComparison.OrdinalIgnoreCase))
            .ThenBy(a => People.Name(a), StringComparer.OrdinalIgnoreCase)
            .ToList();
        var options = assignees.Select(a =>
        {
            var (selected, mixed) = Mark(current, c => c.Contains(a, StringComparer.OrdinalIgnoreCase));
            var name = users.FirstOrDefault(u => string.Equals(u.Login, a, StringComparison.OrdinalIgnoreCase))?.Name;
            var detail = People.IsGuest(a) ? Texts.Get("Picker_NoGitHub")
                : string.Equals(a, me, StringComparison.OrdinalIgnoreCase) ? Texts.Get("Picker_Me")
                : string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            return new PickerOption(People.Display(a), detail, IsSelected: selected, IsMixed: mixed, Avatar: a);
        }).ToList();

        var picker = new QuickPicker(Texts.Get("Picker_Assignee"), options, multiple: true, note: Texts.Get("Picker_AssigneeNote"));
        return await picker.ShowAsync(anchor, position) is null ? null : new ToggleChoice(assignees, picker.States);
    }

    // ---------------------------------------------------------------- 作業するリポジトリ（要件 F-TSK-18）

    private static IReadOnlyList<WorkRepository>? s_repositories;

    /// <summary>
    /// 作業するリポジトリを選ぶ（複数）。候補は自分が触れるリポジトリを最近 push した順に並べ、いま選んでいるものを先頭に置く。
    /// </summary>
    public static async Task<ToggleChoice?> RepositoriesAsync(FrameworkElement anchor, Point? position, IReadOnlyCollection<IReadOnlyList<string>> current)
    {
        if (await RepositoryCandidatesAsync() is not { } candidates)
        {
            return null;
        }

        var chosen = current.SelectMany(c => c).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var names = chosen.Order(StringComparer.OrdinalIgnoreCase)
            .Concat(candidates.Select(r => r.NameWithOwner).Where(n => !chosen.Contains(n, StringComparer.OrdinalIgnoreCase)))
            .ToList();
        var options = names.Select(n =>
        {
            var (selected, mixed) = Mark(current, c => c.Contains(n, StringComparer.OrdinalIgnoreCase));
            var canWrite = candidates.FirstOrDefault(r => string.Equals(r.NameWithOwner, n, StringComparison.OrdinalIgnoreCase))?.CanWrite;
            return new PickerOption(n, canWrite == false ? Texts.Get("Picker_ReadOnly") : null, IsSelected: selected, IsMixed: mixed);
        }).ToList();

        var picker = new QuickPicker(Texts.Get("Picker_Repository"), options, multiple: true, filterable: true, input: Texts.Get("Picker_RepositoryInput"), width: 400,
            note: Texts.Get("Picker_RepositoryNote"));
        return await picker.ShowAsync(anchor, position) is null ? null : new ToggleChoice(names, picker.States);
    }

    /// <summary>
    /// ブランチを作るリポジトリを 1 つ選ぶ。タスクの作業するリポジトリを先頭に、ほかの候補を続ける。
    /// ブランチを作れない（読み取りのみの）リポジトリは候補に出さない。
    /// </summary>
    public static async Task<Picked<string>?> RepositoryAsync(FrameworkElement anchor, Point? position, IReadOnlyList<string> preferred, string? current)
    {
        if (await RepositoryCandidatesAsync() is not { } candidates)
        {
            return null;
        }

        var others = candidates.Where(r => r.CanWrite && !preferred.Contains(r.NameWithOwner, StringComparer.OrdinalIgnoreCase))
            .Select(r => r.NameWithOwner).ToList();
        var names = preferred.Concat(others).ToList();
        var options = names.Select((n, i) => new PickerOption(n, IsSelected: string.Equals(n, current, StringComparison.OrdinalIgnoreCase),
            Group: i < preferred.Count ? Texts.Get("Picker_Repository") : Texts.Get("Picker_OtherRepositories"))).ToList();
        var picker = new QuickPicker(Texts.Get("Picker_BranchRepository"), options, filterable: true, input: Texts.Get("Picker_RepositoryInput"), width: 400);
        return await picker.ShowAsync(anchor, position) is { Index: >= 0 } r ? new Picked<string>(names[r.Index]) : null;
    }

    /// <summary>候補。一度取ったものを使い、開くたびに裏で取り直す。取れなければ理由を示して null を返す。</summary>
    private static async Task<IReadOnlyList<WorkRepository>?> RepositoryCandidatesAsync()
    {
        var refresh = RefreshRepositoriesAsync();
        if ((s_repositories ?? await refresh) is { } candidates)
        {
            return candidates;
        }

        App.Current.Shell?.ShowInfo(Texts.Get("Picker_RepositoryFailedTitle"), Texts.Get("Picker_RepositoryFailedMessage"));
        return null;
    }

    private static async Task<IReadOnlyList<WorkRepository>?> RefreshRepositoriesAsync()
    {
        try
        {
            return s_repositories = await App.Current.Services.Api.ListWorkRepositoriesAsync();
        }
        catch (Exception ex) when (ex is GitHubException or HttpRequestException)
        {
            AppLog.Error("リポジトリの候補の取得", ex);
            return null;
        }
    }

    // ---------------------------------------------------------------- 日付

    /// <summary>期日の候補。1 日を選ぶ暦の左に並べる。</summary>
    public static IReadOnlyList<DateRangePreset> DuePresets { get; } =
    [
        new(Texts.Get("Preset_Today"), t => (t, t)),
        new(Texts.Get("Preset_Tomorrow"), t => (t.AddDays(1), t.AddDays(1))),
        new(Texts.Get("Preset_ThisWeekend"), t => (MyTaskView.EndOfWeek(t), MyTaskView.EndOfWeek(t))),
        new(Texts.Get("Preset_NextWeekend"), t => (MyTaskView.EndOfWeek(t).AddDays(7), MyTaskView.EndOfWeek(t).AddDays(7))),
    ];

    /// <summary>1 日を選ぶ暦で日付を選ぶ。「クリア」で消す。複数のタスクで値が混在しているときは、いまの値を示さない。</summary>
    /// <param name="title">「期日（予定終了日）」「予定開始日」など。</param>
    public static Task<Picked<DateOnly?>?> DateAsync(FrameworkElement anchor, Point? position, string title,
        IReadOnlyCollection<DateOnly?> current)
    {
        var values = current.Distinct().ToList();
        bool mixed = values.Count > 1;
        return DateRangePicker.ShowDateAsync(anchor, position, title, mixed ? null : values.FirstOrDefault(), mixed, DuePresets);
    }

    // ---------------------------------------------------------------- 日付の範囲

    /// <summary>予定（開始日〜期日）の候補。先の日付を決めることが多いので、今日から先の範囲を並べる。</summary>
    public static IReadOnlyList<DateRangePreset> PlanPresets { get; } =
    [
        new(Texts.Get("Preset_Today"), t => (t, t)),
        new(Texts.Get("Preset_Tomorrow"), t => (t.AddDays(1), t.AddDays(1))),
        new(Texts.Get("Preset_UntilWeekend"), t => (t, MyTaskView.EndOfWeek(t))),
        new(Texts.Get("Preset_NextWeek"), t => (MyTaskView.EndOfWeek(t).AddDays(1), MyTaskView.EndOfWeek(t).AddDays(5))),
        new(Texts.Get("Preset_OneWeek"), t => (t, t.AddDays(6))),
        new(Texts.Get("Preset_TwoWeeks"), t => (t, t.AddDays(13))),
        new(Texts.Get("Preset_UntilMonthEnd"), t => (t, new DateOnly(t.Year, t.Month, 1).AddMonths(1).AddDays(-1))),
        new(Texts.Get("Preset_NextMonth"), t => (new DateOnly(t.Year, t.Month, 1).AddMonths(1), new DateOnly(t.Year, t.Month, 1).AddMonths(2).AddDays(-1))),
    ];

    /// <summary>実績（開始日〜終了日）の候補。済んだ日付を記すので、今日までの範囲を並べる。</summary>
    public static IReadOnlyList<DateRangePreset> ActualPresets { get; } =
    [
        new(Texts.Get("Preset_Today"), t => (t, t)),
        new(Texts.Get("Preset_Yesterday"), t => (t.AddDays(-1), t.AddDays(-1))),
        new(Texts.Get("Preset_YesterdayToToday"), t => (t.AddDays(-1), t)),
        new(Texts.Get("Preset_ThisWeekSoFar"), t => (MyTaskView.EndOfWeek(t).AddDays(-6), t)),
        new(Texts.Get("Preset_LastWeek"), t => (MyTaskView.EndOfWeek(t).AddDays(-13), MyTaskView.EndOfWeek(t).AddDays(-9))),
        new(Texts.Get("Preset_ThisMonthSoFar"), t => (new DateOnly(t.Year, t.Month, 1), t)),
    ];

    // ---------------------------------------------------------------- 想定工数

    public static async Task<Picked<double?>?> EstimateAsync(FrameworkElement anchor, Point? position, IReadOnlyCollection<double?> current)
    {
        double?[] values = [0.5, 1, 2, 4, 8, 16, 24, 40, null];
        var options = values.Select(v =>
        {
            var (selected, mixed) = Mark(current, c => c == v);
            return new PickerOption(v is { } h ? EffortText.Of(h) : Texts.Get("Common_None"), IsSelected: selected, IsMixed: mixed);
        }).ToList();
        var picker = new QuickPicker(Texts.Get("Picker_Estimate"), options, input: Texts.Get("Picker_EstimateInput"), validate: text =>
            ParseNumber(text.TrimEnd('h', 'H')) is >= 0 ? null : Texts.Get("Picker_EstimateInvalid"));
        return await picker.ShowAsync(anchor, position) switch
        {
            { Index: >= 0 } r => new Picked<double?>(values[r.Index]),
            { Text: { } text } => new Picked<double?>(Math.Round(ParseNumber(text.TrimEnd('h', 'H'))!.Value, 2)),
            _ => null,
        };
    }

    // ---------------------------------------------------------------- プロジェクト

    /// <param name="current">いまのプロジェクト（チェックを付ける）。</param>
    /// <param name="excludeCurrent">いまのプロジェクトを候補から外す（別のプロジェクトへ移すとき）。</param>
    public static async Task<Picked<Project>?> ProjectAsync(FrameworkElement anchor, Point? position, string title,
        IReadOnlyCollection<string> current, bool excludeCurrent)
    {
        var projects = (await App.Current.Services.Store.GetProjectsAsync())
            .Where(p => !p.Closed && !(excludeCurrent && current.Contains(p.Id)))
            .OrderBy(p => p.Kind == ProjectKind.Inbox ? 0 : p.IsTeam ? 2 : 1)
            .ThenBy(p => p.Title, StringComparer.CurrentCulture)
            .ToList();
        var options = projects.Select(p => new PickerOption(
            ProjectDisplay.Name(p),
            p.Kind == ProjectKind.Inbox ? Texts.Get("Picker_DefaultProject") : null,
            p.Kind switch { ProjectKind.Inbox => "\uE7C3", ProjectKind.Team => "\uE716", _ => "\uE77B" },
            IsSelected: !excludeCurrent && current.Contains(p.Id),
            Group: p.IsTeam ? Texts.Get("Common_Team") : Texts.Get("Common_Personal"))).ToList();
        return await new QuickPicker(title, options).ShowAsync(anchor, position) is { Index: >= 0 } r
            ? new Picked<Project>(projects[r.Index])
            : null;
    }

    // ---------------------------------------------------------------- 区分

    public static async Task<Picked<TaskKind>?> KindAsync(FrameworkElement anchor, Point? position, IReadOnlyCollection<TaskKind> current)
    {
        // マイルストーンはタスクではなくプロジェクトの期限のため、区分には出さない（要件 F-MS-01）
        TaskKind[] kinds = [TaskKind.Issue, TaskKind.Task];
        var options = kinds.Select(k =>
        {
            var (selected, mixed) = Mark(current, c => c == k);
            return new PickerOption(KindVisuals.Name(k), IsSelected: selected, IsMixed: mixed, ToolTip: KindVisuals.Description(k));
        }).ToList();
        return await new QuickPicker(Texts.Get("Picker_Kind"), options, width: 420).ShowAsync(anchor, position) is { Index: >= 0 } r
            ? new Picked<TaskKind>(kinds[r.Index])
            : null;
    }

    // ---------------------------------------------------------------- 先行タスク・置き場所

    /// <summary>先行タスクの候補（張れるものだけ）から選ぶ。</summary>
    public static async Task<Picked<TaskItem>?> PredecessorAsync(FrameworkElement anchor, Point? position, TaskItem task)
    {
        var tasks = await App.Current.Services.Store.GetTasksAsync(task.ProjectId);
        var tree = TaskTree.Build(tasks.Where(t => t.IsPlanned));
        var me = tree.Find(task.ItemId)?.Task ?? task;
        var candidates = tree.All().Where(n => DependencyScheduler.CanLink(tree, n.Task, me) == LinkCheck.Ok).ToList();
        var options = candidates
            .Select(n => new PickerOption(new string('　', n.Depth) + n.Task.Title, n.Task.Target is { } t ? Texts.Format("Date_RangeTo", DateText.Short(t)) : null))
            .ToList();
        var picker = new QuickPicker(Texts.Get("Picker_Predecessor"), options, input: Texts.Get("Picker_TaskInput"), filterable: true, width: 380);
        return await picker.ShowAsync(anchor, position) is { Index: >= 0 } r ? new Picked<TaskItem>(candidates[r.Index].Task) : null;
    }

    /// <summary>計画の中の置き場所（親タスク）を選ぶ。null は最上位。</summary>
    public static async Task<Picked<TaskNode?>?> ParentAsync(FrameworkElement anchor, Point? position, TaskTree plan, string? currentIssueId)
    {
        var nodes = plan.All().ToList();
        var options = new List<PickerOption> { new(Texts.Get("Picker_TopLevel"), IsSelected: currentIssueId is null) };
        options.AddRange(nodes.Select(n => new PickerOption(
            new string('　', n.Depth) + n.Task.Title,
            IsSelected: n.Task.IssueId == currentIssueId)));
        var picker = new QuickPicker(Texts.Get("Picker_Parent"), options, input: nodes.Count >= 10 ? Texts.Get("Picker_TaskInput") : null, filterable: nodes.Count >= 10, width: 380);
        return await picker.ShowAsync(anchor, position) switch
        {
            { Index: 0 } => new Picked<TaskNode?>(null),
            { Index: > 0 } r => new Picked<TaskNode?>(nodes[r.Index - 1]),
            _ => null,
        };
    }

    // ---------------------------------------------------------------- 決まった候補から選ぶ（設定など）

    /// <summary>決まった候補から 1 つを選ぶ。選んだ番号、取り消したら null。</summary>
    /// <param name="tips">選択肢の意味。ツールチップで添える。</param>
    public static async Task<int?> ChoiceAsync(FrameworkElement anchor, string title, IReadOnlyList<string> labels, int current,
        IReadOnlyList<string?>? details = null, IReadOnlyList<string?>? tips = null)
    {
        var options = labels.Select((l, i) => new PickerOption(l, details?[i], IsSelected: i == current, ToolTip: tips?[i])).ToList();
        return await new QuickPicker(title, options, width: Math.Max(320, anchor.ActualWidth)).ShowAsync(anchor) is { Index: >= 0 } r ? r.Index : null;
    }

    /// <summary>全角も読めるよう半角に直して数値として読む（UX-30）。</summary>
    public static double? ParseNumber(string text) =>
        double.TryParse(text.Trim().Normalize(System.Text.NormalizationForm.FormKC), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}
