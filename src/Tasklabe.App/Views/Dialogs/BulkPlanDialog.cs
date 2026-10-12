using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Tasklabe.App.Controls;
using Tasklabe.App.Services;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Wbs;
using Tasklabe.Data;
using Tasklabe.GitHub;
using Windows.System;

namespace Tasklabe.App.Views.Dialogs;

/// <summary>
/// 計画をまとめて入力する（要件 F-UI-WBS-05、UI デザイン設計書 3.4.6 節）。
/// 親タスクとマイルストーンを 1 行ずつ並べ、名前と日程だけを入れて一度に作る。
/// 次の行の開始日と、上の行とのつなぎ（依存関係）は自動で入れる（<see cref="BulkPlan"/>）。値はすべてピッカーで選ぶ。
/// </summary>
public static class BulkPlanDialog
{
    private const string LinkKey = "bulk-plan:link";
    private const string StartKey = "bulk-plan:start";
    private const double Width = 960;

    /// <summary>1 行の入力と、その部品。</summary>
    private sealed class Row
    {
        public BulkKind Kind { get; set; }

        public string Title { get; set; } = "";

        public DateOnly? Start { get; set; }

        public DateOnly? End { get; set; }

        public bool Unlinked { get; set; }

        public required Grid View { get; init; }

        public required TextBlock Number { get; init; }

        public required PickerButton KindButton { get; init; }

        public required TextBox TitleBox { get; init; }

        /// <summary>日程。親タスクは予定（開始〜終了）の範囲、マイルストーンは期日の 1 日を選ぶ。</summary>
        public required PickerButton DatesButton { get; init; }

        public required TextBlock Days { get; init; }

        public required ContentControl Link { get; init; }

        public BulkRow ToInput() => new(Kind, Title, Start, End, Unlinked);
    }

    /// <param name="plan">いまの計画（置き場所の候補と並び順に使う）。</param>
    /// <param name="initialParent">置き場所の既定（計画の表で選んでいた親タスク）。</param>
    public static async Task ShowAsync(XamlRoot root, Project project, TaskTree plan, TaskNode? initialParent)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(plan);

        var caption = AppResources.Style("Text.Caption");
        var rows = new List<Row>();
        var rowsPanel = new StackPanel { Spacing = 8 };
        IReadOnlyList<BulkResolvedRow> resolved = [];
        var summary = new TextBlock { Style = caption };
        var problem = new TextBlock { Style = caption, Foreground = ThemeResources.Brush("SystemFillColorCriticalBrush"), TextWrapping = TextWrapping.Wrap };
        ContentDialog? dialog = null;

        // ---- 設定（選んだものは次に開いたときも使う）
        TaskNode? parent = initialParent;
        bool link = ViewState.Get(LinkKey) != "off";
        var startMode = ViewState.Get(StartKey) == "blank" ? BulkStart.Blank : BulkStart.AfterPrevious;

        var placeButton = new PickerButton(Texts.Get("Picker_Parent")) { MinWidth = 220 };
        void ShowPlace() => placeButton.SetValue(parent?.Task.Title ?? Texts.Get("Bulk_TopLevel"), "\uE8FD");
        placeButton.Pick = async b =>
        {
            if (await ValuePickers.ParentAsync(b, null, plan, parent?.Task.IssueId) is { } picked)
            {
                parent = picked.Value;
                ShowPlace();
            }
        };
        ShowPlace();

        // 依存関係と開始日の決め方は、どちらも決まった候補から選ぶ
        var linkButton = PickerButton.ForChoices(Texts.Get("Bulk_Dependencies"), [Texts.Get("Bulk_LinkAfterAbove"), Texts.Get("Bulk_NoLink")], link ? 0 : 1, i =>
        {
            link = i == 0;
            ViewState.Set(LinkKey, link ? null : "off");
            Refresh();
        }, [Texts.Get("Bulk_LinkAfterAboveTip"), null]);
        linkButton.MinWidth = 220;
        var startButton = PickerButton.ForChoices(Texts.Get("Bulk_Start"), [Texts.Get("Bulk_StartAfterPrevious"), Texts.Get("Bulk_StartEmpty")], startMode == BulkStart.AfterPrevious ? 0 : 1, i =>
        {
            startMode = i == 0 ? BulkStart.AfterPrevious : BulkStart.Blank;
            ViewState.Set(StartKey, startMode == BulkStart.Blank ? "blank" : null);
            Refresh();
        }, [Texts.Get("Bulk_StartAfterPreviousTip"), null]);
        startButton.MinWidth = 240;

