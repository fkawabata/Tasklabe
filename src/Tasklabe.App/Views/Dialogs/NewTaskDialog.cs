using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Tasklabe.App.Controls;
using Tasklabe.Core.Domain;

namespace Tasklabe.App.Views.Dialogs;

/// <summary>
/// タスクの追加（要件 F-UI-MY-04、UX 規約 UX-08、UX-09、UX-12）。どの画面から始めても同じ形で開き、
/// 始めた場所の文脈を既定値にする。「詳細を入力…」で、入力を引き継いで詳細パネルへ移れる。
/// </summary>
public static class NewTaskDialog
{
    /// <param name="projects">追加先の候補。未分類、個人、チームの順に並べたもの。</param>
    /// <param name="context">始めた場所の文脈（既定値と、表示の条件）。</param>
    public static async Task ShowAsync(XamlRoot root, IReadOnlyList<Project> projects, NewTaskContext context)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(context);

        if (projects.Count == 0)
        {
            return;
        }

        var composer = new NewTaskComposer();
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Content = composer,
            PrimaryButtonText = "追加",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };

        composer.CanCreateChanged += (_, _) => dialog.IsPrimaryButtonEnabled = composer.CanCreate;

        // Ctrl + Enter など、キー操作で追加する（説明の欄で改行したいときの Enter と区別する）
        bool submitted = false;
        bool toDetail = false;
        composer.SubmitRequested += (_, _) =>
        {
            if (composer.CanCreate)
            {
                submitted = true;
                dialog.Hide();
            }
        };
        composer.DetailRequested += (_, _) =>
        {
            toDetail = true;
            dialog.Hide();
        };
        dialog.Opened += (_, _) => composer.FocusTitle();

        composer.Load(projects, context);

        while (true)
        {
            var result = await dialog.ShowAsync();
            if (toDetail)
            {
                // 入力を引き継いで、詳細パネルで作成を続ける（この時点ではまだ作らない）
                App.Current.Shell?.StartCreating(composer.Draft);
                return;
            }

            if (submitted || result == ContentDialogResult.Primary)
            {
                break;
            }

            // 入力があれば、破棄してよいかを確かめる。やめたら入力を保ったまま開き直す（UX 規約 UX-11、UX-14）
            if (!composer.Draft.HasContent)
            {
                return;
            }

            var confirm = AppDialog.Confirm(root, "入力中のタスクを破棄しますか？",
                $"「{(composer.Draft.Title.Trim().Length > 0 ? composer.Draft.Title.Trim() : "（タスク名なし）")}」はまだ追加していません。破棄すると入力した内容は戻せません。",
                "破棄");
            if (await confirm.ShowAsync() == ContentDialogResult.Primary)
            {
                return;
            }
        }

        if (!composer.CanCreate)
        {
            return;
        }

        var created = await composer.Draft.CreateAsync();

        // 作ったものが、いまの表示の条件に合わず見えないときは知らせる（UX-12）
        if (context.IsShown is { } isShown && !isShown(created))
        {
            App.Current.Shell?.ShowToast($"「{created.Title}」を追加しました。表示の条件に合わないため、一覧には表示されません", "表示する", () =>
            {
                context.ShowAll?.Invoke();
                return Task.CompletedTask;
            });
        }
    }
}
