using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Tasklabe.App.Services;
using Tasklabe.Core.Calendar;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Settings;
using Tasklabe.GitHub;

namespace Tasklabe.App.Controls;

/// <summary>
/// プロジェクトに関わる設定の編集欄（UI デザイン設計書 3.6 節）。全体の設定の「個人・チームのプロジェクトの既定」のタブと、
/// プロジェクトの設定の画面で使う。項目は番号のキー（プロジェクトのみ）、工数、稼働日、新しいタスクの区分（チームのみ）、ステータス。
/// 既定はこの PC に、プロジェクトの上書きは GitHub の Project に保存する。
/// </summary>
public sealed partial class SettingsEditor : UserControl
{
    private static readonly (DayOfWeek Day, string Label)[] Weekdays =
    [
        (DayOfWeek.Sunday, "日"), (DayOfWeek.Monday, "月"), (DayOfWeek.Tuesday, "火"), (DayOfWeek.Wednesday, "水"),
        (DayOfWeek.Thursday, "木"), (DayOfWeek.Friday, "金"), (DayOfWeek.Saturday, "土"),
    ];

    private readonly StackPanel _root = new() { Spacing = 8 };
    private readonly InfoBar _notice = new() { IsClosable = true };

    private bool _team;
    private DefaultSettings _defaults = DefaultSettings.Personal;

    /// <summary>編集しているプロジェクト。null なら既定を編集している。</summary>
    private Project? _project;
    private ProjectSettings _overrides = ProjectSettings.None;
    private bool _legacyCalendar;
    private bool _canEdit = true;
    private bool _busy;
    private IReadOnlyDictionary<string, int> _usage = new Dictionary<string, int>();

    /// <summary>すべてのプロジェクトの番号のキー（プロジェクトの ID → キー）。重なりを確かめるのに使う。</summary>
    private IReadOnlyDictionary<string, string> _keys = new Dictionary<string, string>();
    private Debouncer? _save;

    public SettingsEditor()
    {
        // 閉じた InfoBar も幅を求めるため、閉じているあいだは折りたたむ（欄全体が右へずれるのを防ぐ）
        _notice.IsOpen = false;
        _notice.Visibility = Visibility.Collapsed;
        _notice.Closed += (_, _) => _notice.Visibility = Visibility.Collapsed;
        _root.Children.Add(_notice);
        Content = _root;
    }

    /// <summary>設定を変えた（開いている画面を描き直す）。</summary>
    public event EventHandler? Changed;

    /// <summary>個人（team = false）またはチームのプロジェクトの既定を編集する。</summary>
    public void LoadDefaults(bool team)
    {
        _team = team;
        _project = null;
        _defaults = ProjectPreferences.Defaults(team);
        _canEdit = true;
        Render();
    }

    /// <summary>プロジェクトの設定を編集する。canEdit が false なら表示だけにする。</summary>
    public async Task LoadProjectAsync(Project project, bool canEdit)
    {
        ArgumentNullException.ThrowIfNull(project);
        _project = project;
        _team = project.IsTeam;
        _defaults = ProjectPreferences.Defaults(project.IsTeam);
        _overrides = project.Settings;
        _canEdit = canEdit;

        // 以前この PC にだけ保存していたチームの稼働日は、プロジェクトの設定として見せる（変えたときに GitHub へ保存する）
        _legacyCalendar = false;
        if (_overrides.Calendar is null && App.Current.Services.CurrentSettings.WorkCalendars.GetValueOrDefault(project.Id) is { } legacy)
        {
            _overrides = _overrides with { Calendar = WorkCalendarRules.Parse(legacy) };
            _legacyCalendar = true;
        }

        _usage = await App.Current.Services.Workspace.StatusUsageAsync(project.Id);
        _keys = ProjectKey.Resolve(await App.Current.Services.Store.GetProjectsAsync());
        Render();
    }

    private bool IsProject => _project is not null;

