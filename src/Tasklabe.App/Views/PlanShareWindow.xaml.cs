using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Tasklabe.App.Controls;
using Tasklabe.App.Services;
using Tasklabe.Core.Calendar;
using Tasklabe.Core.Domain;
using Tasklabe.Core.Export;
using Tasklabe.Core.Wbs;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;

namespace Tasklabe.App.Views;

/// <summary>
/// 共有ビューのウィンドウ（要件 F-UI-GT-10、UI デザイン設計書 3.3.6 節）。
/// 計画を他の人に説明するためのビューを画面いっぱいにネイティブで描き、Mermaid のコードと画像へ書き出せる。
/// 範囲と表示の設定を変えると、すぐにビューを描き直す。
/// </summary>
public sealed partial class PlanShareWindow : Window
{
    /// <summary>プロジェクトごとに最後に使った設定（アプリを閉じるまで覚える）。</summary>
    private static readonly Dictionary<string, (OutlineOptions Options, bool Markdown)> LastOptions = [];

    private readonly Project _project;
    private readonly TaskTree _tree;
    private readonly Func<TaskNode, bool>? _visible;
    private readonly WorkCalendarRules _calendar;
    private readonly bool _loading;
    private readonly Debouncer _render;
    private readonly Debouncer _badge;
    private GanttOutline? _outline;
    private string _code = "";

    private static readonly (OutlineRange Value, string Label)[] Ranges =
    [
        (OutlineRange.All, "すべての期間"),
        (OutlineRange.AroundToday, "今日の前後（1 週前〜4 週後）"),
        (OutlineRange.ThisMonth, "今月"),
        (OutlineRange.NextMonth, "来月"),
        (OutlineRange.Custom, "期間を指定"),
    ];

    private static readonly (OutlineSections Value, string Label)[] SectionChoices =
    [
        (OutlineSections.TopLevel, "最上位の親タスクごと"),
        (OutlineSections.Assignee, "担当者ごと"),
        (OutlineSections.None, "なし"),
    ];

    private OutlineRange _range;
    private OutlineSections _sections;
    private DateOnly? _from;
    private DateOnly? _to;
    private bool _markdown = true;
    private PickerButton? _fromButton;
    private PickerButton? _toButton;

    /// <param name="filterName">画面で担当者を絞り込んでいるときの絞り込みの名前（絞り込んでいなければ null）。</param>
    internal PlanShareWindow(Project project, TaskTree tree, Func<TaskNode, bool>? visible, string? filterName, WorkCalendarRules calendar)
    {
        _project = project;
        _tree = tree;
        _visible = visible;
        _calendar = calendar;
        InitializeComponent();
        _render = new Debouncer(DispatcherQueue, TimeSpan.FromMilliseconds(150));
        _badge = new Debouncer(DispatcherQueue, TimeSpan.FromSeconds(3));

        Title = $"共有ビュー - {ProjectDisplay.Name(project)}";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Tasklabe.ico"));
        Root.RequestedTheme = App.Current.Theme;
        FitToScreen();

        FilterNote.Text = filterName is null ? "" : $"画面の担当者の絞り込み（{filterName}）に従います。";
        FilterNote.Visibility = filterName is null ? Visibility.Collapsed : Visibility.Visible;

        // 前回の設定（なければ既定）を画面へ
        _loading = true;
        var (options, markdown) = LastOptions.GetValueOrDefault(project.Id, (new OutlineOptions { Title = project.Title }, true));
        TitleBox.Text = options.Title;
        _range = options.Range;
        _from = options.From;
        _to = options.To;
        _sections = options.Sections;
        _markdown = markdown;
        BuildPickers();
        MilestonesBox.IsChecked = options.Milestones;
        ParentBarsBox.IsChecked = options.ParentBars;
        CompletedBox.IsChecked = options.Completed;
        CanceledBox.IsChecked = options.Canceled;
        StatusBox.IsChecked = options.StatusColors;
        AssigneeBox.IsChecked = options.AssigneeInName;
        ProgressBox.IsChecked = options.ProgressInName;
        HolidaysBox.IsChecked = options.Holidays;
        TodayBox.IsChecked = options.TodayMarker;
        CustomRange.Visibility = options.Range == OutlineRange.Custom ? Visibility.Visible : Visibility.Collapsed;
        _loading = false;

        Render();
    }

