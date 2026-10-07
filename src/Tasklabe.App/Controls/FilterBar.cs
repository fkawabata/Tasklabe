using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Tasklabe.App.Services;
using Windows.System;

namespace Tasklabe.App.Controls;

/// <summary>絞り込みの 1 つの条件。</summary>
/// <param name="Label">「担当: @sato」のような、条件の短い説明。</param>
/// <param name="Change">押したときに、条件を変えるピッカーを開く。</param>
/// <param name="Clear">条件を解く。</param>
public sealed record FilterChip(string Label, Func<FrameworkElement, Task> Change, Action Clear);

/// <summary>
/// 表示するものを減らしている条件を、効いているあいだビューの上に並べるチップの列（UX 規約 UX-24）。
/// チップを押すと条件を変え、✕ か Delete で解く。右端に「n 件中 m 件を表示」を示す。
/// </summary>
public sealed partial class FilterBar : UserControl
{
    private readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
    private readonly HyperlinkButton _clearAll = new() { Content = "すべて解除", Padding = new Thickness(4, 2, 4, 2) };
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center };
    private Action? _clearAllAction;

    public FilterBar()
    {
        _count.Style = AppResources.Style("Text.Caption");
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        left.Children.Add(new FontIcon { Glyph = "", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeResources.Brush("TextFillColorSecondaryBrush") });
        left.Children.Add(_chips);
        left.Children.Add(_clearAll);
        grid.Children.Add(new ScrollViewer
        {
            Content = left,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Disabled,
        });
        Grid.SetColumn(_count, 1);
        grid.Children.Add(_count);
        Content = grid;
        Margin = new Thickness(0, 0, 0, 8);
        Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(this, "絞り込みの条件");
        _clearAll.Click += (_, _) => _clearAllAction?.Invoke();
    }

    /// <summary>効いている条件と件数を示す。条件がなければ隠す。</summary>
    public void Update(IReadOnlyList<FilterChip> chips, int shown, int total, Action clearAll)
    {
        _clearAllAction = clearAll;
        Visibility = chips.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _chips.Children.Clear();
        foreach (var chip in chips)
        {
            _chips.Children.Add(CreateChip(chip));
        }

        _clearAll.Visibility = chips.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _count.Text = $"{total} 件中 {shown} 件を表示";
        AutomationProperties.SetName(this, $"絞り込みの条件: {string.Join("、", chips.Select(c => c.Label))}。{_count.Text}");
    }

    private static Button CreateChip(FilterChip chip)
    {
        var close = new FontIcon { Glyph = "", FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new TextBlock { Text = chip.Label, Style = AppResources.Style("Text.Caption"), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(close);

        // ピッカーのボタンと同じ色の段階（面・枠・ポインターを載せた色）にし、押して開くピッカーの面とつながって見せる
        var button = new Button
        {
            Content = content,
            Style = AppResources.Style("MorphSelect.Trigger"),
            Padding = new Thickness(10, 3, 8, 4),
            MinHeight = 0,
        };
        Pill.SetIsEnabled(button, true);
        AutomationProperties.SetName(button, $"{chip.Label}。押すと条件を変えます。Delete で解除");
        ToolTipService.SetToolTip(button, "押すと条件を変えます。✕ か Delete で解除");

        // ✕ の上を押したときは解除、それ以外は条件を変える
        bool onClose = false;
        close.PointerPressed += (_, _) => onClose = true;
        button.Click += async (_, _) =>
        {
            if (onClose)
            {
                onClose = false;
                chip.Clear();
                return;
            }

            await chip.Change(button);
        };
        button.KeyDown += (_, e) =>
        {
            if (e.Key is VirtualKey.Delete or VirtualKey.Back)
            {
                e.Handled = true;
                chip.Clear();
            }
        };
        return button;
    }
}
