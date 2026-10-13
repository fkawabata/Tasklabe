using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Tasklabe.App.Controls;
using Tasklabe.App.Services;
using Tasklabe.App.ViewModels;
using Tasklabe.Core.Domain;
using Tasklabe.GitHub;

namespace Tasklabe.App.Views;

/// <summary>
/// プロジェクトの設定（要件 F-SET-02、03）。全体の設定にある既定を、このプロジェクトだけ上書きする。
/// 上書きは GitHub の Project に保存し、チームのメンバーで共有する。
/// </summary>
public sealed partial class ProjectSettingsPage : Page
{
    private Project? _project;

    public ProjectSettingsPage()
    {
        InitializeComponent();
        // 番号のキーも変わりうるため、プロジェクトから読み直す
        Editor.Changed += (_, _) => _ = App.Current.Shell?.ReloadAsync();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not ProjectNavItem item || await App.Current.Services.Store.GetProjectAsync(item.Id) is not { } project)
        {
            return;
        }

        _project = project;
        TitleText.Text = Texts.Format("ProjectSettings_Title", ProjectDisplay.Name(project));
        KindIcon.Glyph = project.IsTeam ? "\uE716" : "\uE77B";
        KindText.Text = project.IsTeam ? Texts.Get("Common_Team") : Texts.Get("Common_Personal");
        IntroText.Text = project.IsTeam
            ? Texts.Get("ProjectSettings_TeamIntro")
            : Texts.Get("ProjectSettings_PersonalIntro");
        DefaultsLink.Content = project.IsTeam ? Texts.Get("ProjectSettings_OpenTeamDefaults") : Texts.Get("ProjectSettings_OpenPersonalDefaults");

        // チームのプロジェクトは、Project を編集できる人だけが設定を変えられる
        bool canEdit = true;
        AccessChip.Hide();
        if (project.IsTeam)
        {
            try
            {
                var access = await App.Current.Services.Api.GetProjectAccessAsync(project.Id);
                canEdit = access.CanUpdateProject;
            }
            catch (GitHubException)
            {
                // 確かめられないときは編集を許し、保存に失敗したら理由を示す
            }

            if (!canEdit)
            {
                var readOnly = Texts.Get("ProjectSettings_ReadOnly");
                AccessChip.Show("\uE72E", readOnly, readOnly, new ChipSection(ChipSeverity.Caution, readOnly, Texts.Get("ProjectSettings_ReadOnlyMessage"), [], []));
            }
        }

        await Editor.LoadProjectAsync(project, canEdit);
    }

    private void OnBackToProject(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
        else if (_project is not null)
        {
            App.Current.MainWindow!.OpenProject(_project.Id);
        }
    }

    private void OnOpenDefaults(object sender, RoutedEventArgs e)
    {
        if (_project is not null)
        {
            App.Current.MainWindow!.OpenSettings(_project.IsTeam ? "team" : "personal");
        }
    }

    /// <summary>
    /// 中身の幅を、表示できる幅（最大 1000）に合わせる。ScrollViewer は中身を横方向に制限なく測るため、
    /// 幅を決めておかないと、中身によって位置と幅が変わってしまう。
    /// </summary>
    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e) =>
        RootPanel.Width = Math.Min(1000, Math.Max(0, e.NewSize.Width));
}
