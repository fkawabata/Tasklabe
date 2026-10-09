using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Tasklabe.App.Controls;
using Tasklabe.App.Services;
using Tasklabe.Core.Domain;

namespace Tasklabe.App.Views.Dialogs;

/// <summary>
/// タスクの追加（要件 F-UI-MY-04、UX 規約 UX-08、UX-09、UX-12）。どの画面から始めても同じ形で開き、
/// 始めた場所の文脈を既定値にする。「詳細を入力…」で、入力を引き継いで詳細パネルへ移れる。
/// </summary>
public static class NewTaskDialog
{
    /// <summary>追加先の候補。閉じていないものを、未分類、個人、チームの順に並べる。</summary>
    public static async Task<List<Project>> ProjectsAsync() =>
        (await App.Current.Services.Store.GetProjectsAsync())
            .Where(p => !p.Closed)
            .OrderBy(p => p.Kind == ProjectKind.Inbox ? 0 : p.IsTeam ? 2 : 1)
            .ThenBy(p => p.Title, StringComparer.CurrentCulture)
            .ToList();

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
            // Enter では追加しない（急いでいるときの誤った確定を防ぐ）。既定のボタンは置かず、確定のキーをボタンに添える（開いたときに差し替える）
            PrimaryButtonText = "追加",
            PrimaryButtonStyle = AppResources.Style("AccentButtonStyle"),
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.None,
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
        dialog.Opened += (_, _) =>
        {
            if (VisualTree.Descendants<Button>(dialog).FirstOrDefault(b => b.Name == "PrimaryButton") is { } primary)
            {
                primary.Content = KeyCaps.Content("追加", "composer.submit");
            }

            composer.FocusTitle();
        };

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

            if (await ConfirmDiscard(root, composer.Draft).ShowAsync() == ContentDialogResult.Primary)
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

    /// <summary>入力中のタスクを破棄してよいかを確かめるダイアログ（UX 規約 UX-11、UX-14）。</summary>
    public static ContentDialog ConfirmDiscard(XamlRoot root, TaskDraft draft) => AppDialog.Confirm(root, "入力中のタスクを破棄しますか？",
        $"「{(draft.Title.Trim().Length > 0 ? draft.Title.Trim() : "（タスク名なし）")}」はまだ追加していません。破棄すると入力した内容は戻せません。",
        "破棄");
}