    private static DateOnly Today => AppClock.Today;

    private bool Markdown => _markdown;

    /// <summary>期間・グループ・形式・日付のピッカーボタンを作る。</summary>
    private void BuildPickers()
    {
        RangeHost.Content = PickerButton.Labeled("期間", PickerButton.ForChoices("期間", [.. Ranges.Select(r => r.Label)],
            Array.FindIndex(Ranges, r => r.Value == _range), i =>
            {
                _range = Ranges[i].Value;
                OnOptionChanged(this, EventArgs.Empty);
            }));
        SectionsHost.Content = PickerButton.Labeled("グループ", PickerButton.ForChoices("グループ", [.. SectionChoices.Select(r => r.Label)],
            Array.FindIndex(SectionChoices, r => r.Value == _sections), i =>
            {
                _sections = SectionChoices[i].Value;
                OnOptionChanged(this, EventArgs.Empty);
            }));
        FormatHost.Content = PickerButton.Labeled("Mermaid の形式", PickerButton.ForChoices("Mermaid の形式",
            ["Markdown（```mermaid で囲む）", "Mermaid のコードだけ"], _markdown ? 0 : 1, i =>
            {
                _markdown = i == 0;
                OnOptionChanged(this, EventArgs.Empty);
            }, ["GitHub の Issue や README に貼ると、そのまま図として表示されます", "Mermaid Live Editor やほかのツールに貼るときに使います"]));

        _fromButton = DateButton("期間の開始日", "開始", () => _from, d => _from = d);
        _toButton = DateButton("期間の終了日", "終了", () => _to, d => _to = d);
        FromHost.Content = _fromButton;
        ToHost.Content = _toButton;
    }

    private PickerButton DateButton(string name, string placeholder, Func<DateOnly?> get, Action<DateOnly?> set)
    {
        var button = new PickerButton(name);
        void Show() => button.SetValue(get() is { } d ? DateText.Short(d) : null, "\uE787", placeholder: placeholder);
        Show();
        button.Pick = async b =>
        {
            if (await Controls.ValuePickers.DateAsync(b, null, name, [get()]) is { } picked)
            {
                set(picked.Value);
                Show();
                OnOptionChanged(this, EventArgs.Empty);
            }
        };
        return button;
    }

    private string Output => Markdown ? MermaidGantt.AsMarkdown(_code) : _code;

    private bool HasItems => _outline is { ItemCount: > 0 };

