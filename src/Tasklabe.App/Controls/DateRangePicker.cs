using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Tasklabe.Animation;
using Tasklabe.App.Services;
using Tasklabe.Core.Editing;
using Windows.Foundation;
using Windows.System;

namespace Tasklabe.App.Controls;

/// <summary>
/// 日付のピッカーの候補（「今日から 1 週間」など）。<paramref name="Range"/> には今日の日付を渡す。
/// 1 日を選ぶピッカーの候補は、開始と終了を同じ日にする。
/// </summary>
public sealed record DateRangePreset(string Label, Func<DateOnly, (DateOnly Start, DateOnly End)> Range);

/// <summary>日付範囲のピッカーで決めた範囲。片側だけの範囲や、両方 null（消去）もある。</summary>
public readonly record struct DateRangeResult(DateOnly? Start, DateOnly? End);

/// <summary>
/// 暦で日付を選ぶピッカー（UI デザイン設計書 4.1.2 節）。起点（押したボタン、カードの期日、行）から面が育って開く。
/// 範囲を選ぶ形では、左に候補の列、右に 2 か月分の暦を置き、1 回目に押した日が開始、2 回目に押した日が終了になる。
/// そのあいだはポインターや矢印キーに合わせて範囲の帯が伸び縮みし、「適用」で決めると値がボタンへ戻って面が縮む。
/// 1 日を選ぶ形では、1 か月分の暦の日か候補を 1 回押せば決まって閉じる。
/// どちらも、キーボードで開いたときは上端に文字で入れる欄を置く。
/// </summary>
public sealed class DateRangePicker
{
    /// <summary>暦の週の始まり。日本の暦にそろえて日曜から並べる。</summary>
    private const DayOfWeek WeekStart = DayOfWeek.Sunday;
    private const double WindowEdge = 8;
    private const double MonthGap = 24;
    private const double RailWidth = 168;
    private const double PresetHeight = 32;
    private const double PresetSpacing = 2;

    private static readonly TimeSpan FaceOutDuration = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan MonthInDuration = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan MonthOutDuration = TimeSpan.FromMilliseconds(140);

    private readonly MorphPopup _morph;
    private readonly bool _single;
    private readonly bool _mixed;
    private readonly IReadOnlyList<DateRangePreset> _presets;
    private readonly bool _keyboard;
    private readonly DateOnly _today = AppClock.Today;
    private readonly DateRangeResult _committed;

    // 呼び出し側が値を保存して画面を描き直すあいだも、縮む動きを先に始めておく
    private readonly TaskCompletionSource<DateRangeResult?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TextBox? _input;
    private readonly TextBlock _error;
    private readonly Border _railMark;
    private readonly List<Button> _presetButtons = [];
    private readonly Grid _viewport = new();
    private readonly RollingText _summary = new();

    /// <summary>範囲の形の「適用」。キーで候補を選んだあとは、ここへフォーカスを移して Enter で決められるようにする。</summary>
    private Button? _apply;

