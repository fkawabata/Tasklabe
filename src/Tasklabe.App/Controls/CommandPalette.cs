using Microsoft.UI.Xaml;
using Tasklabe.App.Services;
using Tasklabe.App.Views;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Keyboard;
using Windows.Foundation;

namespace Tasklabe.App.Controls;

/// <summary>
/// コマンドパレット（要件 F-KEY-05、UX 規約 UX-18）。キーを覚えていなくても、操作・画面・タスクを名前で探して実行できる。
/// 画面の上端の中央に開き、入力欄から始める。タスクへの操作は、開く前に選んでいたタスク（複数を含む）に効く。
/// いまの画面と選択で使えない操作は出さない。
/// </summary>
internal static class CommandPalette
{
    /// <summary>最近使った操作（新しい順）。何も入力していないときに先頭へ並べる。</summary>
    private static readonly List<string> Recent = [];

    private sealed record Entry(string Key, PickerOption Option, Func<Task> Run);

    public static async Task ShowAsync(MainWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var services = App.Current.Services;
        var keymap = services.Keymap;
        var root = (FrameworkElement)window.Content;
        var target = TaskShortcuts.FocusedTask(root.XamlRoot);
        var entries = new List<Entry>();

        // 全体の操作
        foreach (var d in ShortcutCatalog.All.Where(d => d.Scope == ShortcutScope.Global && d.Id != "app.palette"))
        {
            if ((d.Id == "view.filter" && window.CurrentPage is not IFilterHost)
                || (d.Id is "view.sort" or "view.group" && window.CurrentPage is not IViewOptionsHost))
            {
                continue;
            }

            entries.Add(new Entry(d.Id, new PickerOption(d.Label, keymap.Display(d.Id), Group: "操作"), () => window.RunGlobalCommandAsync(d.Id)));
        }

        // 選んでいるタスクへの操作（使えないものは出さない）
        if (target is not null)
        {
            var project = await services.Store.GetProjectAsync(target.Task.ProjectId);
            bool team = project?.IsTeam == true;
            string subject = target.IsMultiple ? $"{target.Tasks.Count} 件" : $"「{Shorten(target.Task.Title)}」";
            foreach (var d in ShortcutCatalog.All.Where(d => d.Scope == ShortcutScope.Task))
            {
                bool usable = d.Id switch
                {
                    "task.assign" or "task.assignMe" => team,
                    "task.plan" => team,
                    "task.predecessor" => team && target.Task.IsPlanned && !target.IsMultiple,
                    "task.openInBrowser" or "task.copyUrl" => target.Task.Url is not null && !target.IsMultiple,
                    "task.copyKey" => target.Tasks.Any(t => t.Number > 0),
                    "task.branch" => !target.IsMultiple && !target.Task.IsLocal,
                    "task.detail" or "task.rename" => !target.IsMultiple,
                    "task.addChild" => !target.IsMultiple && TaskShortcuts.CanHaveChildren(target.Task),
                    "task.progress" => target.Tasks.Any(t => !t.IsDone),
                    _ => true,
                };
                if (usable)
                {
                    entries.Add(new Entry(d.Id, new PickerOption(d.Label, keymap.Display(d.Id), Group: $"{subject}への操作"),
                        () => TaskShortcuts.RunAsync(d.Id, target)));
                }
            }
        }

        // 画面に固有の操作（ビューの切り替え、ガントの操作など）
        if (window.CurrentPage is ICommandSource source)
        {
            foreach (var c in source.Commands())
            {
                entries.Add(new Entry("page:" + c.Label, new PickerOption(c.Label, c.Keys, Group: c.Group), c.Run));
            }
        }

        // 画面へ移る
        entries.Add(new Entry("nav:my", new PickerOption("マイタスク", Glyph: "", Group: "画面"), () =>
        {
            window.GoToMyTasks();
            return Task.CompletedTask;
        }));
        var projects = (await services.Store.GetProjectsAsync()).Where(p => !p.Closed && p.Kind != ProjectKind.Inbox).ToList();
        foreach (var p in projects.OrderBy(p => p.IsTeam).ThenBy(p => p.Title, StringComparer.CurrentCulture))
        {
            entries.Add(new Entry("nav:" + p.Id, new PickerOption(p.Title, p.IsTeam ? "チーム" : "個人", p.IsTeam ? "" : "", Group: "画面"), () =>
            {
                window.OpenProject(p.Id);
                return Task.CompletedTask;
            }));
        }

        foreach (var (tab, label) in ((string, string)[])[("general", "設定: 全般"), ("personal", "設定: 個人のプロジェクトの既定"), ("team", "設定: チームのプロジェクトの既定")])
        {
            entries.Add(new Entry("settings:" + tab, new PickerOption(label, Glyph: "", Group: "画面"), () =>
            {
                window.OpenSettings(tab);
                return Task.CompletedTask;
            }));
        }

        // タスク（名前か番号（例: TLB-123）で探す。文字を入れたときだけ出す）
        var byProject = projects.ToDictionary(p => p.Id);
        foreach (var t in (await services.Store.GetTasksAsync()).Where(t => !t.IsDone).OrderByDescending(t => t.UpdatedAt).Take(500))
        {
            var where = byProject.TryGetValue(t.ProjectId, out var p) ? p.Title : ProjectDisplay.DefaultPersonalName;
            entries.Add(new Entry("task:" + t.ItemId, new PickerOption(t.Number > 0 ? $"{TaskKeys.Of(t)} {t.Title}" : t.Title, where,
                StatusVisuals.Glyph(t.Category), GlyphBrushKey: StatusVisuals.BrushKey(t.Category), Group: "タスク", OnlyWhenSearching: true), () =>
            {
                App.Current.Shell?.SelectTask(t);
                return Task.CompletedTask;
            }));
        }

        // 何も入力していないときは、最近使った操作を先頭に並べる
        var recent = Recent.Select(k => entries.FirstOrDefault(e => e.Key == k)).OfType<Entry>().Take(5)
            .Select(e => e with { Option = e.Option with { Group = "最近使った操作" } })
            .ToList();
        var ordered = recent.Concat(entries).ToList();

        var picker = new QuickPicker("コマンドパレット", [.. ordered.Select(e => e.Option)],
            input: "操作・画面・タスク（名前か番号）を探す", filterable: true, width: 480, search: true);
        double left = Math.Max(16, (root.ActualWidth - 480) / 2);
        if (await picker.ShowAsync(root, new Point(left, 48)) is { Index: >= 0 } r)
        {
            var chosen = ordered[r.Index];
            Recent.Remove(chosen.Key);
            if (!chosen.Key.StartsWith("task:", StringComparison.Ordinal))
            {
                Recent.Insert(0, chosen.Key);
            }

            await chosen.Run();
        }
    }

    private static string Shorten(string text) => text.Length > 16 ? text[..16] + "…" : text;
}
