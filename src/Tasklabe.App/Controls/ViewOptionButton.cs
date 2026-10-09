using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Tasklabe.App.Services;
using Tasklabe.Core.Kanban;

namespace Tasklabe.App.Controls;

/// <summary>並び順・グループの候補（値、名前、補足、アイコン）。</summary>
internal sealed record ViewOption<T>(T Value, string Label, string? Detail, string Glyph);

/// <summary>
/// 並び順とグループの候補とアイコン（UI デザイン設計書 4.4 節）。リストとカンバンで同じ候補・同じアイコンを使う。
/// </summary>
internal static class ViewOptions
{
    public const string OrderingGlyph = "\uE8CB";
    public const string GroupingGlyph = "\uF168";

    /// <summary>並び順の候補。計画の並びを持たない画面（マイタスク）では、既定を期日の順とする。</summary>
    public static IReadOnlyList<ViewOption<TaskOrdering>> Orderings(bool hasPlanOrder)
    {
        List<ViewOption<TaskOrdering>> list = hasPlanOrder
            ? [new(TaskOrdering.Default, "計画の順", null, "\uE8FD"), new(TaskOrdering.Due, "期日", "早い順", "\uE787")]
            : [new(TaskOrdering.Default, "期日", "早い順", "\uE787")];
        list.Add(new(TaskOrdering.Updated, "更新", "新しい順", "\uE81C"));
        list.Add(new(TaskOrdering.Created, "作成", "新しい順", "\uE710"));
        list.Add(new(TaskOrdering.Estimate, "工数", "大きい順", "\uE916"));
        list.Add(new(TaskOrdering.Title, "タイトル", "名前の順", "\uE8D2"));
        return list;
    }

    public static string GlyphOf(KanbanGrouping grouping) => grouping switch
    {
        KanbanGrouping.Project => "\uE8F1",
        KanbanGrouping.Assignee => "\uE77B",
        KanbanGrouping.Parent => "\uE8B7",
        KanbanGrouping.Kind => "\uE7C1",
        _ => "\uEA37",
    };

    /// <summary>候補をアイコン付きのピッカーで選ぶ（UX 規約 UX-01）。選ばなければ null。</summary>
    public static async Task<ViewOption<T>?> PickAsync<T>(FrameworkElement anchor, string title, IReadOnlyList<ViewOption<T>> choices, T current)
        where T : struct, Enum
    {
        var options = choices.Select(c => new PickerOption(c.Label, c.Detail, c.Glyph, EqualityComparer<T>.Default.Equals(c.Value, current))).ToList();
        return await new QuickPicker(title, options).ShowAsync(anchor) is { Index: >= 0 } r ? choices[r.Index] : null;
    }
}

/// <summary>
/// ビューの切り替えの右端に置く、並び順・グループのボタン（UX 規約 UX-24、UX-25）。
/// 種類のアイコン、項目名、いまの値、「⌄」を並べたピッカーのボタンの形（<see cref="IPickerTrigger"/>）にし、
/// 押すとボタンがそのまま候補の面に育つ。狭いときは種類のアイコンと値のアイコンだけにする。
/// </summary>
public sealed partial class ViewOptionButton : Button, IPickerTrigger
{
    private readonly FontIcon _kindIcon;
    private readonly TextBlock _nameText = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeResources.Brush("TextFillColorSecondaryBrush") };
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly FontIcon _valueIcon = new() { FontSize = 14, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    private readonly FontIcon _chevron = new()
    {
        Glyph = "\uE70D",
        FontSize = 10,
        Foreground = ThemeResources.Brush("TextFillColorTertiaryBrush"),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(2, 0, 0, 0),
    };

    private readonly string _name;
    private readonly string _actionId;
    private string _label = "";

    /// <param name="name">項目名（並び順、グループ）。</param>
    /// <param name="glyph">種類のアイコン。</param>
    /// <param name="actionId">開くキーの操作（ツールチップにキーを示す）。</param>
    public ViewOptionButton(string name, string glyph, string actionId)
    {
        _name = name;
        _actionId = actionId;
        _nameText.Text = name + ":";
        _kindIcon = new FontIcon { Glyph = glyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        Style = AppResources.Style("MorphSelect.Trigger");
        Padding = new Thickness(10, 5, 8, 6);
        MinHeight = 32;
        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { _kindIcon, _nameText, _text, _valueIcon, _chevron },
        };

        // ツールバーのボタンはウィンドウの根元を通るが、ピッカーのボタンと同じくここでも入力を記録する（キーボードで開いたかの判定）
        LastInput.Track(this);
        Click += async (_, _) =>
        {
            if (Pick is { } pick)
            {
                await pick(this);
            }
        };
    }

    /// <summary>押したときに開くピッカー。</summary>
    public Func<ViewOptionButton, Task>? Pick { get; set; }

    /// <summary>いまの値。</summary>
    public void SetValue(string label, string glyph)
    {
        _label = label;
        _text.Text = label;
        _valueIcon.Glyph = glyph;
        var text = $"{_name}: {label}";
        ToolTipService.SetToolTip(this, KeyHints.Tip(_actionId, text));
        AutomationProperties.SetName(this, $"{text}。押すと変更できます");
    }

    /// <summary>狭いときの形（種類のアイコンと値のアイコンだけ）。この形では、ボタンの形の起点として扱わない。</summary>
    public bool IsCompact
    {
        get => _text.Visibility == Visibility.Collapsed;
        set
        {
            _nameText.Visibility = _text.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            _valueIcon.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>いまの値の名前。</summary>
    public string Label => _label;

    FrameworkElement IPickerTrigger.Element => this;

    FrameworkElement? IPickerTrigger.Icon => _kindIcon;

    TextBlock? IPickerTrigger.Prefix => _nameText;

    TextBlock? IPickerTrigger.ValueText => _text;

    FontIcon? IPickerTrigger.Chevron => _chevron;
}
