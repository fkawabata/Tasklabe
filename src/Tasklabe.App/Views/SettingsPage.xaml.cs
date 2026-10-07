using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Input;
using Tasklabe.App.Controls;
using Tasklabe.App.Services;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Keyboard;
using Microsoft.UI.Xaml.Automation;

namespace Tasklabe.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        DefaultsEditor.Changed += (_, _) => App.Current.Shell?.NotifyDataChanged();
    }

    private void OnEmptyColumnsToggled(object sender, RoutedEventArgs e)
    {
        var services = App.Current.Services;
        if (services.CurrentSettings.KanbanShowEmptyColumns != EmptyColumnsSwitch.IsOn)
        {
            services.CurrentSettings.KanbanShowEmptyColumns = EmptyColumnsSwitch.IsOn;
            services.SaveSettings();
        }
    }

    /// <summary>画面に書いておく操作のヒントを出すか（要件 F-SET-07）。開いている画面も描き直す。</summary>
    private void OnHintsToggled(object sender, RoutedEventArgs e)
    {
        var settings = App.Current.Services.CurrentSettings;
        if (settings.ShowHints != HintsSwitch.IsOn)
        {
            settings.ShowHints = HintsSwitch.IsOn;
            App.Current.Services.SaveSettings();
            App.Current.Shell?.NotifyDataChanged();
        }
    }

    /// <summary>計画の表・ガント・課題の一覧に番号を出すか（要件 F-SET-06）。この PC の利用者だけの設定。</summary>
    private void OnNumberToggled(object sender, RoutedEventArgs e)
    {
        // スイッチごとに自分の項目だけを見る（開いたときに値を入れても、他の項目を書き換えない）
        var settings = App.Current.Services.CurrentSettings;
        var toggle = (ToggleSwitch)sender;
        bool on = toggle.IsOn;
        bool changed = false;
        if (toggle == NumberInPlanSwitch && settings.ShowNumberInPlan != on)
        {
            settings.ShowNumberInPlan = on;
            changed = true;
        }
        else if (toggle == NumberInGanttSwitch && settings.ShowNumberInGantt != on)
        {
            settings.ShowNumberInGantt = on;
            changed = true;
        }
        else if (toggle == NumberInIssuesSwitch && settings.ShowNumberInIssues != on)
        {
            settings.ShowNumberInIssues = on;
            changed = true;
        }

        if (changed)
        {
            App.Current.Services.SaveSettings();
            App.Current.Shell?.NotifyDataChanged();
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        ElementTheme[] themes = [ElementTheme.Default, ElementTheme.Light, ElementTheme.Dark];
        ThemeHost.Content = PickerButton.ForChoices("テーマ", ["Windows の設定に従う", "ライト", "ダーク"],
            Array.IndexOf(themes, App.Current.Theme), i => App.Current.Theme = themes[i]);

        var settings = App.Current.Services.CurrentSettings;
        EmptyColumnsSwitch.IsOn = settings.KanbanShowEmptyColumns;
        HintsSwitch.IsOn = settings.ShowHints;
        NumberInPlanSwitch.IsOn = settings.ShowNumberInPlan;
        NumberInGanttSwitch.IsOn = settings.ShowNumberInGantt;
        NumberInIssuesSwitch.IsOn = settings.ShowNumberInIssues;

        AccountText.Text = settings.UserName is { Length: > 0 } name ? $"{name} (@{settings.UserLogin})" : $"@{settings.UserLogin}";
        RepositoryText.Text = $"個人用リポジトリ: {settings.PersonalRepositoryName}";

        OrganizationAccessLink.NavigateUri = Dialogs.NewProjectDialog.ApplicationSettingsUrl;
        _ = LoadOrganizationsAsync();
        BuildShortcuts();

        // 開くタブ（プロジェクトの設定の「既定を開く」からは、そのプロジェクトの種類の既定を開く）
        var tab = e.Parameter as string ?? _lastTab;
        foreach (var item in new[] { GeneralTab, PersonalTab, TeamTab })
        {
            item.IsSelected = (string)item.Tag == tab;
        }

        ShowTab(tab);
    }

    /// <summary>最後に開いていたタブ。</summary>
    private static string _lastTab = "general";

    // ---------------------------------------------------------------- タブ（要件 F-SET-01、UI デザイン設計書 3.6 節）

    private void OnTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) =>
        ShowTab(Tabs.SelectedItem?.Tag as string ?? "general");

    private void ShowTab(string tab)
    {
        _lastTab = tab;
        bool general = tab == "general";
        GeneralPanel.Visibility = general ? Visibility.Visible : Visibility.Collapsed;
        DefaultsPanel.Visibility = general ? Visibility.Collapsed : Visibility.Visible;
        if (general)
        {
            return;
        }

        bool team = tab == "team";
        DefaultsIntro.Text = team
            ? "チームのプロジェクトの既定です。新しいチームのプロジェクトには、作った時点のこの値を書き込み、メンバー全員で共有します。"
            : "個人のプロジェクト（マイタスクを含む）の既定です。マイタスクのように複数のプロジェクトをまたぐ画面の工数の表示にも使います。";
        DefaultsEditor.LoadDefaults(team);
        _ = BuildProjectLinksAsync(team);
    }

    /// <summary>既定を上書きできるプロジェクトの一覧（プロジェクトの設定を開く入口）。</summary>
    private async Task BuildProjectLinksAsync(bool team)
    {
        var projects = (await App.Current.Services.Store.GetProjectsAsync())
            .Where(p => !p.Closed && p.IsTeam == team)
            .OrderBy(p => p.Kind == ProjectKind.Inbox ? 0 : 1)
            .ThenBy(p => p.Title, StringComparer.CurrentCulture)
            .ToList();

        ProjectLinks.Children.Clear();
        foreach (var project in projects)
        {
            var grid = new Grid { ColumnSpacing = 16, Padding = new Thickness(16, 8, 16, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = ProjectDisplay.Name(project), Style = AppResources.Style("Text.Body") });
            texts.Children.Add(new TextBlock
            {
                Text = project.Settings.IsEmpty ? "既定に従っています" : "このプロジェクトで上書きしています",
                Style = AppResources.Style("Text.Caption"),
            });
            grid.Children.Add(texts);

            var open = new Button { Content = "設定を開く", VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(open, $"{ProjectDisplay.Name(project)} の設定を開く");
            open.Click += (_, _) => App.Current.MainWindow!.OpenProjectSettings(project.Id);
            Grid.SetColumn(open, 1);
            grid.Children.Add(open);

            ProjectLinks.Children.Add(Controls.Cards.Create(grid, new Thickness(0)));
        }
    }

    // ---------------------------------------------------------------- キーボード（要件 F-KEY-04、UI デザイン設計書 3.6 節）

    /// <summary>操作ごとに、いまの割り当てと「変更」「既定に戻す」を並べる。</summary>
    private void BuildShortcuts()
    {
        var keymap = App.Current.Services.Keymap;
        ShortcutSections.Children.Clear();
        foreach (var group in ShortcutCatalog.All.GroupBy(d => d.Category))
        {
            var card = new StackPanel { Spacing = 2 };
            card.Children.Add(new TextBlock
            {
                Text = group.Key,
                Style = AppResources.Style("Text.Caption"),
                Margin = new Thickness(0, 0, 0, 4),
            });

            foreach (var definition in group)
            {
                card.Children.Add(ShortcutRow(keymap, definition));
            }

            ShortcutSections.Children.Add(Controls.Cards.Create(card));
        }
    }

    private Grid ShortcutRow(Keymap keymap, ShortcutDefinition definition)
    {
        var grid = new Grid { ColumnSpacing = 12, MinHeight = 36 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });

        grid.Children.Add(new TextBlock { Text = definition.Label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });

        var keys = keymap.Display(definition.Id);
        var caps = Dialogs.ShortcutHelpDialog.KeyCaps(keys.Length > 0 ? keys : "割り当てなし");
        Grid.SetColumn(caps, 1);
        grid.Children.Add(caps);

        var change = new Button { Content = "変更…", VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(change, $"{definition.Label}のキーを変更");
        change.Click += async (_, _) => await ChangeShortcutAsync(definition);
        Grid.SetColumn(change, 2);
        grid.Children.Add(change);

        if (!keymap.For(definition.Id).SequenceEqual(definition.Defaults))
        {
            var reset = new HyperlinkButton { Content = "既定に戻す", VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(reset, $"{definition.Label}のキーを既定に戻す");
            reset.Click += (_, _) => Save(App.Current.Services.Keymap.Reset(definition.Id));
            Grid.SetColumn(reset, 3);
            grid.Children.Add(reset);
        }

        return grid;
    }

    /// <summary>
    /// 押したキーを割り当てる。画面の基本操作に使うキーは断り、同時に働く別の操作と重なるときは、そちらから外すことを示す。
    /// </summary>
    private async Task ChangeShortcutAsync(ShortcutDefinition definition)
    {
        var keymap = App.Current.Services.Keymap;
        KeyGesture? captured = null;
        ShortcutDefinition? conflict = null;

        var shown = new TextBlock { Text = "キーを押してください", Style = AppResources.Style("Text.Subtitle"), HorizontalAlignment = HorizontalAlignment.Center };
        var message = new TextBlock { Style = AppResources.Style("Text.Caption"), TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };
        var target = new Button
        {
            Content = shown,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 72,
            Style = AppResources.Style("SubtleButtonStyle"),
        };
        AutomationProperties.SetName(target, "割り当てるキーを押す");
        var panel = new StackPanel { Spacing = 12, Width = 380 };
        panel.Children.Add(new TextBlock { Text = $"「{definition.Label}」に割り当てるキーを押してください。いまの割り当て: {Current(keymap, definition.Id)}", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(target);
        panel.Children.Add(message);
        panel.Children.Add(AppDialog.HintText("キーを押してから、Tab で「割り当てる」へ移って Enter　Esc でキャンセル"));

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "キーを変更",
            Content = panel,
            PrimaryButtonText = "割り当てる",
            SecondaryButtonText = "割り当てを外す",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.None,
            IsPrimaryButtonEnabled = false,
        };

        // Esc 以外のキーはすべて受け取る（Tab や Enter でフォーカスが動かないように）
        target.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape || KeyInput.From(e.Key) is not { } gesture)
            {
                return;
            }

            e.Handled = true;
            captured = gesture;
            shown.Text = gesture.Display;
            var (result, other) = keymap.Check(definition.Id, gesture);
            conflict = result == KeymapCheck.Conflict ? other : null;
            message.Text = result switch
            {
                KeymapCheck.Reserved => "このキーは画面の基本操作（移動、決定、取り消しなど）に使うため割り当てられません。",
                KeymapCheck.Conflict => $"「{other!.Label}」に割り当て済みです。割り当てると「{other.Label}」からは外します。",
                _ => "",
            };
            dialog.IsPrimaryButtonEnabled = result != KeymapCheck.Reserved;
        }), true);
        dialog.Opened += (_, _) => target.Focus(FocusState.Keyboard);

        switch (await dialog.ShowAsync())
        {
            case ContentDialogResult.Primary when captured is { } g:
                var updated = keymap;
                if (conflict is not null)
                {
                    updated = updated.With(conflict.Id, updated.For(conflict.Id).Where(x => x != g).ToList());
                }

                Save(updated.With(definition.Id, [g]));
                break;
            case ContentDialogResult.Secondary:
                Save(keymap.With(definition.Id, []));
                break;
        }
    }

    private static string Current(Keymap keymap, string id) => keymap.Display(id) is { Length: > 0 } s ? s : "なし";

    private void Save(Keymap keymap)
    {
        App.Current.Services.SaveKeymap(keymap);
        BuildShortcuts();
    }

    private async void OnShowShortcuts(object sender, RoutedEventArgs e) => await Dialogs.ShortcutHelpDialog.ShowAsync(XamlRoot);

    private async void OnResetAllShortcuts(object sender, RoutedEventArgs e)
    {
        var dialog = AppDialog.Confirm(XamlRoot, "キーの割り当てを既定に戻しますか？", "変えた割り当てをすべて既定に戻します。", "既定に戻す");
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            Save(new Keymap());
        }
    }


    private async Task LoadOrganizationsAsync()
    {
        var orgs = await App.Current.Services.Store.GetOrganizationsAsync();
        var summary = orgs.Count == 0
            ? "利用できる Organization はありません。"
            : "利用できる Organization: " + string.Join("、", orgs.Select(o => o.IsAccessible ? o.Login : $"{o.Login}（{Dialogs.NewProjectDialog.ProblemAdvice(o)}）"));
        OrganizationsText.Text = summary + " 所属している組織が表示されない場合は、組織が外部アプリの利用を制限している可能性があります。";
    }

    private async void OnSignOutClick(object sender, RoutedEventArgs e)
    {
        var dialog = AppDialog.Confirm(XamlRoot, "サインアウトしますか？",
            "この PC に保存したアクセストークンとキャッシュを削除します。GitHub 上のデータは削除されません。", "サインアウト");

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await App.Current.MainWindow!.SignOutAsync();
        }
    }

    /// <summary>
    /// 中身の幅を、表示できる幅（最大 1000）に合わせる。ScrollViewer は中身を横方向に制限なく測るため、
    /// 幅を決めておかないと、中身によって位置と幅が変わってしまう。
    /// </summary>
    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e) =>
        RootPanel.Width = Math.Min(1000, Math.Max(0, e.NewSize.Width));
}
