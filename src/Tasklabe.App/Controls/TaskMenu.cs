using Microsoft.UI.Xaml;
using Tasklabe.App.Services;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Editing;
using Windows.Foundation;

namespace Tasklabe.App.Controls;

/// <summary>
/// タスクの右クリックのメニュー（UX 規約 UX-20）。一覧・計画の表・カンバン・ガントチャートで同じ項目を同じ順に並べる。
/// 並びは「値を変える」「タスクを扱う」「画面に固有の操作」「取り消せない操作」とし、各項目に同じ操作のキーを添える。
/// その場で使えない項目は消さずに無効にして位置を保つ（チームにしかない項目は、個人のプロジェクトでは出さない）。
/// 複数を選んでいるときは、選んでいるすべてに効く。
/// </summary>
internal static class TaskMenu
{
    /// <param name="extras">画面に固有の操作（ガントチャートの「予定をクリア」など）。</param>
    /// <param name="isParent">子を持つタスク（工数・進捗率・ステータスは配下から決まるため変えられない）。</param>
    public static async Task<IReadOnlyList<MenuEntry>> BuildAsync(TaskTarget target, IReadOnlyList<MenuEntry>? extras = null, bool isParent = false)
    {
        ArgumentNullException.ThrowIfNull(target);

        var store = App.Current.Services.Store;
        var projects = new Dictionary<string, Project>(StringComparer.Ordinal);
        foreach (var id in target.Tasks.Select(t => t.ProjectId).Distinct())
        {
            if (await store.GetProjectAsync(id) is { } p)
            {
                projects[id] = p;
            }
        }

        var task = target.Task;
        var tasks = target.Tasks;
        bool many = target.IsMultiple;
        bool anyTeam = tasks.Any(t => projects.GetValueOrDefault(t.ProjectId)?.IsTeam == true);
        var menu = new List<MenuEntry>();

        if (many)
        {
            menu.Add(new MenuHeader(Texts.Format("SelectionBar_Count", tasks.Count)));
            menu.Add(MenuSeparator.Instance);
        }

        // 値を変える
        var status = StatusSubmenu(tasks, projects);
        menu.Add(status with { IsEnabled = !isParent && status.Items.Count > 0 });
        menu.Add(Item(Texts.Get("Menu_Progress"), "\uE9D2", "task.progress", target, enabled: !isParent && tasks.Any(t => !t.IsDone)));
        if (anyTeam)
        {
            menu.Add(Item(Texts.Get("Menu_Assignee"), "\uE77B", "task.assign", target));
        }

        menu.Add(Item(Texts.Get("Menu_Due"), "\uE787", "task.due", target));
        menu.Add(Item(Texts.Get("Menu_Estimate"), "\uE916", "task.estimate", target, enabled: !isParent));
        menu.Add(MenuSeparator.Instance);

        // タスクを扱う（1 件にしか効かない操作は、複数を選んでいるときは無効にする）
        menu.Add(Item(KeyHints.Name("task.detail"), "\uE8A7", "task.detail", target, enabled: !many));
        menu.Add(Item(KeyHints.Name("task.addChild"), "\uE710", "task.addChild", target, enabled: !many && TaskShortcuts.CanHaveChildren(task)));
        if (anyTeam)
        {
            bool issue = task.Kind == TaskKind.Issue;
            menu.Add(Item(issue ? Texts.Get("Menu_MoveToPlan") : Texts.Get("Menu_MoveToTriage"), issue ? "\uE8DE" : "\uE7C1", "task.plan", target));
        }

        menu.Add(Item(Texts.Format("Common_Ellipsis", KeyHints.Name("task.project")), "\uE8DE", "task.project", target));
        menu.Add(Item(Texts.Get("Menu_Repository"), "\uE8B7", "task.repository", target));
        menu.Add(Item(Texts.Format("Common_Ellipsis", KeyHints.Name("task.branch")), "\uE8D4", "task.branch", target, enabled: !many && !task.IsLocal));
        menu.Add(Item(KeyHints.Name("task.openInBrowser"), "\uE774", "task.openInBrowser", target, enabled: !many && task.Url is not null));
        menu.Add(Item(many || !TaskKeys.Has(task) ? KeyHints.Name("task.copyKey") : Texts.Format("Menu_CopyKeyOf", TaskKeys.Of(task)), "\uE8C8", "task.copyKey", target, enabled: tasks.Any(TaskKeys.Has)));
        menu.Add(Item(KeyHints.Name("task.copyUrl"), "\uE8C8", "task.copyUrl", target, enabled: !many && task.Url is not null));

        // 画面に固有の操作
        if (extras is { Count: > 0 })
        {
            menu.Add(MenuSeparator.Instance);
            menu.AddRange(extras);
        }

        // 取り消せない操作は末尾に分ける
        menu.Add(MenuSeparator.Instance);
        menu.Add(Item(Texts.Format("Common_Ellipsis", KeyHints.Name("task.delete")), "\uE74D", "task.delete", target) with { IsDestructive = true });
        return menu;
    }