    private string DefaultsName => _team ? "チームのプロジェクトの既定" : "個人のプロジェクトの既定";

    // ================================================================ 描画

    private void Render()
    {
        _root.Children.Clear();
        _root.Children.Add(_notice);

        if (IsProject)
        {
            AddKeySection();
        }

        var effort = Resolved;
        AddSection(
            "工数",
            "工数の入力は常に時間で行い、表示だけを切り替えます。",
            IsProject ? nameof(ProjectSettings.EffortUnit) : null,
            _overrides.EffortUnit is not null || _overrides.HoursPerDay is not null,
            enabled => [
                Row("", "表示の単位", null, EffortUnitBox(effort.EffortUnit, enabled)),
                Row("", "1 日の稼働時間", "人日の換算と、状況の画面で担当者の負荷の上限に使います。", HoursPerDayBox(effort.HoursPerDay, enabled)),
            ]);

        AddSection(
            "稼働日",
            "ガントの休日の表示、稼働日数、遅れや日程の調整の計算に使います。",
            IsProject ? nameof(ProjectSettings.Calendar) : null,
            _overrides.Calendar is not null,
            enabled => [CalendarCard(effort.Calendar, enabled)]);

        if (_team)
        {
            AddSection(
                "新しいタスク",
                null,
                IsProject ? nameof(ProjectSettings.NewTaskKind) : null,
                _overrides.NewTaskKind is not null,
                enabled => [Row("", "区分の既定", "N キーや ＋ で追加したタスクを、課題とタスクのどちらとして入れるか。", KindBox(effort.NewTaskKind, enabled))]);
        }

        AddStatusSection();
    }

    /// <summary>既定と上書きを重ねた、いま画面に出す値。</summary>
    private ResolvedSettings Resolved => IsProject ? _overrides.Over(_defaults) : _defaults.Resolved;

