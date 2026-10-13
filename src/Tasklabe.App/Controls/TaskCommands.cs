using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Tasklabe.App.Services;
using Tasklabe.App.Views.Dialogs;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Editing;
using Tasklabe.Core.Scheduling;
using Tasklabe.Core.Wbs;

namespace Tasklabe.App.Controls;

/// <summary>一覧・表・カンバン・詳細パネルで共通のタスク操作。</summary>
public static class TaskCommands
{
    /// <summary>課題を計画へ移す（要件 F-TSK-09）。置き場所と日程はダイアログで決める。複数のときは置き場所だけを決める。</summary>
    public static async Task PromoteAsync(XamlRoot root, IReadOnlyList<TaskItem> tasks)
    {
        if (tasks.Count == 0)
        {
            return;
        }

        var all = await App.Current.Services.Store.GetTasksAsync(tasks[0].ProjectId);
        var plan = TaskTree.Build(all.Where(t => t.IsPlanned));
        await PromoteToPlanDialog.ShowAsync(root, tasks, plan);
    }

    /// <inheritdoc cref="PromoteAsync(XamlRoot, IReadOnlyList{TaskItem})"/>
    public static Task PromoteAsync(XamlRoot root, TaskItem task) => PromoteAsync(root, [task]);

    /// <summary>計画のタスクを課題へ戻す（要件 F-TSK-10）。</summary>
    public static Task DemoteAsync(TaskItem task) => DemoteAsync([task]);

    /// <inheritdoc cref="DemoteAsync(TaskItem)"/>
    public static Task DemoteAsync(IReadOnlyList<TaskItem> tasks) =>
        ApplyAsync([.. tasks.Select(t => (t, TaskRules.DemoteToIssue(t)))],
            tasks.Count == 1 ? Texts.Format("Toast_MovedToTriageOne", tasks[0].Title) : Texts.Format("Toast_MovedToTriageMany", tasks.Count));