    /// <summary>起点の値（ボタンの値、カードのチップ）の写し。下端の日付と組にして、起点とのあいだを入れ替えながら運ぶ。</summary>
    private readonly Image _ghost = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Stretch = Stretch.Fill };

    /// <summary>写しを撮る起点の要素。</summary>
    private FrameworkElement? _ghostSource;
    private readonly RollingText _count = new();
    private readonly int _monthCount;

    private StackPanel? _monthsHost;
    private List<RangeMonth> _months = [];
    private DateOnly _view;
    private DateOnly? _start;
    private DateOnly? _end;
    private DateOnly? _anchor;
    private DateOnly? _hover;
    private DateOnly? _lastShownStart;
    private int _activePreset = -1;
    private bool _markShown;
    private bool _opened;

    /// <param name="mixed">1 日を選ぶ形で、複数のタスクの値が混在している（いまの値を示さない）。</param>
    private DateRangePicker(FrameworkElement anchor, Point? position, string title, DateOnly? start, DateOnly? end,
        IReadOnlyList<DateRangePreset> presets, bool single, bool mixed)
    {
        _single = single;
        _mixed = mixed;
        _presets = presets;
        _keyboard = !LastInput.WasPointer;
        _committed = new DateRangeResult(start, end);
        _start = start;
        _end = end;
        _view = MonthOf(start ?? end ?? _today);

        var bounds = anchor.XamlRoot.Size;
        double fullWidth = 20 + RailWidth + 21 + RangeMonth.Cell * 7 * 2 + MonthGap;
        _monthCount = !single && bounds.Width >= fullWidth + WindowEdge * 2 ? 2 : 1;

        var caption = AppResources.Style("Text.Caption");

        // 上端の入力欄（キーボードで開いたときだけ）
        var inputArea = new StackPanel { Spacing = 4 };
        _error = new TextBlock { Style = caption, Foreground = ThemeResources.Brush("Viz.Delay"), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        if (_keyboard)
        {
            _input = new TextBox { PlaceholderText = single ? "日付を入力（9/25、+3、t など）" : "範囲を入力（10/5-10/9、t〜+4、〜10/9 など）" };
            AutomationProperties.SetName(_input, title + "を入力");
            _input.TextChanged += (_, _) => OnInputChanged();
            _input.KeyDown += OnInputKeyDown;
            inputArea.Children.Add(_input);
            inputArea.Children.Add(_error);
        }
        else
        {
            inputArea.Visibility = Visibility.Collapsed;
        }

        // 左の列の候補。選んでいる候補の下に印を敷き、選び直すと印が滑る
        var presetList = new StackPanel { Spacing = PresetSpacing };
        var presetStyle = AppResources.Style("RangePicker.Preset");
        for (int i = 0; i < presets.Count; i++)
        {
            int index = i;
            // 候補に 1〜9、10 番目は 0 の番号を、副テキストの色で左端に添える（UX 規約 UX-01）
            var number = new TextBlock
            {
                Text = i < 10 ? ((i + 1) % 10).ToString(System.Globalization.CultureInfo.InvariantCulture) : "",
                Style = caption,
                Width = 14,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var label = new TextBlock { Text = presets[i].Label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { number, label } };
            var button = new Button { Content = row, Style = presetStyle };
            AutomationProperties.SetName(button, presets[i].Label);
            button.Click += (_, _) =>
            {
                ChoosePreset(index);
                FocusApplyAfterKey();
            };
            _presetButtons.Add(button);
            presetList.Children.Add(button);
        }

        presetList.KeyDown += OnRailKeyDown;
        AutomationProperties.SetName(presetList, "候補");
        _railMark = new Border
        {
            Height = PresetHeight,
            VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = AppResources.CornerRadius("Radius.Control"),
            Background = ThemeResources.Brush("Morph.Highlight"),
        };
        Motion.VisualOf(_railMark).Opacity = 0;
        var rail = new Grid { Width = single ? 112 : RailWidth, Children = { _railMark, presetList } };

        // 暦。前後の月へのボタンを、月の見出しの両脇に重ねる
        var prev = NavButton("", "前の月", -1);
        var next = NavButton("", "次の月", 1);
        next.HorizontalAlignment = HorizontalAlignment.Right;
        _viewport.Width = RangeMonth.Cell * 7 * _monthCount + MonthGap * (_monthCount - 1);
        var calendars = new Grid { Children = { _viewport, prev, next } };
        calendars.PointerExited += (_, _) =>
        {
            if (_anchor is not null)
            {
                _hover = null;
                ShowRange();
            }
        };

        var body = new Grid { ColumnSpacing = 10 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var divider = new Rectangle { Width = 1, Fill = ThemeResources.Brush("DividerStrokeColorDefaultBrush") };
        Grid.SetColumn(divider, 1);
        Grid.SetColumn(calendars, 2);
        body.Children.Add(rail);
        body.Children.Add(divider);
        body.Children.Add(calendars);

        // 下端。選んでいる日付（範囲なら日数も）と、決める・やめる。1 日を選ぶ形は押せば決まるので「クリア」だけを置く
        var footerLine = new Rectangle { Height = 1, Fill = ThemeResources.Brush("DividerStrokeColorDefaultBrush") };
        _summary.WordStyle = AppResources.Style("Text.Body");
        _count.WordStyle = caption;
        var summaryArea = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(2, 0, 0, 0), Children = { _summary } };
        if (!single)
        {
            summaryArea.Children.Add(_count);
        }

        var clear = new Button { Content = "クリア", Style = AppResources.Style("SubtleButtonStyle") };
        clear.Click += (_, _) => Clear();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { clear } };
        if (!single)
        {
            var cancel = new Button { Content = "キャンセル" };
            cancel.Click += (_, _) => Close(null);
            var apply = _apply = new Button { Content = "適用", Style = AppResources.Style("AccentButtonStyle"), MinWidth = 76 };
            ToolTipService.SetToolTip(apply, "Ctrl + Enter");
            AutomationProperties.SetAcceleratorKey(apply, "Ctrl+Enter");
            apply.Click += (_, _) => Apply();
            actions.Children.Add(cancel);
            actions.Children.Add(apply);
        }

        var footer = new Grid { ColumnSpacing = 12 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(actions, 1);
        footer.Children.Add(summaryArea);
        footer.Children.Add(actions);

        // 起点の写しは、下端の日付と同じ場所に重ねて置く
        _ghost.Margin = new Thickness(2, 0, 0, 0);
        footer.Children.Add(_ghost);

        var content = new Grid { Padding = new Thickness(10), RowSpacing = 10 };

        // 数字キーで候補を選ぶ（UX 規約 UX-01）。入力欄では数字は日付の入力に使うため、入力欄の外にいるときだけ
        content.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown), true);
        foreach (var _ in Enumerable.Range(0, 4))
        {
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        Grid.SetRow(body, 1);
        Grid.SetRow(footerLine, 2);
        Grid.SetRow(footer, 3);
        content.Children.Add(inputArea);
        content.Children.Add(body);
        content.Children.Add(footerLine);
        content.Children.Add(footer);
        AutomationProperties.SetName(content, title);

        _morph = new MorphPopup(anchor, position, content);

        // 中身の出入りは面より少し遅れて始まり、面より先に消える
        _morph.Faces.AddRange([inputArea, body, footerLine, _count, actions]);

        _morph.Dismissed += (_, _) => Close(null);
        _morph.Grown += (_, _) => OnGrown();

        ShowMonths(_view, direction: 0);
        UpdateTexts(direction: 1, animate: false);
        UpdatePreset(animate: false);

        // 値（下端の日付）は、起点がボタンの形なら、起点の写しと組にして、起点とのあいだを入れ替えながら運ぶ。
        // 起点の値とは文字の大きさや示し方が違っても（カードの期日のチップ「10/8」と「10/8(木)」）、写しが起点と同じ姿で着くので、
        // 閉じて起点に切り替わる瞬間に何も入れ替わらない。ボタンでない起点では、中身と一緒に出入りさせる
        if (_morph.Trigger is { } trigger)
        {
            // ボタンの中身全体（アイコン、値、「⌄」）を写し、閉じ終えたときにボタンのどの部分も突然現れないようにする
            _ghostSource = trigger.Element is ContentControl { Content: FrameworkElement face }
                ? face
                : trigger.ValueText ?? trigger.Icon;
            if (_ghostSource is not null)
            {
                _morph.Carry(_ghost, _ghostSource, CarryFade.Out);
                _morph.Carry(_summary, _ghostSource, CarryFade.In);
            }
            else
            {
                _morph.Faces.Add(_summary);
            }
        }
        else
        {
            _morph.Faces.Add(_summary);
        }
    }

    /// <summary>開始日と終了日をまとめて選ぶ。やめたら null。</summary>
    /// <param name="trigger">押したボタンかセル。パネルはここから広がり、決めた値はここへ戻る。</param>
    /// <param name="title">「予定」「実績」など。読み上げに使う。</param>
    public static Task<DateRangeResult?> ShowAsync(FrameworkElement trigger, string title, DateOnly? start, DateOnly? end,
        IReadOnlyList<DateRangePreset> presets)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(presets);
        var picker = new DateRangePicker(trigger, null, title, start, end, presets, single: false, mixed: false);
        return picker.OpenAsync();
    }

    /// <summary>1 日を選ぶ。決めたら日付（「クリア」なら null）を持つ結果、やめたら null。</summary>
    /// <param name="anchor">起点（ボタン、カードの期日、行）。</param>
    /// <param name="position">右クリックした位置（anchor の中の座標）。</param>
    /// <param name="mixed">複数のタスクの値が混在している。</param>
    internal static async Task<Picked<DateOnly?>?> ShowDateAsync(FrameworkElement anchor, Point? position, string title,
        DateOnly? current, bool mixed, IReadOnlyList<DateRangePreset> presets)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(presets);
        var picker = new DateRangePicker(anchor, position, title, current, current, presets, single: true, mixed);
        return await picker.OpenAsync() is { } result ? new Picked<DateOnly?>(result.End) : null;
    }

    /// <summary>起点の写しを撮ってから開き、閉じるまで待つ（写しは起点を隠す前に撮る）。</summary>
    private async Task<DateRangeResult?> OpenAsync()
    {
        if (_ghostSource is { ActualWidth: > 0 } source && await MorphPopup.TrySnapshotAsync(source) is { } bitmap)
        {
            _ghost.Source = bitmap;
            _ghost.Width = source.ActualWidth;
            _ghost.Height = source.ActualHeight;
        }

        _morph.Open();
        return await _result.Task;
    }

    private static DateOnly MonthOf(DateOnly date) => new(date.Year, date.Month, 1);

    private static int MonthDiff(DateOnly a, DateOnly b) => (a.Year - b.Year) * 12 + a.Month - b.Month;

    private DateOnly? ShownStart => _anchor is { } a ? Min(a, _hover ?? a) : _start;

    private DateOnly? ShownEnd => _anchor is { } a ? Max(a, _hover ?? a) : _end;

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;

    private static string SummaryOf(DateOnly? start, DateOnly? end, bool mixed) =>
        mixed ? "複数の値" : DateText.Range(start, end) ?? "日付なし";

    // ---------------------------------------------------------------- 開く・閉じる

    /// <summary>面が広がり始めた（下端の日付は、起点がピッカーのボタンなら MorphPopup がボタンの値の位置から運ぶ）。</summary>
    private void OnGrown()
    {
        _opened = true;

        UpdatePreset(animate: false);

        // いまの値の候補（なければ先頭の「今日」）に置き、数字キーと ↑↓ ですぐ選べるようにする（UX 規約 UX-01）。
        // ポインターで開いたときは枠を出さない。入力欄（キーボードで開いたとき）へは Shift + Tab か、数字以外の文字を打ち始めると移る
        _presetButtons[_activePreset >= 0 ? _activePreset : 0].Focus(_input is not null ? FocusState.Keyboard : FocusState.Programmatic);
    }

    /// <summary>閉じる。決めた値（やめたなら元の値）を下端に示し、面を起点の大きさへ縮める（値は MorphPopup がボタンの位置へ運ぶ）。</summary>
    private void Close(DateRangeResult? result)
    {
        if (_morph.IsClosing)
        {
            return;
        }

        _result.TrySetResult(result);
        if (result is null)
        {
            _summary.SetText(SummaryOf(_committed.Start, _committed.End, _mixed), -1);
        }
        else if (_single)
        {
            _summary.SetText(SummaryOf(result.Value.End, result.Value.End, mixed: false));
        }

        _morph.Close(_keyboard);
    }

    // ---------------------------------------------------------------- 選ぶ

    private void Pick(DateOnly date)
    {
        if (_single)
        {
            Close(new DateRangeResult(date, date));
            return;
        }

        if (_anchor is not { } anchor)
        {
            _anchor = date;
            _hover = date;
        }
        else
        {
            _start = Min(anchor, date);
            _end = Max(anchor, date);
            _anchor = null;
            _hover = null;
        }

        Changed();
    }

    private void Hover(DateOnly date)
    {
        if (_anchor is not null && _hover != date)
        {
            _hover = date;
            ShowRange();
            UpdateTexts(direction: 0, animate: true);
        }
    }

    private void ChoosePreset(int index)
    {
        var (start, end) = _presets[index].Range(_today);
        if (_single)
        {
            Close(new DateRangeResult(end, end));
            return;
        }

        _start = start;
        _end = end;
        _anchor = null;
        _hover = null;
        Reveal(start, end);
        Changed();
    }

    private void Clear()
    {
        if (_single)
        {
            Close(new DateRangeResult(null, null));
            return;
        }

        _start = _end = null;
        _anchor = _hover = null;
        Changed();
    }

    private void Apply() => Close(_anchor is { } a ? new DateRangeResult(a, a) : new DateRangeResult(_start, _end));

    private void Changed()
    {
        ShowRange();
        UpdateTexts(direction: 0, animate: true);
        UpdatePreset(animate: true);
    }

    private void ShowRange()
    {
        foreach (var month in _months)
        {
            month.ShowRange(ShownStart, ShownEnd, animate: _opened);
        }
    }

    private void UpdateTexts(int direction, bool animate)
    {
        var start = ShownStart;
        var end = ShownEnd;
        if (direction == 0)
        {
            direction = (start ?? end) is { } now && _lastShownStart is { } before && now < before ? -1 : 1;
        }

        _lastShownStart = start ?? end;
        bool mixed = _mixed && start == _committed.Start && end == _committed.End;
        var summary = SummaryOf(start, end, mixed);
        _summary.WordForeground = mixed || (start is null && end is null) ? ThemeResources.Brush("TextFillColorTertiaryBrush") : null;
        _summary.SetText(summary, direction, animate);

        string count = (_anchor, start, end) switch
        {
            ({ } a, _, _) when _hover is null || _hover == a => "終了日を選んでください",
            (_, { } s, { } e) => $"{e.DayNumber - s.DayNumber + 1} 日間",
            (_, { }, null) => "終了日なし",
            (_, null, { }) => "開始日なし",
            _ => "",
        };
        _count.SetText(count, direction, animate);
    }

    private void UpdatePreset(bool animate)
    {
        _activePreset = _anchor is not null || _mixed ? -1 : Enumerable.Range(0, _presets.Count).FirstOrDefault(i =>
        {
            var (s, e) = _presets[i].Range(_today);
            return s == _start && e == _end;
        }, -1);

        for (int i = 0; i < _presetButtons.Count; i++)
        {
            if (i == _activePreset)
            {
                _presetButtons[i].Foreground = ThemeResources.Brush("TextFillColorPrimaryBrush");
            }
            else
            {
                _presetButtons[i].ClearValue(Control.ForegroundProperty);
            }
        }

        var mark = Motion.VisualOf(_railMark);
        if (_activePreset < 0)
        {
            Motion.EaseTo(mark, "Opacity", 0f, animate ? FaceOutDuration : TimeSpan.Zero);
            _markShown = false;
            return;
        }

        var offset = new Vector3(0, (float)(_activePreset * (PresetHeight + PresetSpacing)), 0);
        Motion.SpringTo(mark, "Translation", offset, Motion.Glide, animate: animate && _markShown);
        Motion.EaseTo(mark, "Opacity", 1f, animate ? TimeSpan.FromMilliseconds(160) : TimeSpan.Zero);
        _markShown = true;
    }

    // ---------------------------------------------------------------- 月

    private Button NavButton(string glyph, string name, int step)
    {
        var button = new Button
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top,
            Style = AppResources.Style("SubtleButtonStyle"),
            Content = new FontIcon { Glyph = glyph, FontSize = 12 },
        };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        button.Click += (_, _) => GoTo(_view.AddMonths(step));
        return button;
    }

    private void GoTo(DateOnly month)
    {
        int delta = MonthDiff(month, _view);
        if (delta != 0)
        {
            ShowMonths(month, Math.Sign(delta));
        }
    }

    /// <summary>範囲が見えていなければ、範囲の始まりの月へめくる。</summary>
    private void Reveal(DateOnly? start, DateOnly? end)
    {
        var last = _view.AddMonths(_monthCount - 1);
        bool fits = (start is not { } s || MonthDiff(s, _view) >= 0) && (end is not { } e || MonthDiff(e, last) <= 0);
        if (!fits && (start ?? end) is { } first)
        {
            GoTo(MonthOf(first));
        }
    }

    /// <summary>月を並べ直す。めくる向きがあれば、新しい月は進む向きから滑り込み、古い月は反対へ抜ける。</summary>
    private void ShowMonths(DateOnly first, int direction)
    {
        _view = first;
        var old = _monthsHost;
        _monthsHost = new StackPanel { Orientation = Orientation.Horizontal, Spacing = MonthGap };
        _months = [];
        for (int i = 0; i < _monthCount; i++)
        {
            var month = new RangeMonth(first.AddMonths(i), WeekStart, _today, Pick, Hover, OnDayKeyDown);
            month.ShowRange(ShownStart, ShownEnd, animate: false);
            _months.Add(month);
            _monthsHost.Children.Add(month);
        }

        _viewport.Children.Add(_monthsHost);
        var viewport = ElementCompositionPreview.GetElementVisual(_viewport);
        viewport.Clip ??= viewport.Compositor.CreateInsetClip();
        if (old is null)
        {
            return;
        }

        if (direction == 0 || !Motion.IsEnabled || !_opened)
        {
            _viewport.Children.Remove(old);
            return;
        }

        old.IsHitTestVisible = false;
        Motion.Conceal(old, new Vector3(-36 * direction, 0, 0), MonthOutDuration, () => _viewport.Children.Remove(old));
        Motion.Reveal(_monthsHost, new Vector3(48 * direction, 0, 0), MonthInDuration, travel: Motion.Slide);
    }

    private void FocusDay(DateOnly date, FocusState state)
    {
        if (_months.Select(m => m.DayButton(date)).FirstOrDefault(b => b is not null) is { } button)
        {
            button.Focus(state);
        }
        else if (_months.FirstOrDefault()?.DayButton(_view) is { } first)
        {
            first.Focus(state);
        }
    }

    // ---------------------------------------------------------------- キーボード

    private void OnDayKeyDown(DateOnly day, KeyRoutedEventArgs e)
    {
        int weekday = ((int)day.DayOfWeek - (int)WeekStart + 7) % 7;
        bool shift = KeyInput.IsDown(VirtualKey.Shift);
        DateOnly? next = e.Key switch
        {
            VirtualKey.Left => day.AddDays(-1),
            VirtualKey.Right => day.AddDays(1),
            VirtualKey.Up => day.AddDays(-7),
            VirtualKey.Down => day.AddDays(7),
            VirtualKey.Home => day.AddDays(-weekday),
            VirtualKey.End => day.AddDays(6 - weekday),
            VirtualKey.PageUp => day.AddMonths(shift ? -12 : -1),
            VirtualKey.PageDown => day.AddMonths(shift ? 12 : 1),
            _ => null,
        };
        if (next is not { } target)
        {
            return;
        }

        e.Handled = true;
        if (MonthDiff(target, _view) < 0)
        {
            ShowMonths(MonthOf(target), -1);
        }
        else if (MonthDiff(target, _view.AddMonths(_monthCount - 1)) > 0)
        {
            ShowMonths(MonthOf(target).AddMonths(-(_monthCount - 1)), 1);
        }

        if (_anchor is not null)
        {
            Hover(target);
        }

        FocusDay(target, FocusState.Keyboard);
    }

    /// <summary>
    /// 数字キーで、その番号の候補を選ぶ（1 日の形は選んで決め、範囲の形は範囲に入れて「適用」へ移る）。
    /// 入力欄にいるとき（「10/5」のように数字から書く）と、修飾キーを押しているときは選ばない。
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // 範囲の形は、どこにいても Ctrl + Enter で適用する（追加のダイアログの Ctrl + Enter と同じ）
        if (!_single && !_morph.IsClosing && e.Key == VirtualKey.Enter && KeyInput.IsDown(VirtualKey.Control))
        {
            e.Handled = true;
            Apply();
            return;
        }

        bool inInput = _input is not null && _input.FocusState != FocusState.Unfocused;
        bool modified = KeyInput.IsDown(VirtualKey.Control) || KeyInput.IsDown(VirtualKey.Menu);
        if (_morph.IsClosing || inInput || modified)
        {
            return;
        }

        // 数字以外の文字（t、+、/、〜 など）を打ち始めたら、入力欄へ移ってそのまま入力させる（UX 規約 UX-04）
        if (_input is not null && IsTextKey(e.Key))
        {
            _input.Focus(FocusState.Keyboard);
            _input.SelectionStart = _input.Text.Length;
            return;
        }

        if (KeyInput.IsDown(VirtualKey.Shift) || KeyInput.Digit(e.Key) is not { } n || n > Math.Min(_presets.Count, 10))
        {
            return;
        }

        e.Handled = true;
        ChoosePreset(n - 1);
        FocusApplyAfterKey();
    }

    /// <summary>入力欄に書く文字のキー（英字、記号、テンキーの + - /、Shift を押した数字キーの記号）。数字そのものは候補を選ぶのに使う。</summary>
    private static bool IsTextKey(VirtualKey key) =>
        key is >= VirtualKey.A and <= VirtualKey.Z or VirtualKey.Add or VirtualKey.Subtract or VirtualKey.Divide
            || (int)key is 187 or 188 or 189 or 190 or 191 or 219 or 220 or 221 or 222 or 226
            || (key is >= VirtualKey.Number0 and <= VirtualKey.Number9 && KeyInput.IsDown(VirtualKey.Shift));

    /// <summary>
    /// 範囲の形でキー（数字キー、候補の列の Enter・Space）で候補を選んだら、「適用」へフォーカスを移し、続けて Enter で決められるようにする。
    /// ポインターで選んだときは動かさない。
    /// </summary>
    private void FocusApplyAfterKey()
    {
        if (!_single && !LastInput.WasPointer && !_morph.IsClosing)
        {
            _apply?.Focus(FocusState.Keyboard);
        }
    }

    private void OnRailKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Up or VirtualKey.Down))
        {
            return;
        }

        int at = _presetButtons.FindIndex(b => b.FocusState != FocusState.Unfocused);
        if (at < 0)
        {
            return;
        }

        e.Handled = true;
        int step = e.Key == VirtualKey.Down ? 1 : -1;
        _presetButtons[(at + step + _presetButtons.Count) % _presetButtons.Count].Focus(FocusState.Keyboard);
    }

    private void OnInputChanged()
    {
        _error.Visibility = Visibility.Collapsed;
        var text = _input!.Text;
        if (text.Trim().Length > 0 && TryParse(text, out var start, out var end))
        {
            _start = start;
            _end = end;
            _anchor = _hover = null;
            Reveal(start, end);
            Changed();
        }
    }

    private void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        if (TryParse(_input!.Text, out var start, out var end))
        {
            Close(new DateRangeResult(start, end));
        }
        else
        {
            _error.Text = _single
                ? "日付として読み取れません（例: 9/25、2026/9/25、+3、t）"
                : "範囲として読み取れません（例: 10/5-10/9、t〜+4、〜10/9）";
            _error.Visibility = Visibility.Visible;
        }
    }

    /// <summary>入力欄の文字を読む。1 日を選ぶ形では 1 つの日付だけを読み、開始と終了を同じ日にする。</summary>
    private bool TryParse(string text, out DateOnly? start, out DateOnly? end)
    {
        if (!_single)
        {
            return DateRangeInput.TryParse(text, _today, out start, out end);
        }

        bool ok = DateInput.TryParse(text, _today, out var date);
        start = end = date;
        return ok;
    }
}