    /// <summary>
    /// 見出しと、項目のカードを足す。プロジェクトの設定では、見出しの右に「このプロジェクトで設定する」のスイッチを置き、
    /// 切っているあいだは既定の値を見せて編集させない。
    /// </summary>
    private void AddSection(string title, string? description, string? overrideKey, bool overridden, Func<bool, IEnumerable<UIElement>> content)
    {
        var header = new Grid { Margin = new Thickness(0, 16, 0, 0), ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new TextBlock { Text = title, Style = Res("Text.BodyStrong"), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetHeadingLevel(heading, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
        header.Children.Add(heading);

        bool enabled = _canEdit && (overrideKey is null || overridden);
        if (overrideKey is not null)
        {
            var toggle = new ToggleSwitch
            {
                IsOn = overridden,
                OnContent = "このプロジェクトで設定する",
                OffContent = "既定に従う",
                IsEnabled = _canEdit && !_busy,
                MinWidth = 0,
            };
            AutomationProperties.SetName(toggle, $"{title}をこのプロジェクトで設定する");
            toggle.Toggled += (_, _) => SetOverride(overrideKey, toggle.IsOn);
            Grid.SetColumn(toggle, 1);
            header.Children.Add(toggle);
        }

        _root.Children.Add(header);

        var note = description;
        if (overrideKey is not null && !overridden)
        {
            note = $"{DefaultsName}（全体の設定）に従っています。" + (_team ? "メンバーそれぞれの PC の既定が使われるため、全員でそろえるには、このプロジェクトで設定してください。" : "");
        }
        else if (overrideKey == nameof(ProjectSettings.Calendar) && _legacyCalendar)
        {
            note = "この PC にだけ保存されていた設定です。変えると GitHub に保存し、メンバーと共有します。";
        }

        if (note is not null)
        {
            _root.Children.Add(new TextBlock { Text = note, Style = Res("Text.Caption"), TextWrapping = TextWrapping.Wrap });
        }

        foreach (var element in content(enabled))
        {
            _root.Children.Add(element);
        }
    }

    /// <summary>アイコン、名前、説明、右端の操作からなる 1 行のカード（Fluent の設定カード）。</summary>
    private static Border Row(string glyph, string title, string? description, FrameworkElement control)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new FontIcon { Glyph = glyph, VerticalAlignment = VerticalAlignment.Center });

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = title, Style = Res("Text.Body") });
        if (description is not null)
        {
            texts.Children.Add(new TextBlock { Text = description, Style = Res("Text.Caption"), TextWrapping = TextWrapping.Wrap });
        }

        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 2);
        grid.Children.Add(control);
        return Card(grid);
    }

    private static Border Card(UIElement child) => Cards.Create(child);

    private static Style Res(string key) => AppResources.Style(key);

    // ================================================================ 番号のキー（要件 F-TSK-14〜16、F-SET-05）

    /// <summary>
    /// タスクの番号のキー。既定を持たないプロジェクトだけの値のため、上書きのスイッチは持たない。
    /// まだ決めていないプロジェクトでは、名前から提案したキーを見せ、「決める」で保存させる。
    /// </summary>
    private void AddKeySection()
    {
        var header = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        var heading = new TextBlock { Text = "番号", Style = Res("Text.BodyStrong") };
        AutomationProperties.SetHeadingLevel(heading, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
        header.Children.Add(heading);
        _root.Children.Add(header);
        _root.Children.Add(new TextBlock
        {
            Text = "タスクを「キー-番号」（例: TLB-123）で呼びます。課題を計画へ移しても番号は変わりません。"
                + "キーを決めると GitHub のリポジトリの自動リンクにも登録し、コミットやコメントに書いた番号が Issue へのリンクになります。",
            Style = Res("Text.Caption"),
            TextWrapping = TextWrapping.Wrap,
        });

        var saved = _overrides.Key;
        var current = saved ?? _keys.GetValueOrDefault(_project!.Id) ?? ProjectKey.Fallback;
        bool enabled = _canEdit && !_busy;
        var box = new TextBox
        {
            Text = current,
            MaxLength = ProjectKey.MaxLength,
            CharacterCasing = CharacterCasing.Upper,
            Width = 120,
            IsEnabled = enabled,
        };
        AutomationProperties.SetName(box, "番号のキー");
        var decide = new Button { Content = "決める", IsEnabled = enabled, Style = Res("AccentButtonStyle"), Visibility = saved is null ? Visibility.Visible : Visibility.Collapsed };
        AutomationProperties.SetName(decide, "このキーに決める");

        void Commit()
        {
            var key = ProjectKey.Normalize(box.Text);
            if (key == saved)
            {
                return;
            }

            var others = _keys.Where(k => k.Key != _project!.Id).Select(k => k.Value);
            if (ProjectKey.Problem(key, others) is { } problem)
            {
                ShowNotice(InfoBarSeverity.Warning, "キーを変更できません", problem);
                box.Text = current;
                return;
            }

            _ = SaveKeyAsync(key);
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                Commit();
            }
        };
        box.TextChanged += (_, _) => decide.Visibility = saved is null || ProjectKey.Normalize(box.Text) != saved ? Visibility.Visible : Visibility.Collapsed;
        decide.Click += (_, _) => Commit();

        var control = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        control.Children.Add(box);
        control.Children.Add(decide);
        _root.Children.Add(Row(
            "",
            "キー",
            saved is null
                ? "名前から提案したキーです。決めると、プロジェクトの名前を変えても番号が変わらず、メンバーとも同じ番号になります。"
                : $"このプロジェクトのタスクは {ProjectKey.Format(saved, 123)} のように呼びます。キーを変えても、前のキーの自動リンクは残ります。",
            control));
    }

    /// <summary>キーを GitHub の Project に保存し、リポジトリの自動リンクに登録する。</summary>
    private async Task SaveKeyAsync(string key)
    {
        if (_project is not { } project)
        {
            return;
        }

        _overrides = _overrides with { Key = key };
        _busy = true;
        Render();
        try
        {
            await App.Current.Services.Workspace.SaveProjectSettingsAsync(project, _overrides);
            var all = await App.Current.Services.Store.GetProjectsAsync();
            _project = all.FirstOrDefault(p => p.Id == project.Id) ?? project;
            _keys = ProjectKey.Resolve(all);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is GitHubException or HttpRequestException)
        {
            _overrides = _overrides with { Key = project.Settings.Key };
            ShowNotice(InfoBarSeverity.Error, "キーを GitHub に保存できませんでした", Data.SyncEngine.Describe(ex));
            _busy = false;
            Render();
            return;
        }

        // 自動リンクは、登録できなくてもキーはアプリの中で使えるため、結果を知らせるだけにする
        try
        {
            var result = await App.Current.Services.Workspace.RegisterKeyAsync(project.RepositoryNameWithOwner, key);
            if (result == AutolinkResult.NotPermitted)
            {
                ShowNotice(InfoBarSeverity.Warning, "キーを保存しました",
                    $"リポジトリの管理者でないため、GitHub の自動リンクは登録できませんでした。番号はアプリの中で使えます。"
                    + $"コミットやコメントでもリンクにするには、リポジトリの管理者にこの画面でキーを決め直してもらうか、リポジトリの設定の Autolink references に「{key}-」を登録してもらってください。");
            }
            else
            {
                ShowNotice(InfoBarSeverity.Success, "キーを保存しました",
                    result is null
                        ? $"このプロジェクトのタスクは {ProjectKey.Format(key, 123)} のように呼びます。"
                        : $"GitHub のリポジトリの自動リンクにも登録しました。コミットやコメントに書いた {ProjectKey.Format(key, 123)} が Issue へのリンクになります。");
            }
        }
        catch (Exception ex) when (ex is GitHubException or HttpRequestException)
        {
            ShowNotice(InfoBarSeverity.Warning, "キーを保存しました", "GitHub の自動リンクは登録できませんでした。" + Data.SyncEngine.Describe(ex));
        }
        finally
        {
            _busy = false;
            Render();
        }
    }

    // ================================================================ 工数・区分

    // 候補から選ぶ項目は、ドロップダウンではなくピッカーで選ぶ（UX 規約 UX-02）
    private PickerButton EffortUnitBox(EffortUnit unit, bool enabled)
    {
        EffortUnit[] units = [EffortUnit.Hours, EffortUnit.Days];
        var box = PickerButton.ForChoices("工数の表示の単位", ["時間 (h)", "人日"], unit == EffortUnit.Days ? 1 : 0, i =>
            Update(d => d with { EffortUnit = units[i] }, o => o with { EffortUnit = units[i] }));
        box.MinWidth = 160;
        box.HorizontalAlignment = HorizontalAlignment.Right;
        box.IsEnabled = enabled;
        return box;
    }

    private NumberBox HoursPerDayBox(double hours, bool enabled)
    {
        var box = new NumberBox
        {
            MinWidth = 160,
            Minimum = 1,
            Maximum = 24,
            SmallChange = 0.5,
            Value = hours,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            IsEnabled = enabled,
        };
        AutomationProperties.SetName(box, "1 日の稼働時間（時間）");
        box.ValueChanged += (_, e) =>
        {
            if (!double.IsNaN(e.NewValue) && e.NewValue is >= 1 and <= 24 && Math.Abs(e.NewValue - e.OldValue) > 0.001)
            {
                Update(d => d with { HoursPerDay = e.NewValue }, o => o with { HoursPerDay = e.NewValue }, rerender: false);
            }
        };
        return box;
    }

    private PickerButton KindBox(TaskKind kind, bool enabled)
    {
        TaskKind[] kinds = [TaskKind.Issue, TaskKind.Task];
        var box = PickerButton.ForChoices("新しいタスクの区分の既定", [.. kinds.Select(KindVisuals.Name)], kind == TaskKind.Issue ? 0 : 1, i =>
            Update(d => d with { NewTaskKind = kinds[i] }, o => o with { NewTaskKind = kinds[i] }),
            tips: [.. kinds.Select(k => (string?)KindVisuals.Description(k))]);
        box.MinWidth = 160;
        box.HorizontalAlignment = HorizontalAlignment.Right;
        box.IsEnabled = enabled;
        return box;
    }

    // ================================================================ 稼働日（要件 F-CAL-01）

    private Border CalendarCard(WorkCalendarRules rules, bool enabled)
    {
        var editor = new StackPanel { Spacing = 12 };
        void Save(WorkCalendarRules updated)
        {
            _legacyCalendar = false;
            Update(d => d with { Calendar = updated }, o => o with { Calendar = updated });
        }

        // 休む曜日
        var days = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var (day, label) in Weekdays)
        {
            var toggle = new ToggleButton { Content = label, Width = 40, IsChecked = rules.DaysOff.Contains(day), IsEnabled = enabled };
            AutomationProperties.SetName(toggle, $"{label}曜日を休む");
            toggle.Click += (_, _) =>
            {
                var off = new HashSet<DayOfWeek>(rules.DaysOff);
                if (toggle.IsChecked == true)
                {
                    off.Add(day);
                }
                else
                {
                    off.Remove(day);
                }

                // すべての曜日を休みにはできない
                if (off.Count == 7)
                {
                    toggle.IsChecked = false;
                    return;
                }

                Save(rules with { DaysOff = off });
            };
            days.Children.Add(toggle);
        }

        var daysRow = new StackPanel { Spacing = 4 };
        daysRow.Children.Add(new TextBlock { Text = "休む曜日", Style = Res("Text.Body") });
        daysRow.Children.Add(days);
        editor.Children.Add(daysRow);

        var holidays = new ToggleSwitch
        {
            Header = "日本の祝日を休む",
            IsOn = rules.JapaneseHolidaysOff,
            IsEnabled = enabled,
        };
        holidays.Toggled += (_, _) =>
        {
            if (holidays.IsOn != rules.JapaneseHolidaysOff)
            {
                Save(rules with { JapaneseHolidaysOff = holidays.IsOn });
            }
        };
        editor.Children.Add(holidays);

        // 独自の休日（夏季休業、年末年始など）
        var extraRow = new StackPanel { Spacing = 4 };
        extraRow.Children.Add(new TextBlock { Text = "独自の休日", Style = Res("Text.Body") });
        extraRow.Children.Add(new TextBlock { Text = "夏季休業や年末年始など、会社やチームで決めた休みを足します。", Style = Res("Text.Caption"), TextWrapping = TextWrapping.Wrap });
        // 日付は、ほかの日付と同じピッカー（候補と入力欄）で選ぶ（UX 規約 UX-02、UX-30）
        var picker = new PickerButton("独自の休日に足す日付") { MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = enabled };
        picker.SetValue(null, "\uE787", placeholder: "日付を足す…");
        picker.Pick = async b =>
        {
            if (await ValuePickers.DateAsync(b, null, "独自の休日に足す日付", [null]) is { Value: { } date })
            {
                Save(rules with { ExtraHolidays = new HashSet<DateOnly>(rules.ExtraHolidays) { date } });
            }
        };
        extraRow.Children.Add(picker);

        foreach (var date in rules.ExtraHolidays.Order())
        {
            var chip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            chip.Children.Add(new TextBlock { Text = DateText.Long(date), VerticalAlignment = VerticalAlignment.Center });
            var remove = SubtleIconButton("", $"{DateText.Long(date)} を独自の休日から外す");
            remove.IsEnabled = enabled;
            remove.Click += (_, _) =>
            {
                var extra = new HashSet<DateOnly>(rules.ExtraHolidays);
                extra.Remove(date);
                Save(rules with { ExtraHolidays = extra });
            };
            chip.Children.Add(remove);
            extraRow.Children.Add(chip);
        }

        editor.Children.Add(extraRow);

        if (enabled && !rules.IsStandard)
        {
            var reset = new HyperlinkButton { Content = "標準（土日と祝日が休み）に戻す", Padding = new Thickness(0) };
            reset.Click += (_, _) => Save(WorkCalendarRules.Standard);
            editor.Children.Add(reset);
        }

        return Card(editor);
    }

    // ================================================================ ステータス（要件 F-PRJ-04、F-SET-03）

    /// <summary>
    /// 既定では新しいプロジェクトの Status の選択肢のひな形を、プロジェクトではそのプロジェクトの Status の選択肢そのものを編集する。
    /// プロジェクトの選択肢は GitHub に直接反映するため、上書きのスイッチは持たない。
    /// </summary>
    private void AddStatusSection()
    {
        var header = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        var heading = new TextBlock { Text = "ステータス", Style = Res("Text.BodyStrong") };
        AutomationProperties.SetHeadingLevel(heading, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
        header.Children.Add(heading);
        _root.Children.Add(header);
        _root.Children.Add(new TextBlock
        {
            Text = IsProject
                ? "このプロジェクトのステータスの名前・カテゴリ・並びです。変更はすぐに GitHub の Project に反映します。タスクがあるステータスは外せません。"
                : "新しいプロジェクトを作るときの、ステータスの名前・カテゴリ・並びです。作った後は、プロジェクトの設定で変えられます。",
            Style = Res("Text.Caption"),
            TextWrapping = TextWrapping.Wrap,
        });

        var rows = CurrentStatuses();
        var list = new StackPanel { Spacing = 4 };
        bool enabled = _canEdit && !_busy;
        for (int i = 0; i < rows.Count; i++)
        {
            list.Children.Add(StatusRow(rows, i, enabled));
        }

        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        var add = new Button { IsEnabled = enabled, Style = Res("SubtleButtonStyle") };
        add.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { new FontIcon { Glyph = "", FontSize = 12 }, new TextBlock { Text = "ステータスを追加" } },
        };
        AutomationProperties.SetName(add, "ステータスを追加");
        add.Click += (_, _) =>
        {
            var name = NextName(rows);
            ApplyStatuses([.. rows, new StatusOptionEdit(null, name, "GRAY", StatusCategory.Todo)]);
        };
        footer.Children.Add(add);

        if (IsProject && _project!.Url is { } url)
        {
            footer.Children.Add(new HyperlinkButton { Content = "色や説明を GitHub で編集", NavigateUri = new Uri(url + "/settings") });
        }
        else if (!IsProject && !_defaults.Statuses.SequenceEqual(DefaultSettings.StandardStatuses))
        {
            var reset = new HyperlinkButton { Content = "標準の 6 種に戻す" };
            reset.Click += (_, _) => ApplyStatuses([.. DefaultSettings.StandardStatuses.Select(s => new StatusOptionEdit(null, s.Name, s.Color, s.Category))]);
            footer.Children.Add(reset);
        }

        list.Children.Add(footer);
        _root.Children.Add(Card(list));
    }

    private List<StatusOptionEdit> CurrentStatuses() => IsProject
        ? [.. _project!.StatusOptions.Select(o => new StatusOptionEdit(o.Id, o.Name, o.Color, o.Category))]
        : [.. _defaults.Statuses.Select(s => new StatusOptionEdit(null, s.Name, s.Color, s.Category))];

    private Grid StatusRow(List<StatusOptionEdit> rows, int index, bool enabled)
    {
        var row = rows[index];
        var grid = new Grid { ColumnSpacing = 8 };
        foreach (var width in (ReadOnlySpan<GridLength>)[GridLength.Auto, new(1, GridUnitType.Star), new(128), new(64), GridLength.Auto, GridLength.Auto, GridLength.Auto])
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        }

        grid.Children.Add(new FontIcon
        {
            Glyph = StatusVisuals.Glyph(row.Category),
            FontSize = 16,
            Foreground = ThemeResources.Brush(StatusVisuals.BrushKey(row.Category)),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var name = new TextBox { Text = row.Name, IsEnabled = enabled };
        AutomationProperties.SetName(name, $"{index + 1} 番目のステータスの名前");
        void Rename()
        {
            var text = name.Text.Trim();
            if (text != row.Name)
            {
                ApplyStatuses([.. rows.Select((r, i) => i == index ? r with { Name = text } : r)]);
            }
        }

        name.LostFocus += (_, _) => Rename();
        name.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                Rename();
            }
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        var categories = StatusCategories.All.ToArray();
        var category = PickerButton.ForChoices($"{row.Name} のカテゴリ", [.. categories.Select(StatusVisuals.Name)], Array.IndexOf(categories, row.Category), i =>
        {
            if (categories[i] != row.Category)
            {
                ApplyStatuses([.. rows.Select((r, n) => n == index ? r with { Category = categories[i] } : r)]);
            }
        });
        category.IsEnabled = enabled;
        category.SetValue(StatusVisuals.Name(row.Category), StatusVisuals.Glyph(row.Category), StatusVisuals.BrushKey(row.Category));
        Grid.SetColumn(category, 2);
        grid.Children.Add(category);

        int used = row.Id is null ? 0 : _usage.GetValueOrDefault(row.Id);
        var usage = new TextBlock
        {
            Text = IsProject ? $"{used} 件" : "",
            Style = Res("Text.Caption"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(usage, 3);
        grid.Children.Add(usage);

        var up = SubtleIconButton("", $"{row.Name} を上へ");
        up.IsEnabled = enabled && index > 0;
        up.Click += (_, _) => ApplyStatuses(Swap(rows, index, index - 1));
        Grid.SetColumn(up, 4);
        grid.Children.Add(up);

        var down = SubtleIconButton("", $"{row.Name} を下へ");
        down.IsEnabled = enabled && index < rows.Count - 1;
        down.Click += (_, _) => ApplyStatuses(Swap(rows, index, index + 1));
        Grid.SetColumn(down, 5);
        grid.Children.Add(down);

        var remove = SubtleIconButton("", $"{row.Name} を外す");
        remove.IsEnabled = enabled && used == 0 && rows.Count > 1;
        if (used > 0)
        {
            ToolTipService.SetToolTip(remove, $"タスクが {used} 件あるため外せません。先に別のステータスへ移してください");
        }

        remove.Click += (_, _) => ApplyStatuses([.. rows.Where((_, i) => i != index)]);
        Grid.SetColumn(remove, 6);
        grid.Children.Add(remove);
        return grid;
    }

    private static List<StatusOptionEdit> Swap(List<StatusOptionEdit> rows, int a, int b)
    {
        var copy = rows.ToList();
        (copy[a], copy[b]) = (copy[b], copy[a]);
        return copy;
    }

    private static string NextName(List<StatusOptionEdit> rows)
    {
        for (int n = 1; ; n++)
        {
            var name = n == 1 ? "新しいステータス" : $"新しいステータス {n}";
            if (!rows.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return name;
            }
        }
    }

    /// <summary>ステータスの並びを確かめて保存する（既定はこの PC に、プロジェクトは GitHub に）。</summary>
    private async void ApplyStatuses(List<StatusOptionEdit> rows)
    {
        var templates = rows.Select(r => new StatusTemplate(r.Name.Trim(), r.Color, r.Category)).ToList();
        if (StatusTemplates.Problem(templates) is { } problem)
        {
            ShowNotice(InfoBarSeverity.Warning, "ステータスを変更できません", problem);
            Render();
            return;
        }

        if (!IsProject)
        {
            _defaults = _defaults with { Statuses = templates };
            ProjectPreferences.SaveDefaults(_team, _defaults);
            _notice.IsOpen = false;
            _notice.Visibility = Visibility.Collapsed;
            Render();
            return;
        }

        var project = _project!;
        _busy = true;
        Render();
        try
        {
            await App.Current.Services.Workspace.UpdateStatusOptionsAsync(project, rows);
            _project = await App.Current.Services.Store.GetProjectAsync(project.Id) ?? project;
            _usage = await App.Current.Services.Workspace.StatusUsageAsync(project.Id);
            ShowNotice(InfoBarSeverity.Success, "GitHub に反映しました", "ステータスの変更を Project に保存しました。");
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is GitHubException or HttpRequestException or InvalidOperationException)
        {
            ShowNotice(InfoBarSeverity.Error, "ステータスを GitHub に反映できませんでした", ex is InvalidOperationException ? ex.Message : Data.SyncEngine.Describe(ex));
        }
        finally
        {
            _busy = false;
            Render();
        }
    }

    // ================================================================ 保存

    /// <summary>上書きのスイッチを切り替えた。入れたときは、いまの既定の値から始める。</summary>
    private void SetOverride(string key, bool on)
    {
        var d = _defaults;
        _overrides = key switch
        {
            nameof(ProjectSettings.EffortUnit) => _overrides with { EffortUnit = on ? d.EffortUnit : null, HoursPerDay = on ? d.HoursPerDay : null },
            nameof(ProjectSettings.Calendar) => _overrides with { Calendar = on ? d.Calendar : null },
            nameof(ProjectSettings.NewTaskKind) => _overrides with { NewTaskKind = on ? d.NewTaskKind : null },
            _ => _overrides,
        };
        if (key == nameof(ProjectSettings.Calendar))
        {
            _legacyCalendar = false;
        }

        Render();
        ScheduleSave();
    }

    /// <summary>値を変えた。既定はすぐにこの PC へ、プロジェクトの上書きは少し待ってから GitHub へ保存する。</summary>
    private void Update(Func<DefaultSettings, DefaultSettings> defaults, Func<ProjectSettings, ProjectSettings> overrides, bool rerender = true)
    {
        if (IsProject)
        {
            _overrides = overrides(_overrides);
            ScheduleSave();
        }
        else
        {
            _defaults = defaults(_defaults);
            ProjectPreferences.SaveDefaults(_team, _defaults);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        if (rerender)
        {
            Render();
        }
    }

    private void ScheduleSave()
    {
        _save ??= new Debouncer(DispatcherQueue, TimeSpan.FromMilliseconds(700));
        _save.Run(() => _ = SaveOverridesAsync());
    }

    private async Task SaveOverridesAsync()
    {
        if (_project is not { } project)
        {
            return;
        }

        try
        {
            await App.Current.Services.Workspace.SaveProjectSettingsAsync(project, _overrides);
            ProjectPreferences.ForgetLegacy(project);
            _project = await App.Current.Services.Store.GetProjectAsync(project.Id) ?? project;
            ShowNotice(InfoBarSeverity.Success, "GitHub に保存しました", "このプロジェクトのメンバー全員に同じ設定が使われます。");
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is GitHubException or HttpRequestException)
        {
            var retry = new Button { Content = "もう一度保存" };
            retry.Click += async (_, _) => await SaveOverridesAsync();
            ShowNotice(InfoBarSeverity.Error, "設定を GitHub に保存できませんでした", Data.SyncEngine.Describe(ex), retry);
        }
    }

    private void ShowNotice(InfoBarSeverity severity, string title, string message, ButtonBase? action = null)
    {
        _notice.Severity = severity;
        _notice.Title = title;
        _notice.Message = message;
        _notice.ActionButton = action;
        _notice.Visibility = Visibility.Visible;
        _notice.IsOpen = true;
    }

    private static Button SubtleIconButton(string glyph, string name)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 12 },
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Style = Res("SubtleButtonStyle"),
        };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        return button;
    }
}