        var settings = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        settings.Children.Add(PickerButton.Labeled(Texts.Get("Picker_Parent"), placeButton));
        settings.Children.Add(PickerButton.Labeled(Texts.Get("Bulk_Dependencies"), linkButton));
        settings.Children.Add(PickerButton.Labeled(Texts.Get("Bulk_Start"), startButton));

        // ---- 表
        var header = RowGrid();
        foreach (var (text, column) in ((string, int)[])[(Texts.Get("Bulk_ColumnKind"), 1), (Texts.Get("Bulk_ColumnName"), 2), (Texts.Get("Bulk_ColumnDates"), 3), (Texts.Get("Bulk_ColumnDays"), 4), (Texts.Get("Bulk_ColumnPredecessor"), 5)])
        {
            var label = new TextBlock { Text = text, Style = caption };
            Grid.SetColumn(label, column);
            header.Children.Add(label);
        }

        var scroller = new ScrollViewer { Content = rowsPanel, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 12, 0) };
        var panel = new StackPanel { Spacing = 12, Width = Width };
        panel.Children.Add(settings);
        panel.Children.Add(new StackPanel { Spacing = 6, Children = { header, scroller } });
        panel.Children.Add(new StackPanel { Spacing = 2, Children = { summary, problem } });

        dialog = AppDialog.Create(root, KeyHints.Name("wbs.bulk"), panel, Texts.Get("Common_Create"), multiline: true,
            extraHint: Texts.Get("Bulk_Hint"));
        dialog.Resources["ContentDialogMaxWidth"] = Width + 80;
        // Enter は次の行へ移るのに使うため、既定のボタンは置かず、作成のボタンは見た目だけ強調する
        dialog.DefaultButton = ContentDialogButton.None;
        dialog.PrimaryButtonStyle = AppResources.Style("AccentButtonStyle");

        bool submit = false;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (!CanCreate())
            {
                args.Cancel = true;
                return;
            }

            submit = true;
        };

        // Ctrl + Enter で作る（Enter は次の行へ移るのに使う）
        panel.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key == VirtualKey.Enter && KeyInput.IsDown(VirtualKey.Control))
            {
                e.Handled = true;
                if (CanCreate())
                {
                    submit = true;
                    dialog?.Hide();
                }
            }
        }), true);

        bool CanCreate() => resolved.Count > 0 && resolved.All(r => r.Problem is null);

        // ---- 行
        Row AddRow()
        {
            var row = new Row
            {
                View = RowGrid(),
                Number = new TextBlock { Style = caption, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center },
                KindButton = new PickerButton(Texts.Get("Bulk_ColumnKind")),
                TitleBox = new TextBox { PlaceholderText = Hints.Shown ? Texts.Get("Bulk_NamePlaceholderHint") : Texts.Get("Bulk_ColumnName") },
                DatesButton = new PickerButton(Texts.Get("Bulk_ColumnDates")),
                Days = new TextBlock { Style = caption, VerticalAlignment = VerticalAlignment.Center },
                // 入れ物は Tab で止まらないようにし、中のチップか「つなぐ」だけに止まる
                Link = new ContentControl { VerticalAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Left, IsTabStop = false },
            };
            AutomationProperties.SetName(row.TitleBox, Texts.Get("Bulk_ColumnName"));

            row.KindButton.Pick = async b =>
            {
                if (await ValuePickers.ChoiceAsync(b, Texts.Get("Bulk_ColumnKind"), [Texts.Get("Bulk_Parent"), Texts.Get("Field_Milestone")], row.Kind == BulkKind.Parent ? 0 : 1) is { } i)
                {
                    row.Kind = i == 0 ? BulkKind.Parent : BulkKind.Milestone;
                    Refresh();
                }
            };
            row.TitleBox.TextChanged += (_, _) =>
            {
                row.Title = row.TitleBox.Text;

                // 末尾の行に書き始めたら、次の入力の行を足しておく
                if (row == rows[^1] && row.Title.Trim().Length > 0)
                {
                    AddRow();
                }

                Refresh();
            };
            row.TitleBox.KeyDown += (_, e) =>
            {
                if (e.Key == VirtualKey.Enter && !KeyInput.IsDown(VirtualKey.Control))
                {
                    e.Handled = true;
                    int index = rows.IndexOf(row);
                    var next = index + 1 < rows.Count ? rows[index + 1] : AddRow();
                    next.TitleBox.Focus(FocusState.Keyboard);
                }
            };
            row.DatesButton.Pick = async b =>
            {
                var info = resolved.FirstOrDefault(r => r.Index == rows.IndexOf(row));
                if (row.Kind == BulkKind.Milestone)
                {
                    if (await ValuePickers.DateAsync(b, null, Texts.Get("SelectionBar_Due"), [info is null ? row.End : info.End]) is { } due)
                    {
                        row.End = due.Value;
                        Refresh();
                    }

                    return;
                }

                // 名前のない行は自動の値を持たないため、選んだ値のまま開く
                var (start, end) = info is null ? (row.Start, row.End) : (info.Start, info.End);
                if (await DateRangePicker.ShowAsync(b, Texts.Get("Field_Plan"), start, end, PlanPresets(info)) is { } range)
                {
                    // 自動で入れた開始のまま決めたなら、選んだ値にはせず、前の行に合わせて動き続けるようにする
                    row.Start = info is { StartIsAuto: true } && range.Start == info.Start ? null : range.Start;
                    row.End = range.End;
                    Refresh();
                }
            };

            Place(row.View, row.Number, 0);
            Place(row.View, row.KindButton, 1);
            Place(row.View, row.TitleBox, 2);
            Place(row.View, row.DatesButton, 3);
            Place(row.View, row.Days, 4);
            Place(row.View, row.Link, 5);
            rows.Add(row);
            rowsPanel.Children.Add(row.View);
            return row;
        }

        // 予定の候補: 開始が決まっていれば、開始からの期間（1 週間など）と前の行と同じ期間。決まっていなければ、ほかの予定と同じ候補
        IReadOnlyList<DateRangePreset> PlanPresets(BulkResolvedRow? info)
        {
            if (info?.Start is not { } s)
            {
                return ValuePickers.PlanPresets;
            }

            var presets = new List<DateRangePreset>
            {
                new(Texts.Get("Bulk_OneWeek"), _ => (s, BulkPlan.EndAfter(s, BulkSpan.OneWeek) ?? s)),
                new(Texts.Get("Bulk_TwoWeeks"), _ => (s, BulkPlan.EndAfter(s, BulkSpan.TwoWeeks) ?? s)),
                new(Texts.Get("Bulk_OneMonth"), _ => (s, BulkPlan.EndAfter(s, BulkSpan.OneMonth) ?? s)),
            };
            if (BulkPlan.PreviousWorkingDays(resolved, info.Index) is { } n)
            {
                presets.Add(new(Texts.Count("Bulk_SameAsPrevious", n), _ => (s, BulkPlan.EndAfter(s, BulkSpan.SameAsPrevious, n) ?? s)));
            }

            return presets;
        }

        // ---- 自動の値を入れ直して、表示を合わせる
        void Refresh()
        {
            resolved = BulkPlan.Resolve([.. rows.Select(r => r.ToInput())], startMode, link);
            var today = AppClock.Today;
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var info = resolved.FirstOrDefault(r => r.Index == i);
                bool milestone = row.Kind == BulkKind.Milestone;
                row.Number.Text = (i + 1).ToString(CultureInfo.InvariantCulture);
                row.KindButton.SetValue(milestone ? Texts.Get("Field_Milestone") : Texts.Get("Bulk_Parent"), "");
                if (milestone)
                {
                    MilestoneVisuals.Apply(row.KindButton.Icon);
                }
                else
                {
                    KindVisuals.Apply(row.KindButton.Icon, TaskKind.Task);
                }

                row.KindButton.Icon.Visibility = Visibility.Visible;
                // 名前のない行は作らないため、選んだ日程を薄く示すだけにする（名前を入れると使われる）。
                // 名前のある行は、自動の値だけでできている日程を薄い斜体で示す（親タスクは、開始が自動で終了が未定のとき）
                var (start, end) = info is null ? (row.Start, row.End) : (info.Start, info.End);
                row.DatesButton.SetValue(milestone ? (end is { } due ? DateText.Short(due, today) : null) : DateText.Range(start, end), "\uE787",
                    placeholder: milestone ? Texts.Get("Bulk_ChooseDue") : Texts.Get("Bulk_ChoosePlan"),
                    isAuto: info is not null && (milestone ? info.EndIsAuto : info is { StartIsAuto: true, End: null }),
                    isPending: info is null);
                row.Days.Text = info?.WorkingDays is { } d ? Texts.Count("Bulk_Days", d) : "";
                row.Link.Content = LinkView(row, info);
            }

            var first = resolved.Select(r => r.Start ?? r.End).FirstOrDefault(d => d is not null);
            var last = resolved.Select(r => r.End).LastOrDefault(d => d is not null);
            summary.Text = resolved.Count == 0
                ? Texts.Get("Bulk_SummaryEmpty")
                : first is { } f ? Texts.Format("Bulk_SummaryWithDates", Counts(resolved), DateText.Range(f, last is { } l && l >= f ? l : null)) : Counts(resolved);
            problem.Text = string.Join(Texts.Get("Common_HintSeparator"), resolved.Where(r => r.Problem is not null).Select(r => Texts.Format("Bulk_RowProblem", r.Index + 1, r.Problem)));
            if (dialog is not null)
            {
                dialog.PrimaryButtonText = resolved.Count > 0 ? Texts.Format("Bulk_CreateCounts", Counts(resolved)) : Texts.Get("Common_Create");
                dialog.IsPrimaryButtonEnabled = CanCreate();
            }
        }

        // 先行の欄: つないでいれば「⇢ 1 要件定義 ✕」、外していれば「つなぐ」
        FrameworkElement? LinkView(Row row, BulkResolvedRow? info)
        {
            if (info is null || !info.CanLink || !link)
            {
                return new TextBlock { Text = "—", Style = caption, Foreground = ThemeResources.Brush("TextFillColorTertiaryBrush") };
            }

            if (info.Predecessor is { } p)
            {
                var name = $"{p + 1} {rows[p].Title.Trim()}";
                var chip = new Button
                {
                    Content = new TextBlock { Text = $"⇢ {name}　✕", TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 },
                    Padding = new Thickness(10, 2, 10, 2),
                    MinHeight = 0,
                    BorderThickness = new Thickness(0),
                    Background = ThemeResources.Brush("SubtleFillColorSecondaryBrush"),
                };
                Pill.SetIsEnabled(chip, true);
                AutomationProperties.SetName(chip, Texts.Format("Bulk_UnlinkName", name));
                ToolTipService.SetToolTip(chip, Texts.Format("Bulk_UnlinkName", rows[p].Title.Trim()));
                chip.Click += (_, _) =>
                {
                    row.Unlinked = true;
                    Refresh();
                };
                return chip;
            }

            var relink = new HyperlinkButton { Content = Texts.Get("Bulk_Relink"), Padding = new Thickness(4, 0, 4, 0) };
            AutomationProperties.SetName(relink, Texts.Get("Bulk_LinkAfterAbove"));
            relink.Click += (_, _) =>
            {
                row.Unlinked = false;
                Refresh();
            };
            return relink;
        }

        AddRow();
        Refresh();
        dialog.Opened += (_, _) => rows[0].TitleBox.Focus(FocusState.Programmatic);

        await dialog.ShowAsync();
        if (!submit || resolved.Count == 0)
        {
            return;
        }

        await CreateAsync(project, plan, parent, resolved);
    }

    /// <summary>
    /// マイルストーンの行は GitHub の Milestone として先に作り、親タスクの行は上から順にタスクとして作る。
    /// 先行は、同じ操作で作ったばかりのタスクの Issue を指す（送信のときに本当の ID へ付け替わる）。
    /// 親タスクは、作ったマイルストーンを含めた期日から、所属するマイルストーンが決まる（要件 F-MS-02）。
    /// </summary>
    private static async Task CreateAsync(Project project, TaskTree plan, TaskNode? parent, IReadOnlyList<BulkResolvedRow> resolved)
    {
        var services = App.Current.Services;

        // マイルストーンは Milestone を作るため、つながっていないと作れない。作れなければ何も作らない
        var milestones = new List<Milestone>();
        var milestoneRows = resolved.Where(r => r.Kind == BulkKind.Milestone).ToList();
        if (milestoneRows.Count > 0)
        {
            var tasksBefore = await services.Store.GetTasksAsync(project.Id);
            try
            {
                foreach (var r in milestoneRows)
                {
                    milestones.Add(await services.Workspace.CreateMilestoneAsync(project, r.Title, r.End));
                }
            }
            catch (Exception ex) when (ex is GitHubException or HttpRequestException or InvalidOperationException)
            {
                App.Current.Shell?.ShowInfo(Texts.Get("Bulk_MilestoneFailedTitle"),
                    Texts.Format("Bulk_MilestoneFailedMessage", ex is InvalidOperationException ? ex.Message : SyncEngine.Describe(ex)));
                return;
            }

            await services.Edits.ReassignMilestonesAsync(project.Id, tasksBefore, project.Milestones);
            project = await services.Store.GetProjectAsync(project.Id) ?? project;
        }

        double order = parent switch
        {
            null => WbsOperations.PositionAfter(plan, null).SortOrder,
            { ChildNodes: [.., var last] } => WbsOperations.PositionAfter(plan, last).SortOrder,
            _ => WbsOperations.Spacing,
        };

        var issueIds = new Dictionary<int, string>();
        var created = new List<string>();
        foreach (var r in resolved.Where(r => r.Kind == BulkKind.Parent))
        {
            var blockedBy = r.Predecessor is { } p && issueIds.TryGetValue(p, out var id) ? new[] { id } : null;
            var task = await services.Edits.CreateAsync(project, r.Title,
                target: r.End,
                parentIssueId: parent?.Task.IssueId,
                sortOrder: order,
                kind: TaskKind.Task,
                start: r.Start,
                blockedBy: blockedBy);
            issueIds[r.Index] = task.IssueId;
            created.Add(task.ItemId);
            order += WbsOperations.Spacing;
        }

        // 作成は元に戻す履歴（項目の変更）に載らないため、下端の知らせの「元に戻す」で、作ったものをまとめて削除する。
        // 送信の途中で ID が付け替わっても追えるよう、知らせが出ているあいだは付け替えを追う
        void OnReplaced(object? sender, (string OldItemId, string NewItemId) ids)
        {
            lock (created)
            {
                int i = created.IndexOf(ids.OldItemId);
                if (i >= 0)
                {
                    created[i] = ids.NewItemId;
                }
            }
        }

        services.Store.ItemIdReplaced += OnReplaced;
        _ = Task.Delay(TimeSpan.FromMinutes(1)).ContinueWith(_ => services.Store.ItemIdReplaced -= OnReplaced, TaskScheduler.Default);
        App.Current.Shell?.ShowUndoable(Texts.Format("Bulk_Created", Counts(resolved)), async () =>
        {
            services.Store.ItemIdReplaced -= OnReplaced;
            List<string> ids;
            lock (created)
            {
                ids = [.. created];
            }

            ids.Reverse();
            foreach (var itemId in ids)
            {
                if (await services.Store.GetTaskAsync(itemId) is { } task)
                {
                    await services.Edits.DeleteAsync(task);
                }
            }

            if (milestones.Count > 0 && await services.Store.GetProjectAsync(project.Id) is { } current)
            {
                try
                {
                    var tasksBefore = await services.Store.GetTasksAsync(project.Id);
                    foreach (var m in milestones)
                    {
                        await services.Workspace.DeleteMilestoneAsync(current, m);
                    }

                    await services.Edits.ReassignMilestonesAsync(project.Id, tasksBefore, current.Milestones);
                }
                catch (Exception ex) when (ex is GitHubException or HttpRequestException)
                {
                    App.Current.Shell?.ShowInfo(Texts.Get("Bulk_MilestoneDeleteFailed"), SyncEngine.Describe(ex));
                    return;
                }
            }

            App.Current.Shell?.ShowToast(Texts.Format("Bulk_Undone", Counts(resolved)));
        });
    }

    /// <summary>
    /// 作るものの数（「親タスク 2・マイルストーン 1」）。マイルストーンは作業ではないため、タスクとまとめて「n 件」とは数えない。
    /// </summary>
    private static string Counts(IReadOnlyList<BulkResolvedRow> resolved)
    {
        int parents = resolved.Count(r => r.Kind == BulkKind.Parent);
        int milestones = resolved.Count - parents;
        return milestones == 0 ? Texts.Count("Bulk_Parents", parents)
            : parents == 0 ? Texts.Count("Bulk_Milestones", milestones)
            : Texts.Format("Bulk_Both", Texts.Count("Bulk_Parents", parents), Texts.Count("Bulk_Milestones", milestones));
    }

    /// <summary>番号・区分・名前・日程・期間・先行の列。</summary>
    private static Grid RowGrid()
    {
        var grid = new Grid { ColumnSpacing = 8 };
        foreach (var width in (ReadOnlySpan<GridLength>)[new(24), new(150), new(1, GridUnitType.Star), new(240), new(52), new(170)])
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        }

        return grid;
    }

    private static void Place(Grid grid, FrameworkElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }
}
