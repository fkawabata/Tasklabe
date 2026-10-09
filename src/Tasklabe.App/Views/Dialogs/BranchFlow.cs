using Microsoft.UI.Xaml;
using Tasklabe.App.Controls;
using Tasklabe.App.Services;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Editing;
using Tasklabe.Data;
using Tasklabe.GitHub;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace Tasklabe.App.Views.Dialogs;

/// <summary>
/// タスクの作業用のブランチを作る、または既存のブランチから選ぶ（要件 F-TSK-19）。
/// リポジトリを決めたら、先頭に「新しいブランチを作る…」、続けてそのリポジトリのブランチを並べたピッカーを開く。
/// </summary>
public static class BranchFlow
{
    /// <param name="repository">ブランチのリポジトリ。null なら、作業するリポジトリが 1 つのときはそれを、ほかは選ばせる。</param>
    public static async Task ShowAsync(FrameworkElement anchor, Point? position, TaskItem task, string? repository = null)
    {
        if (task.IsLocal)
        {
            App.Current.Shell?.ShowInfo("まだブランチを扱えません", "GitHub にタスクを送り終えてから作る・選べます。");
            return;
        }

        if (repository is null)
        {
            if (task.Repositories.Count == 1)
            {
                repository = task.Repositories[0];
            }
            else if (await ValuePickers.RepositoryAsync(anchor, position, task.Repositories, null) is { } picked)
            {
                repository = picked.Value;
            }
            else
            {
                return;
            }
        }

        RepositoryBranches list;
        try
        {
            list = await App.Current.Services.Api.ListBranchesAsync(repository);
        }
        catch (Exception ex) when (ex is GitHubException or HttpRequestException)
        {
            App.Current.Shell?.ShowInfo($"{repository} のブランチを取れませんでした", SyncEngine.Describe(ex));
            return;
        }

        // 空のリポジトリにはブランチを作る元（既定のブランチ）がないため、作る・選ぶの候補を出さずに知らせる
        if (list.IsEmpty)
        {
            App.Current.Shell?.ShowInfo($"{repository} にはまだコミットがありません", "最初のコミットを push すると、ブランチを作れるようになります。");
            return;
        }

        var branches = list.Branches;

        var chosen = task.Branches.Select(TaskValues.SplitBranchEntry)
            .Where(b => string.Equals(b.Repository, repository, StringComparison.OrdinalIgnoreCase))
            .Select(b => b.Branch)
            .Concat((LinkedBranches.Cached(task.IssueId) ?? []).Where(b => string.Equals(b.RepositoryNameWithOwner, repository, StringComparison.OrdinalIgnoreCase)).Select(b => b.Name))
            .ToHashSet(StringComparer.Ordinal);
        var options = new List<PickerOption> { new("新しいブランチを作る…", Glyph: "\uE710") };
        options.AddRange(branches.Select(b => new PickerOption(b, chosen.Contains(b) ? "このタスクのブランチ" : null, Group: "既存のブランチ")));

        var picker = new QuickPicker($"{repository} のブランチ", options, filterable: branches.Count > 0, width: 400,
            input: branches.Count > 0 ? "ブランチ名で絞り込む" : null,
            note: branches.Count == 0 ? $"既定のブランチ（{list.DefaultBranch}）のほかにブランチはありません。" : $"既定のブランチ（{list.DefaultBranch}）は除いて、最近コミットした順に並べています。");
        if (await picker.ShowAsync(anchor, position) is not { Index: >= 0 } result)
        {
            return;
        }

        if (result.Index == 0)
        {
            await CreateBranchDialog.ShowAsync(anchor.XamlRoot, task, repository);
            return;
        }

        var branch = branches[result.Index - 1];
        var changes = new List<TaskChange>();
        if (!task.Branches.Contains(TaskValues.BranchEntry(repository, branch), StringComparer.Ordinal))
        {
            changes.AddRange(TaskRules.Set(task, TaskField.Branches, TaskValues.Branches(task.Branches.Append(TaskValues.BranchEntry(repository, branch)))));
        }

        changes.AddRange(WithRepository(task, repository));
        if (changes.Count > 0)
        {
            await TaskCommands.ApplyAsync([(task, changes)]);
        }

        CopySwitchCommand(branch, $"{branch} を選び、切り替えるコマンドをコピーしました");
    }

    /// <summary>ブランチのあるリポジトリは作業するリポジトリでもあるため、まだなら加える変更。</summary>
    internal static IReadOnlyList<TaskChange> WithRepository(TaskItem task, string repository) =>
        task.Repositories.Contains(repository, StringComparer.OrdinalIgnoreCase)
            ? []
            : TaskRules.Set(task, TaskField.Repositories, TaskValues.Repositories(task.Repositories.Append(repository)));

    /// <summary>手元でそのブランチへ切り替えるコマンドをクリップボードに置き、知らせる。</summary>
    internal static void CopySwitchCommand(string branch, string message)
    {
        var package = new DataPackage();
        package.SetText($"git fetch origin && git switch {branch}");
        Clipboard.SetContent(package);
        App.Current.Shell?.ShowToast(message);
    }
}
