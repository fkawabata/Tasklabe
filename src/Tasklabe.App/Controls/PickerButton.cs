using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Tasklabe.App.Services;

namespace Tasklabe.App.Controls;

/// <summary>
/// いまの値を表示し、押すか Enter・Space でピッカーを開くボタン（UX 規約 UX-02、UX-19）。
/// ダイアログ・詳細パネル・設定の中の「候補から選ぶ項目」は、ドロップダウンではなくこれを使う。
/// 値がないときは「—」を薄く表示する。
/// </summary>
public sealed partial class PickerButton : Button, IPickerTrigger
{
    private readonly FontIcon _icon = new() { FontSize = 14, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    private readonly FontIcon _chevron;
    private readonly TextBlock _text = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly string _name;
    private IReadOnlyList<string> _labels = [];
    private IReadOnlyList<string?>? _details;
    private int _selectedIndex = -1;

    /// <param name="name">項目の名前（読み上げとピッカーの見出しに使う）。</param>
    public PickerButton(string name)
    {
        _name = name;
        Style = AppResources.Style("MorphSelect.Trigger");
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Padding = new Thickness(10, 5, 8, 6);
        MinHeight = 32;

        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(_icon);
        Grid.SetColumn(_text, 1);
        grid.Children.Add(_text);
        _chevron = new FontIcon
        {
            Glyph = "",
            FontSize = 10,
            Foreground = ThemeResources.Brush("TextFillColorTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(_chevron, 2);
        grid.Children.Add(_chevron);
        Content = grid;

        // ダイアログの中のボタンはウィンドウの根元を通らないため、ここでも入力を記録する（キーボードで開いたかの判定）
        LastInput.Track(this);
        Click += async (_, _) =>
        {
            if (Pick is { } pick)
            {
                await pick(this);
            }
        };
        SetValue(null);
    }

    /// <summary>押したときに開くピッカー。</summary>
    public Func<PickerButton, Task>? Pick { get; set; }

    /// <summary>表示する値。null や空なら「—」（または項目名）を薄く表示する（UX 規約 UX-30）。</summary>
    /// <param name="placeholder">値がないときに薄く示す文字。小さなボタン（追加のダイアログ）では項目名にする。</param>
    /// <param name="isAuto">自動で決めた値（利用者が選んでいない値）。薄い斜体で示す。</param>
    /// <param name="isPending">まだ使われない値（名前を入れる前の行の日程など）。薄く示す。</param>
    public void SetValue(string? text, string? glyph = null, string? glyphBrushKey = null, string? placeholder = null, bool isAuto = false,
        bool isPending = false)
    {
        bool empty = string.IsNullOrEmpty(text);
        _text.Text = empty ? placeholder ?? "—" : text!;
        _text.Foreground = empty ? ThemeResources.Brush("TextFillColorTertiaryBrush")
            : isAuto || isPending ? ThemeResources.Brush("TextFillColorSecondaryBrush")
            : ThemeResources.Brush("TextFillColorPrimaryBrush");
        _text.FontStyle = isAuto && !empty ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;
        _icon.Visibility = glyph is null ? Visibility.Collapsed : Visibility.Visible;
        _icon.Glyph = glyph ?? "";
        if (glyphBrushKey is not null)
        {
            _icon.Foreground = ThemeResources.Brush(glyphBrushKey);
        }
        else
        {
            _icon.ClearValue(IconElement.ForegroundProperty);
        }

        AutomationProperties.SetName(this, $"{_name}: {(empty ? "なし" : text + (isAuto ? "（自動）" : ""))}。押すと変更できます");
    }

    /// <summary>アイコンを直接変える（区分の ◆ のようにフォントの違うもの）。</summary>
    public FontIcon Icon => _icon;

    FrameworkElement IPickerTrigger.Element => this;

    FrameworkElement? IPickerTrigger.Icon => _icon;

    TextBlock? IPickerTrigger.ValueText => _text;

    FontIcon? IPickerTrigger.Chevron => _chevron;

    // ---------------------------------------------------------------- 決まった候補から選ぶ（設定などの ComboBox の代わり）

    /// <summary>決まった候補から 1 つを選ぶボタンにする。</summary>
    /// <param name="tips">選択肢の意味。ピッカーの中でツールチップで添える。</param>
    public static PickerButton ForChoices(string name, IReadOnlyList<string> labels, int selectedIndex, Action<int> changed,
        IReadOnlyList<string?>? details = null, IReadOnlyList<string?>? tips = null)
    {
        var button = new PickerButton(name) { _labels = labels, _details = details };
        button.SelectedIndex = selectedIndex;
        button.Pick = async b =>
        {
            if (await ValuePickers.ChoiceAsync(b, name, b._labels, b._selectedIndex, b._details, tips) is { } index && index != b._selectedIndex)
            {
                b.SelectedIndex = index;
                changed(index);
            }
        };
        return button;
    }

    /// <summary>候補を入れ替える（読み込みが済んでから候補が決まる項目など）。</summary>
    public void SetChoices(IReadOnlyList<string> labels, int selectedIndex, IReadOnlyList<string?>? details = null)
    {
        _labels = labels;
        _details = details;
        SelectedIndex = selectedIndex;
    }

    /// <summary>選んでいる候補の番号（<see cref="ForChoices"/> で作ったとき）。</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            _selectedIndex = value;
            SetValue(value >= 0 && value < _labels.Count ? _labels[value] : null);
        }
    }

    /// <summary>項目名を上に置いた形にする（ダイアログや設定で使う）。</summary>
    public static StackPanel Labeled(string header, FrameworkElement control) => new()
    {
        Spacing = 4,
        Children =
        {
            new TextBlock { Text = header, Style = AppResources.Style("Text.Caption") },
            control,
        },
    };
}
