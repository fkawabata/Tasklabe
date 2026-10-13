using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Tasklabe.App.Controls;
using Tasklabe.App.Services;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Settings;
using Tasklabe.Data;
using Tasklabe.GitHub;

namespace Tasklabe.App.Views.Dialogs;

/// <summary>
/// プロジェクトの作成（要件 F-PRJ-01、02）。個人プロジェクトは個人用リポジトリを使い、
/// チームプロジェクトは Organization と、その既存のリポジトリ（または新規作成するリポジトリ）を選ぶ。
/// </summary>
public static partial class NewProjectDialog
{
    private const string NewRepositoryTag = "new";

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex RepositoryNamePattern();

    /// <returns>作成したプロジェクト。キャンセルした場合は null。</returns>
    public static async Task<Project?> ShowAsync(XamlRoot root)
    {
        var services = App.Current.Services;
        var organizations = await services.Store.GetOrganizationsAsync();
        var usableOrgs = organizations.Where(o => o.IsAccessible && o.CanCreateProjects).ToList();
        Project? created = null;

        var caption = AppResources.Style("Text.Caption");
        var name = new TextBox { Header = Texts.Get("Project_NameField"), PlaceholderText = Texts.Get("NewProject_NamePlaceholder") };

        // 候補から選ぶ項目は、ドロップダウンではなくピッカーで選ぶ（UX 規約 UX-02）
        bool team = false;
        Organization? org = null;
        var repositories = new List<(string Label, object? Tag)>();
        int repoIndex = -1;
        var kind = PickerButton.ForChoices(Texts.Get("NewProject_Kind"), [Texts.Get("Common_Personal"), Texts.Get("Common_Team")], 0, _ => { },
            [Texts.Get("NewProject_PersonalTip"), usableOrgs.Count > 0 ? Texts.Get("NewProject_TeamTip") : Texts.Get("NewProject_NoOrganizations")]);
        var orgButton = PickerButton.ForChoices("Organization", [.. usableOrgs.Select(o => o.DisplayName)], -1, _ => { });
        var repoButton = PickerButton.ForChoices(Texts.Get("NewProject_Repository"), [], -1, _ => { });
        repoButton.IsEnabled = false;

        var orgHint = new TextBlock { Style = caption, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var orgLink = new HyperlinkButton { Padding = new Thickness(0), Visibility = Visibility.Collapsed };
        var repoName = new TextBox { Header = Texts.Get("NewProject_RepositoryName"), PlaceholderText = Texts.Get("NewProject_RepositoryNamePlaceholder"), Visibility = Visibility.Collapsed };
        var repoLoading = new ProgressRing { Width = 16, Height = 16, IsActive = false, HorizontalAlignment = HorizontalAlignment.Left };
        // 外部アプリの利用を制限している組織は、所属自体が Tasklabe に見えないことがある
        var hiddenHint = new TextBlock
        {
            Style = caption,
            TextWrapping = TextWrapping.Wrap,
            Text = Texts.Get("NewProject_HiddenOrganizations"),
        };
        var hiddenLink = new HyperlinkButton
        {
            Padding = new Thickness(0),
            Content = Texts.Get("NewProject_ReviewAccess"),
            NavigateUri = ApplicationSettingsUrl,
        };
        var teamPanel = new StackPanel
        {
            Spacing = 12,
            Visibility = Visibility.Collapsed,
            Children =
            {
                PickerButton.Labeled("Organization", orgButton), orgHint, orgLink,
                PickerButton.Labeled(Texts.Get("NewProject_Repository"), repoButton), repoLoading, repoName, hiddenHint, hiddenLink,
            },
        };

        var problems = organizations.Where(o => !(o.IsAccessible && o.CanCreateProjects)).ToList();
        if (problems.Count > 0)
        {
            orgHint.Text = Texts.Format("NewProject_UnavailableOrganizations", string.Join(" ", problems.Select(p => Texts.Format("NewProject_OrganizationProblem", p.Login, ProblemLabel(p), ProblemAdvice(p)))));
            orgHint.Visibility = Visibility.Visible;
            if (problems.FirstOrDefault(p => ActionUrl(p) is not null) is { } first)
            {
                orgLink.Content = ActionLabel(first);
                orgLink.NavigateUri = ActionUrl(first);
                orgLink.Visibility = Visibility.Visible;
            }
        }

        // 設定の引き継ぎ元（要件 F-SET-04）: 既定、または既存のプロジェクトのステータスと設定を引き継ぐ
        var existing = (await services.Store.GetProjectsAsync()).Where(p => !p.Closed)
            .OrderBy(p => p.Kind == ProjectKind.Inbox ? 0 : p.IsTeam ? 2 : 1)
            .ThenBy(p => p.Title, StringComparer.CurrentCulture)
            .ToList();
        Project? source = null;
        var sourceButton = PickerButton.ForChoices(Texts.Get("NewProject_Source"), [], 0, i => source = i == 0 ? null : existing[i - 1]);
        var sourceHint = new TextBlock
        {
            Style = caption,
            TextWrapping = TextWrapping.Wrap,
            Text = Texts.Get("NewProject_SourceDescription"),
        };

        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false };

        // 作成できないあいだは、その理由をその場に示す（UX 規約 UX-27）
        var reason = new TextBlock { Style = caption, TextWrapping = TextWrapping.Wrap, Foreground = ThemeResources.Brush("Viz.Delay") };

        var dialog = AppDialog.Create(root, Texts.Get("NewProject_Title"), new ScrollViewer
        {
            MaxHeight = 520,
            Content = new StackPanel
            {
                Spacing = 16,
                Width = 440,
                Children =
                {
                    name,
                    PickerButton.Labeled(Texts.Get("NewProject_Kind"), kind),
                    teamPanel,
                    new StackPanel { Spacing = 4, Children = { PickerButton.Labeled(Texts.Get("NewProject_Source"), sourceButton), sourceHint } },
                    reason,
                    error,
                },
            },
        }, Texts.Get("Common_Create"));
        dialog.IsPrimaryButtonEnabled = false;
        dialog.Opened += (_, _) => name.Focus(FocusState.Programmatic);

        void FillSources()
        {
            // 引き継ぎ元の候補は種類によって既定の名前が変わる。選んでいたプロジェクトは選んだままにする
            var labels = new List<string> { team ? Texts.Get("NewProject_TeamDefaults") : Texts.Get("NewProject_PersonalDefaults") };
            labels.AddRange(existing.Select(p => ProjectDisplay.Name(p)));
            var details = new List<string?> { null };
            details.AddRange(existing.Select(p => (string?)(p.IsTeam ? Texts.Get("Common_Team") : Texts.Get("Common_Personal"))));
            sourceButton.SetChoices(labels, source is null ? 0 : existing.IndexOf(source) + 1, details);
        }

        FillSources();
        bool IsNewRepository() => repoIndex >= 0 && repositories[repoIndex].Tag as string == NewRepositoryTag;

        void Validate()
        {
            string? problem = string.IsNullOrWhiteSpace(name.Text) ? Texts.Get("NewProject_NeedName")
                : team && org is null ? Texts.Get("NewProject_NeedOrganization")
                : team && repoIndex < 0 ? Texts.Get("NewProject_NeedRepository")
                : team && IsNewRepository() && !RepositoryNamePattern().IsMatch(repoName.Text.Trim()) ? Texts.Get("NewProject_InvalidRepositoryName")
                : null;
            reason.Text = problem is null ? "" : "⚠ " + problem;
            reason.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
            dialog.IsPrimaryButtonEnabled = problem is null;
        }

        name.TextChanged += (_, _) => Validate();
        repoName.TextChanged += (_, _) => Validate();
        Validate();
        kind.Pick = async b =>
        {
            if (await ValuePickers.ChoiceAsync(b, Texts.Get("NewProject_Kind"), [Texts.Get("Common_Personal"), Texts.Get("Common_Team")], team ? 1 : 0,
                [Texts.Get("NewProject_PersonalTip"), Texts.Get("NewProject_TeamTip")]) is { } index)
            {
                if (index == 1 && usableOrgs.Count == 0)
                {
                    error.Message = Texts.Format("Common_TwoSentences", Texts.Get("NewProject_NoTeamOrganizations"), orgHint.Text);
                    error.IsOpen = true;
                    return;
                }

                team = index == 1;
                kind.SelectedIndex = index;
                teamPanel.Visibility = team ? Visibility.Visible : Visibility.Collapsed;
                FillSources();
                Validate();
            }
        };

        orgButton.Pick = async b =>
        {
            if (await ValuePickers.ChoiceAsync(b, "Organization", [.. usableOrgs.Select(o => o.DisplayName)], org is null ? -1 : usableOrgs.IndexOf(org)) is not { } index)
            {
                return;
            }

            org = usableOrgs[index];
            orgButton.SelectedIndex = index;
            repositories.Clear();
            repoIndex = -1;
            repoButton.SetChoices([], -1);
            repoButton.IsEnabled = false;
            Validate();

            repoLoading.IsActive = true;
            try
            {
                var repos = await services.Api.ListOrganizationRepositoriesAsync(org.Login);
                if (org.CanCreateRepositories)
                {
                    repositories.Add((Texts.Get("NewProject_NewRepository"), NewRepositoryTag));
                }

                repositories.AddRange(repos.Where(r => r.CanWrite).Select(r => (r.NameWithOwner, (object?)r)));
                repoIndex = repositories.Count == 0 ? -1 : repos.Any(r => r.CanWrite) && org.CanCreateRepositories ? 1 : 0;
                repoButton.SetChoices([.. repositories.Select(r => r.Label)], repoIndex);
                repoButton.IsEnabled = repositories.Count > 0;
                repoName.Visibility = IsNewRepository() ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (GitHubException ex)
            {
                error.Message = SyncEngine.Describe(ex);
                error.IsOpen = true;
            }
            finally
            {
                repoLoading.IsActive = false;
                Validate();
            }
        };

        repoButton.Pick = async b =>
        {
            if (await ValuePickers.ChoiceAsync(b, Texts.Get("NewProject_Repository"), [.. repositories.Select(r => r.Label)], repoIndex) is { } index)
            {
                repoIndex = index;
                repoButton.SelectedIndex = index;
                repoName.Visibility = IsNewRepository() ? Visibility.Visible : Visibility.Collapsed;
                Validate();
            }
        };

        dialog.PrimaryButtonClick += async (d, args) =>
        {
            var deferral = args.GetDeferral();
            d.IsPrimaryButtonEnabled = false;
            error.IsOpen = false;
            try
            {
                var title = name.Text.Trim();
                var setup = Setup(team, source);

                // 番号のキーは、名前から提案したものを、ほかのプロジェクトや既存のリポジトリの自動リンクと重ならないように決めて保存する（要件 F-TSK-15）
                var existing = team ? (repositories[repoIndex].Tag as OrganizationRepository)?.NameWithOwner : services.PersonalRepository?.NameWithOwner;
                var repository = existing ?? (team && IsNewRepository() ? repoName.Text.Trim() : null);
                IEnumerable<string> taken = ProjectKey.Resolve(await services.Store.GetProjectsAsync()).Values;
                try
                {
                    taken = taken.Concat(await services.Workspace.ForeignAutolinkKeysAsync(existing));
                }
                catch (GitHubException)
                {
                    // 自動リンクを確かめられなくても、プロジェクトは作れる
                }

                setup = setup with { Settings = setup.Settings with { Key = ProjectKey.Suggest(title, repository, taken) } };
                created = team
                    ? await services.Workspace.CreateTeamProjectAsync(
                        org!,
                        repositories[repoIndex].Tag as OrganizationRepository,
                        IsNewRepository() ? repoName.Text.Trim() : null,
                        title,
                        setup)
                    : await services.Workspace.CreatePersonalProjectAsync(services.User!, services.PersonalRepository!, title, setup);
            }
            catch (GitHubException ex)
            {
                error.Message = SyncEngine.Describe(ex);
                error.IsOpen = true;
                args.Cancel = true;
                d.IsPrimaryButtonEnabled = true;
            }
            finally
            {
                deferral.Complete();
            }
        };

        AutomationProperties.SetName(repoName, Texts.Get("NewProject_RepositoryName"));

        await dialog.ShowAsync();
        return created;
    }

    /// <summary>
    /// 新しいプロジェクトに持たせるステータスと設定（要件 F-SET-04）。
    /// チームのプロジェクトには、メンバーで同じ設定を使えるよう、決まった値をすべて書き込む。
    /// 個人のプロジェクトは、引き継ぎ元が上書きしていた項目だけを書き込み、ほかは個人の既定に従わせる。
    /// </summary>
    private static ProjectSetup Setup(bool team, Project? source)
    {
        var defaults = ProjectPreferences.Defaults(team);
        if (source is null)
        {
            return new ProjectSetup(defaults.Statuses, team ? ProjectSettings.From(defaults.Resolved) : ProjectSettings.None);
        }

        IReadOnlyList<StatusTemplate> statuses = [.. source.StatusOptions.Select(o => new StatusTemplate(o.Name, o.Color, o.Category))];
        if (!StatusTemplates.IsUsable(statuses))
        {
            statuses = defaults.Statuses;
        }

        if (team)
        {
            // 個人のプロジェクトから引き継ぐときは、区分の既定だけはチームの既定にする（個人は課題を扱わないため）
            var resolved = ProjectPreferences.Resolve(source);
            return new ProjectSetup(statuses, ProjectSettings.From(source.IsTeam ? resolved : resolved with { NewTaskKind = defaults.NewTaskKind }));
        }

        return new ProjectSetup(statuses, source.Settings with { NewTaskKind = null, Key = null });
    }

    private static string ProblemLabel(Organization org) => org.Problem switch
    {
        OrganizationAccessProblem.OAuthAppNotApproved => Texts.Get("Org_NotApproved"),
        OrganizationAccessProblem.SamlSsoRequired => Texts.Get("Org_SsoRequired"),
        OrganizationAccessProblem.Forbidden => Texts.Get("Org_Forbidden"),
        _ => Texts.Get("Org_CannotCreate"),
    };

    public static string ProblemAdvice(Organization org) => org.Problem switch
    {
        OrganizationAccessProblem.OAuthAppNotApproved => Texts.Get("Org_NotApprovedAdvice"),
        OrganizationAccessProblem.SamlSsoRequired => Texts.Get("Org_SsoRequiredAdvice"),
        OrganizationAccessProblem.Forbidden => Texts.Get("Org_ForbiddenAdvice"),
        _ => Texts.Get("Org_CannotCreateAdvice"),
    };

    /// <summary>GitHub 上の Tasklabe の認可設定ページ。組織へのアクセスをリクエストできる。</summary>
    public static Uri ApplicationSettingsUrl =>
        new($"https://{App.Current.Services.Host.Name}/settings/connections/applications/{App.Current.Services.Host.ClientId}");

    public static Uri? ActionUrl(Organization org) => org.Problem switch
    {
        OrganizationAccessProblem.OAuthAppNotApproved => ApplicationSettingsUrl,
        OrganizationAccessProblem.SamlSsoRequired => new Uri($"https://{App.Current.Services.Host.Name}/orgs/{org.Login}/sso"),
        _ => null,
    };

    public static string ActionLabel(Organization org) => org.Problem switch
    {
        OrganizationAccessProblem.OAuthAppNotApproved => Texts.Format("Org_RequestApproval", org.Login),
        OrganizationAccessProblem.SamlSsoRequired => Texts.Format("Org_AuthorizeSso", org.Login),
        _ => "",
    };
}