    /// <summary>
    /// 親タスクの配下を、予定の順に依存関係でつなぐ（要件 F-DEP-08）。張る前に、つなぐ組み合わせを一覧で確かめる。
    /// </summary>
    public static async Task ChainChildrenAsync(XamlRoot root, TaskItem parent)
    {
        var tasks = await App.Current.Services.Store.GetTasksAsync(parent.ProjectId);
        var tree = TaskTree.Build(tasks.Where(t => t.IsPlanned));
        var links = DependencyPlanner.ChainChildren(tree, parent);
        if (links.Count == 0)
        {
            App.Current.Shell?.ShowInfo(Texts.Get("Chain_NothingTitle"), Texts.Get("Chain_NothingMessage"));
            return;
        }

        var list = new StackPanel { Spacing = 4 };
        list.Children.Add(new TextBlock
        {
            Text = Texts.Format("Chain_Message", parent.Title),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        foreach (var link in links)
        {
            list.Children.Add(new TextBlock
            {
                Text = $"{link.Predecessor.Title}　→　{link.Successor.Title}",
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        var dialog = AppDialog.Create(root, Texts.Count("Chain_Title", links.Count), new ScrollViewer { Content = list, MaxHeight = 420 }, Texts.Get("Common_Add"));
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        // 後続ごとにまとめて先行を足す（1 回の操作として元に戻せるようにする）
        var edits = links.GroupBy(l => l.Successor.ItemId)
            .Select(group =>
            {
                var successor = tasks.First(t => t.ItemId == group.Key);
                var ids = successor.BlockedBy.Concat(group.Select(l => l.Predecessor.IssueId)).Distinct(StringComparer.Ordinal);
                return (successor, TaskRules.Set(successor, TaskField.BlockedBy, TaskValues.IssueIds(ids)));
            })
            .ToList();
        await ApplyAsync(edits, Texts.Count("Chain_Done", links.Count));
    }

    /// <summary>取り消せない削除を、安全な側（キャンセル）を既定にして確かめてから行う（UX 規約 UX-11）。</summary>
    /// <param name="returnFocusTo">確認を閉じた直後にフォーカスを戻す先（キーボードで続けて操作できるように）。</param>
    public static async Task<bool> ConfirmAndDeleteAsync(XamlRoot root, IReadOnlyList<TaskItem> tasks, Control? returnFocusTo = null)
    {
        if (tasks.Count == 0)
        {
            return false;
        }

        string message;
        if (tasks.Count == 1)
        {
            var task = tasks[0];
            message = task.IsLocal
                ? Texts.Format("Delete_LocalMessage", task.Title)
                : Texts.Format("Delete_Message", task.Title, TaskKeys.Of(task));
        }
        else
        {
            var names = string.Join("\n", tasks.Take(5).Select(t => Texts.Format("Common_Bullet", t.Title)));
            var more = tasks.Count > 5 ? "\n" + Texts.Format("Delete_More", tasks.Count - 5) : "";
            message = Texts.Format("Delete_ManyMessage", tasks.Count) + "\n" + names + more;
        }

        // 確認のあいだに GitHub への作成が済むと、タスクの ID が付け替わる。付け替わった先を追い、押した時点のタスクを消す
        var replaced = new Dictionary<string, string>(StringComparer.Ordinal);
        void OnReplaced(object? sender, (string OldItemId, string NewItemId) ids)
        {
            lock (replaced)
            {
                replaced[ids.OldItemId] = ids.NewItemId;
            }
        }

        var store = App.Current.Services.Store;
        store.ItemIdReplaced += OnReplaced;
        ContentDialogResult result;
        try
        {
            var dialog = AppDialog.Confirm(root, tasks.Count == 1 ? Texts.Get("Delete_Title") : Texts.Format("Delete_ManyTitle", tasks.Count), message, Texts.Get("Common_Delete"));
            result = await dialog.ShowAsync();
        }
        finally
        {
            store.ItemIdReplaced -= OnReplaced;
        }

        // 削除した行の位置を一覧が覚えられるよう、再表示の前に元の行へフォーカスを戻す
        returnFocusTo?.Focus(FocusState.Programmatic);
        if (result != ContentDialogResult.Primary)
        {
            return false;
        }

        foreach (var shown in tasks)
        {
            string itemId = shown.ItemId;
            lock (replaced)
            {
                while (replaced.TryGetValue(itemId, out var next))
                {
                    itemId = next;
                }
            }

            var task = await store.GetTaskAsync(itemId) ?? shown;
            if (App.Current.Shell?.SelectedTask?.ItemId is { } selected && (selected == shown.ItemId || selected == task.ItemId))
            {
                App.Current.Shell.SelectTask(null);
            }

            await App.Current.Services.Edits.DeleteAsync(task);
        }

        return true;
    }

    /// <inheritdoc cref="ConfirmAndDeleteAsync(XamlRoot, IReadOnlyList{TaskItem}, Control?)"/>
    public static Task<bool> ConfirmAndDeleteAsync(XamlRoot root, TaskItem task, Control? returnFocusTo = null) =>
        ConfirmAndDeleteAsync(root, [task], returnFocusTo);

    /// <summary>
    /// 別のプロジェクトへ移す（要件 F-TSK-03）。画面の外へ影響が及ぶため、下端の知らせに「元に戻す」を置く（UX 規約 UX-26）。
    /// </summary>
    /// <returns>移した後のタスク。</returns>
    public static async Task<IReadOnlyList<TaskItem>> MoveAsync(IReadOnlyList<TaskItem> tasks, Project target)
    {
        var services = App.Current.Services;
        var moved = new List<(TaskItem Before, TaskItem After)>();
        foreach (var task in tasks.Where(t => t.ProjectId != target.Id))
        {
            if (await services.Edits.MoveAsync(task, target) is { } after)
            {
                moved.Add((task, after));
            }
        }

        if (moved.Count == 0)
        {
            return [];
        }

        var name = Services.ProjectDisplay.Name(target);
        App.Current.Shell?.ShowUndoable(
            moved.Count == 1 ? Texts.Format("Toast_MovedOne", moved[0].Before.Title, name) : Texts.Format("Toast_MovedMany", moved.Count, name),
            () => UndoMoveAsync(moved));
        return [.. moved.Select(m => m.After)];
    }

    /// <summary>移したタスクを元のプロジェクトへ戻し、移すときに変わった区分・置き場所・ステータス・担当者も戻す。</summary>
    private static async Task UndoMoveAsync(IReadOnlyList<(TaskItem Before, TaskItem After)> moved)
    {
        var services = App.Current.Services;
        var edits = new List<(TaskItem, IReadOnlyList<TaskChange>)>();
        foreach (var (before, after) in moved)
        {
            if (await services.Store.GetProjectAsync(before.ProjectId) is not { } origin
                || await services.Store.GetTaskAsync(after.ItemId) is not { } current
                || await services.Edits.MoveAsync(current, origin) is not { } back)
            {
                continue;
            }

            // 移す前の値へそのまま戻す（ステータスに伴う実績日などは、移す前の値に含まれている）
            var changes = new List<TaskChange>();
            foreach (var field in (TaskField[])[TaskField.Status, TaskField.Kind, TaskField.Parent, TaskField.SortOrder, TaskField.Assignees])
            {
                var now = TaskValues.Get(back, field);
                var original = TaskValues.Get(before, field);
                if (!string.Equals(now, original, StringComparison.Ordinal))
                {
                    changes.Add(new TaskChange(field, now, original));
                }
            }

            edits.Add((back, changes));
        }

        await ApplyAsync(edits);
    }

    /// <summary>
    /// 複数のタスクへの変更を 1 回の操作として反映する（1 回の Ctrl + Z で全件を戻せる。UX 規約 UX-07）。
    /// </summary>
    /// <param name="message">下端に出す知らせ。null なら出さない。出すときは「元に戻す」を添える（UX-26）。</param>
    public static async Task ApplyAsync(IReadOnlyList<(TaskItem Task, IReadOnlyList<TaskChange> Changes)> edits, string? message = null)
    {
        var effective = edits.Where(e => e.Changes.Count > 0).ToList();
        if (effective.Count == 0)
        {
            return;
        }

        var services = App.Current.Services;
        if (effective.Count == 1)
        {
            await services.Edits.ApplyChangesAsync(effective[0].Task, effective[0].Changes);
        }
        else
        {
            await services.Edits.ApplyManyAsync(effective);
        }

        if (message is not null)
        {
            App.Current.Shell?.ShowUndoable(message, () => RevertAsync(effective));
        }
    }

    /// <summary>
    /// 反映した変更を打ち消す。いまの値が変更後のままの項目だけを戻し、その後に変わった項目は上書きしない。
    /// </summary>
    private static async Task RevertAsync(IReadOnlyList<(TaskItem Task, IReadOnlyList<TaskChange> Changes)> edits)
    {
        var services = App.Current.Services;
        var inverse = new List<(TaskItem, IReadOnlyList<TaskChange>)>();
        foreach (var (task, changes) in edits)
        {
            if (await services.Store.GetTaskAsync(task.ItemId) is { } current)
            {
                inverse.Add((current, EditHistory.Inverse(current, changes)));
            }
        }

        await ApplyAsync(inverse);
    }
}