    /// <summary>画面の許す限り大きく開く（作業領域の 90 %、中央）。</summary>
    private void FitToScreen()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        int width = (int)(area.Width * 0.9);
        int height = (int)(area.Height * 0.9);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
    }

    // ================================================================ 設定

    private OutlineOptions Options() => new()
    {
        Title = TitleBox.Text,
        Range = _range,
        From = _from,
        To = _to,
        Sections = _sections,
        Milestones = MilestonesBox.IsChecked == true,
        ParentBars = ParentBarsBox.IsChecked == true,
        Completed = CompletedBox.IsChecked == true,
        Canceled = CanceledBox.IsChecked == true,
        StatusColors = StatusBox.IsChecked == true,
        AssigneeInName = AssigneeBox.IsChecked == true,
        ProgressInName = ProgressBox.IsChecked == true,
        Holidays = HolidaysBox.IsChecked == true,
        TodayMarker = TodayBox.IsChecked == true,
    };

    private void OnOptionChanged(object sender, object e)
    {
        if (_loading)
        {
            return;
        }

        CustomRange.Visibility = _range == OutlineRange.Custom ? Visibility.Visible : Visibility.Collapsed;

        // 入力が続くあいだ（タイトルの入力など）は待ってから描き直す
        _render.Run(Render);
    }

    /// <summary>設定から計画の形を作り、ビュー・コード・要約を描き直す。</summary>
    private void Render()
    {
        var options = Options();
        LastOptions[_project.Id] = (options, Markdown);

        var outline = PlanOutline.Build(_tree, options, Today, _calendar, _visible, _project.Milestones);
        _outline = outline;
        _code = MermaidGantt.ToCode(outline, _calendar);
        Outline.Show(outline, Today);
        CodeBox.Text = Output.Replace("\n", "\r", StringComparison.Ordinal);

        var span = outline.Span is { } s ? $" · {DateText.Long(s.Start)}〜{DateText.Long(s.End)}" : "";
        var skipped = outline.Unscheduled > 0 ? $" · 予定のないタスク {outline.Unscheduled} 件は含めません" : "";
        SummaryText.Text = $"{ProjectDisplay.Name(_project)} · {outline.ItemCount} 件{span}{skipped}";
        CopyButton.IsEnabled = CopyImageButton.IsEnabled = SaveButton.IsEnabled = HasItems;
    }

    private void OnViewChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        bool code = ReferenceEquals(ViewSelector.SelectedItem, CodeTab);
        CodeBox.Visibility = code ? Visibility.Visible : Visibility.Collapsed;
        ViewScroller.Visibility = code ? Visibility.Collapsed : Visibility.Visible;
    }

    // ================================================================ 書き出し

    private void OnCopy(object sender, RoutedEventArgs e) => CopyCode();

    private void CopyCode()
    {
        if (!HasItems)
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(Output);
        Clipboard.SetContent(package);
        ShowBadge(Markdown ? "Mermaid を Markdown としてコピーしました" : "Mermaid のコードをコピーしました");
    }

    private async void OnCopyImage(object sender, RoutedEventArgs e) => await CopyImageAsync();

    private async Task CopyImageAsync()
    {
        if (!HasItems || await Outline.RenderPngAsync() is not { } png)
        {
            return;
        }

        var package = new DataPackage();
        package.SetBitmap(RandomAccessStreamReference.CreateFromStream(png));
        Clipboard.SetContent(package);
        ShowBadge("画像をコピーしました");
    }

    private async void OnSave(object sender, RoutedEventArgs e) => await SaveAsync();

    private async Task SaveAsync()
    {
        if (!HasItems)
        {
            return;
        }

        var picker = new FileSavePicker { SuggestedFileName = FileName(), SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeChoices.Add("Markdown（Mermaid の図）", [".md"]);
        picker.FileTypeChoices.Add("Mermaid", [".mmd"]);
        picker.FileTypeChoices.Add("画像（PNG）", [".png"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (await picker.PickSaveFileAsync() is not { } file)
        {
            return;
        }

        // ファイルの種類で書き出す中身を決める（Markdown ならコードブロックで囲む）
        switch (file.FileType.ToLowerInvariant())
        {
            case ".png":
                if (await Outline.RenderPngAsync() is not { } png)
                {
                    return;
                }

                using (png)
                using (var target = await file.OpenAsync(FileAccessMode.ReadWrite))
                {
                    target.Size = 0;
                    await RandomAccessStream.CopyAndCloseAsync(png.GetInputStreamAt(0), target.GetOutputStreamAt(0));
                }

                break;
            case ".md":
                await FileIO.WriteTextAsync(file, MermaidGantt.AsMarkdown(_code));
                break;
            default:
                await FileIO.WriteTextAsync(file, _code);
                break;
        }

        ShowBadge($"{file.Name} に書き出しました");
    }

    private string FileName()
    {
        var name = string.IsNullOrWhiteSpace(TitleBox.Text) ? _project.Title : TitleBox.Text.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return $"{name}-gantt";
    }

    private void ShowBadge(string text)
    {
        CopiedText.Text = text;
        CopiedBadge.Visibility = Visibility.Visible;
        _badge.Run(() => CopiedBadge.Visibility = Visibility.Collapsed);
    }

    // ================================================================ キー

    private async void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        static bool Down(VirtualKey key) => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool ctrl = Down(VirtualKey.Control);
        switch (e.Key)
        {
            case VirtualKey.Escape:
                Close();
                e.Handled = true;
                break;
            case VirtualKey.S when ctrl:
                e.Handled = true;
                await SaveAsync();
                break;

            // 入力欄で文字を選んでいるときの Ctrl + C は、その文字のコピーに任せる
            case VirtualKey.C when ctrl && !(FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox { SelectionLength: > 0 }):
                e.Handled = true;
                if (Down(VirtualKey.Shift))
                {
                    await CopyImageAsync();
                }
                else
                {
                    CopyCode();
                }

                break;
        }
    }
}