    /// <summary>
    /// 依存関係の操作（要件 F-DEP-07、08）。計画の表とガントチャートで、画面に固有の操作として同じ項目を同じ順に並べる。
    /// 1 件にしか効かないため、複数を選んでいるときは無効にする。
    /// </summary>
    /// <param name="hasChildren">配下を持つタスクか（「配下を予定の順に接続…」を使える）。</param>
    /// <param name="plan">先行タスクの名前を引くタスク（同じプロジェクトの計画）。</param>
    public static IReadOnlyList<MenuEntry> DependencyEntries(TaskTarget target, bool hasChildren, IEnumerable<TaskItem> plan)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(plan);

        var task = target.Task;
        bool single = !target.IsMultiple;
        var byIssue = new Dictionary<string, TaskItem>(StringComparer.Ordinal);
        foreach (var t in plan)
        {
            byIssue.TryAdd(t.IssueId, t);
        }

        var predecessors = task.BlockedBy.Select(id => (MenuEntry)new MenuCommand(
            byIssue.TryGetValue(id, out var pred) ? $"{TaskKeys.Of(pred)} {pred.Title}" : Texts.Get("Menu_ExternalTask"), null,
            () => App.Current.Services.Edits.SetBlockedByAsync(task, task.BlockedBy.Where(b => b != id)))).ToList();

        return
        [
            new MenuCommand(Texts.Get("Menu_ChainChildren"), "\uE71B", () => TaskCommands.ChainChildrenAsync(target.Anchor.XamlRoot, task))
            {
                IsEnabled = single && hasChildren,
            },
            Item(Texts.Format("Common_Ellipsis", KeyHints.Name("task.predecessor")), "\uE71B", "task.predecessor", target, enabled: single),
            new MenuSubmenu(Texts.Get("Menu_RemovePredecessor"), "\uE711", predecessors) { IsEnabled = single && predecessors.Count > 0 },
            new MenuCommand(Texts.Get("Menu_NonBlocking"), null, () => App.Current.Services.Edits.SetNonBlockingAsync(task, !task.NonBlocking))
            {
                IsEnabled = single,
                IsChecked = task.NonBlocking,
            },
        ];
    }

    /// <summary>メニューを開く。位置があれば右クリックした位置から、なければ要素から育てる（Shift + F10）。</summary>
    public static async Task ShowAsync(TaskTarget target, UIElement placementTarget, Point? position,
        IReadOnlyList<MenuEntry>? extras = null, bool isParent = false)
    {
        var items = await BuildAsync(target, extras, isParent);
        new MorphMenu(Texts.Get("Menu_TaskMenuName"), items).Show((FrameworkElement)placementTarget, position);
    }

    /// <summary>ステータスのサブメニュー。選んだものは、選んでいるすべてのタスクに効く。</summary>
    private static MenuSubmenu StatusSubmenu(IReadOnlyList<TaskItem> tasks, IReadOnlyDictionary<string, Project> projects)
    {
        var list = projects.Values.ToList();
        var choices = StatusChoice.For(list);

        var today = AppClock.Today;
        var items = new List<MenuEntry>();
        foreach (var choice in choices)
        {
            items.Add(new MenuCommand(choice.Name, StatusVisuals.Glyph(choice.Category), () => TaskCommands.ApplyAsync(
                [.. tasks.Where(t => projects.ContainsKey(t.ProjectId)).Select(t => (t,
                    choice.In(projects[t.ProjectId]) is { } option ? TaskRules.ChangeStatus(t, option, today) : (IReadOnlyList<TaskChange>)[]))],
                choice.Message(tasks.Count, list)))
            {
                IsChecked = tasks.All(t => choice.Matches(t.StatusName, t.Category)),
                GlyphBrushKey = StatusVisuals.BrushKey(choice.Category),
            });
        }

        return new MenuSubmenu(Texts.Get("Menu_Status"), StatusVisuals.Glyph(tasks[0].Category), items)
        {
            GlyphBrushKey = StatusVisuals.BrushKey(tasks[0].Category),
        };
    }

    private static MenuCommand Item(string text, string glyph, string actionId, TaskTarget target, bool enabled = true) =>
        new(text, glyph, () => TaskShortcuts.RunAsync(actionId, target))
        {
            IsEnabled = enabled,
            Accelerator = App.Current.Services.Keymap.Display(actionId),
        };
}
