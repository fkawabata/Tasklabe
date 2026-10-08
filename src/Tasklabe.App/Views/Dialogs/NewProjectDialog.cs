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
        var name = new TextBox { Header = "プロジェクト名", PlaceholderText = "例: Web サイトのリニューアル" };

        // 候補から選ぶ項目は、ドロップダウンではなくピッカーで選ぶ（UX 規約 UX-02）
        bool team = false;
        Organization? org = null;
        var repositories = new List<(string Label, object? Tag)>();
        int repoIndex = -1;
        var kind = PickerButton.ForChoices("種類", ["個人", "チーム"], 0, _ => { },
            ["自分だけが使うプロジェクト", usableOrgs.Count > 0 ? "Organization で共有するプロジェクト" : "使える Organization がありません"]);
        var orgButton = PickerButton.ForChoices("Organization", [.. usableOrgs.Select(o => o.DisplayName)], -1, _ => { });
        var repoButton = PickerButton.ForChoices("タスクを作成するリポジトリ", [], -1, _ => { });
        repoButton.IsEnabled = false;

        var orgHint = new TextBlock { Style = caption, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var orgLink = new HyperlinkButton { Padding = new Thickness(0), Visibility = Visibility.Collapsed };
        var repoName = new TextBox { Header = "新しいリポジトリの名前", PlaceholderText = "例: web-renewal", Visibility = Visibility.Collapsed };
        var repoLoading = new ProgressRing { Width = 16, Height = 16, IsActive = false, HorizontalAlignment = HorizontalAlignment.Left };
        // 外部アプリの利用を制限している組織は、所属自体が Tasklabe に見えないことがある
        var hiddenHint = new TextBlock
        {
            Style = caption,
            TextWrapping = TextWrapping.Wrap,
            Text = "所属している Organization が一覧にない場合は、その組織が外部アプリの利用を制限している可能性があります。"
                + "GitHub の Tasklabe の設定ページから、組織へのアクセスをリクエストしてください。",
        };
        var hiddenLink = new HyperlinkButton
        {
            Padding = new Thickness(0),
            Content = "GitHub で Tasklabe の組織アクセスを確認する",
            NavigateUri = ApplicationSettingsUrl,
        };
        var teamPanel = new StackPanel
        {
            Spacing = 12,
            Visibility = Visibility.Collapsed,
            Children =
            {
                PickerButton.Labeled("Organization", orgButton), orgHint, orgLink,
                PickerButton.Labeled("タスクを作成するリポジトリ", repoButton), repoLoading, repoName, hiddenHint, hiddenLink,
            },
        };

        var problems = organizations.Where(o => !(o.IsAccessible && o.CanCreateProjects)).ToList();
        if (problems.Count > 0)
        {
            orgHint.Text = "選択できない Organization があります。" + string.Join(" ", problems.Select(p => $"{p.Login}: {ProblemLabel(p)}。{ProblemAdvice(p)}"));
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
        var sourceButton = PickerButton.ForChoices("設定の引き継ぎ元", [], 0, i => source = i == 0 ? null : existing[i - 1]);
        var sourceHint = new TextBlock
        {
            Style = caption,
            TextWrapping = TextWrapping.Wrap,
            Text = "ステータスの名前・カテゴリ・並びと、工数・稼働日などの設定を引き継ぎます。作った後も、プロジェクトの設定で変えられます。",
        };

        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false };

        // 作成できないあいだは、その理由をその場に示す（UX 規約 UX-27）
        var reason = new TextBlock { Style = caption, TextWrapping = TextWrapping.Wrap, Foreground = ThemeResources.Brush("Viz.Delay") };

        var dialog = AppDialog.Create(root, "新しいプロジェクト", new ScrollViewer
        {
            MaxHeight = 520,
            Content = new StackPanel
            {
                Spacing = 16,
                Width = 440,
                Children =
                {
                    name,
                    PickerButton.Labeled("種類", kind),
                    teamPanel,
                    new StackPanel { Spacing = 4, Children = { PickerButton.Labeled("設定の引き継ぎ元", sourceButton), sourceHint } },
                    reason,
                    error,
                },
            },
        }, "作成");
        dialog.IsPrimaryButtonEnabled = false;
        dialog.Opened += (_, _) => name.Focus(FocusState.Programmatic);

        void FillSources()
        {
            // 引き継ぎ元の候補は種類によって既定の名前が変わる。選んでいたプロジェクトは選んだままにする
            var labels = new List<string> { team ? "チームのプロジェクトの既定" : "個人のプロジェクトの既定" };
            labels.AddRange(existing.Select(p => ProjectDisplay.Name(p)));
            var details = new List<string?> { null };
            details.AddRange(existing.Select(p => (string?)(p.IsTeam ? "チーム" : "個人")));
            sourceButton.SetChoices(labels, source is null ? 0 : existing.IndexOf(source) + 1, details);
        }

        FillSources();
        bool IsNewRepository() => repoIndex >= 0 && repositories[repoIndex].Tag as string == NewRepositoryTag;

        void Validate()
        {
            string? problem = string.IsNullOrWhiteSpace(name.Text) ? "プロジェクト名を入力すると作成できます"
                : team && org is null ? "Organization を選ぶと作成できます"
                : team && repoIndex < 0 ? "タスクを作成するリポジトリを選ぶと作成できます"
                : team && IsNewRepository() && !RepositoryNamePattern().IsMatch(repoName.Text.Trim()) ? "新しいリポジトリの名前は、英数字と . _ - で入力してください（例: web-renewal）"
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
            if (await ValuePickers.ChoiceAsync(b, "種類", ["個人", "チーム"], team ? 1 : 0,
                ["自分だけが使うプロジェクト", "Organization で共有するプロジェクト"]) is { } index)
            {
                if (index == 1 && usableOrgs.Count == 0)
                {
                    error.Message = "チームのプロジェクトを作れる Organization がありません。" + orgHint.Text;
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
                    repositories.Add(("新しいリポジトリを作成する", NewRepositoryTag));
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
            if (await ValuePickers.ChoiceAsync(b, "タスクを作成するリポジトリ", [.. repositories.Select(r => r.Label)], repoIndex) is { } index)
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

        AutomationProperties.SetName(repoName, "新しいリポジトリの名前");

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
        OrganizationAccessProblem.OAuthAppNotApproved => "Tasklabe が未承認",
        OrganizationAccessProblem.SamlSsoRequired => "SSO の承認が必要",
        OrganizationAccessProblem.Forbidden => "アクセス権がありません",
        _ => "プロジェクトを作成する権限がありません",
    };

    public static string ProblemAdvice(Organization org) => org.Problem switch
    {
        OrganizationAccessProblem.OAuthAppNotApproved =>
            "組織が外部アプリの利用を制限しています。組織の管理者に Tasklabe の承認を依頼してください。",
        OrganizationAccessProblem.SamlSsoRequired =>
            "組織がシングルサインオン（SAML）を利用しています。GitHub でトークンの SSO 承認を行ってください。",
        OrganizationAccessProblem.Forbidden => "組織のプロジェクトを参照する権限がありません。",
        _ => "組織でプロジェクトを作成する権限がありません。",
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
        OrganizationAccessProblem.OAuthAppNotApproved => $"{org.Login} への承認をリクエストする",
        OrganizationAccessProblem.SamlSsoRequired => $"{org.Login} の SSO を承認する",
        _ => "",
    };
}
