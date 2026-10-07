using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Tasklabe.App.Controls;
using Tasklabe.App.Services;
using Tasklabe.Core.Domain;
using Tasklabe.Data;
using Tasklabe.GitHub;

namespace Tasklabe.App.Views.Dialogs;

/// <summary>
/// タスクから作業用のブランチを作る（要件 F-TSK-19）。リポジトリの既定のブランチから、Issue に紐づくブランチ（GitHub の Development）を作る。
/// 作ったら、手元で切り替えるコマンドをクリップボードに置く。
/// </summary>
public static class CreateBranchDialog
{
    /// <param name="repository">作るリポジトリ。null なら、作業するリポジトリの先頭から始める。</param>
    public static async Task ShowAsync(XamlRoot root, TaskItem task, string? repository = null)
    {
        if (task.IsLocal)
        {
            App.Current.Shell?.ShowInfo("まだブランチを作れません", "GitHub にタスクを送り終えてから作れます。");
            return;
        }

        var caption = AppResources.Style("Text.Caption");
        repository ??= task.Repositories.FirstOrDefault();
        var repositoryButton = new PickerButton("リポジトリ");
        var name = new TextBox { Text = BranchName.Suggest(TaskKeys.Of(task), task.Title) };
        var error = new TextBlock { Style = caption, Foreground = ThemeResources.Brush("SystemFillColorCriticalBrush"), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(name, "ブランチ名");

        void Refresh() => repositoryButton.SetValue(repository, "", placeholder: "リポジトリを選ぶ…");
        repositoryButton.Pick = async b =>
        {
            if (await ValuePickers.RepositoryAsync(b, null, task.Repositories, repository) is { } picked)
            {
                repository = picked.Value;
                Refresh();
            }
        };
        Refresh();

        var panel = new StackPanel { Spacing = 12, Width = 420 };
        panel.Children.Add(new TextBlock
        {
            Text = $"{TaskKeys.Of(task)} {task.Title}",
            Style = AppResources.Style("Text.BodyStrong"),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(PickerButton.Labeled("リポジトリ", repositoryButton));
        var namePanel = new StackPanel { Spacing = 4 };
        namePanel.Children.Add(new TextBlock { Text = "ブランチ名", Style = caption });
        namePanel.Children.Add(name);
        namePanel.Children.Add(error);
        panel.Children.Add(namePanel);
        panel.Children.Add(new TextBlock
        {
            Text = "リポジトリの既定のブランチから作り、GitHub の Issue の Development に紐づけます。このブランチの PR は Issue に紐づきます。"
                + "作ったら、手元で切り替えるコマンドをコピーします。",
            Style = caption,
            TextWrapping = TextWrapping.Wrap,
        });

        var dialog = AppDialog.Create(root, "ブランチを作る", panel, "作る");
        dialog.Opened += (_, _) =>
        {
            name.Focus(FocusState.Programmatic);
            name.SelectAll();
        };

        LinkedBranch? created = null;
        dialog.PrimaryButtonClick += async (d, args) =>
        {
            var problem = repository is null ? "リポジトリを選んでください。" : BranchName.Problem(name.Text.Trim());
            if (problem is not null)
            {
                error.Text = problem;
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
                return;
            }

            var deferral = args.GetDeferral();
            d.IsPrimaryButtonEnabled = false;
            try
            {
                created = await App.Current.Services.Api.CreateLinkedBranchAsync(task.IssueId, repository!, name.Text.Trim());
            }
            catch (Exception ex) when (ex is GitHubException or HttpRequestException)
            {
                error.Text = SyncEngine.Describe(ex);
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
            finally
            {
                d.IsPrimaryButtonEnabled = true;
                deferral.Complete();
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || created is null)
        {
            return;
        }

        LinkedBranches.Add(task.IssueId, created);

        // ブランチを作ったリポジトリは作業するリポジトリでもあるため、まだなら関連付ける
        if (BranchFlow.WithRepository(task, created.RepositoryNameWithOwner) is { Count: > 0 } changes)
        {
            await TaskCommands.ApplyAsync([(task, changes)]);
        }

        BranchFlow.CopySwitchCommand(created.Name, $"{created.Name} を作り、切り替えるコマンドをコピーしました");
        App.Current.Shell?.NotifyDataChanged();
    }
}
