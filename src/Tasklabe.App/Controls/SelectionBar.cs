using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Tasklabe.App.Services;

namespace Tasklabe.App.Controls;

/// <summary>
/// 複数を選んでいるあいだ、画面の下端に出す操作バー（UX 規約 UX-07）。件数と、選んでいるすべてに効くキーを示す。
/// キーはキーの割り当てから引き、設定で変えると表示も変わる（UX-17）。
/// </summary>
public sealed partial class SelectionBar : UserControl
{
    private static readonly (string Id, string Label)[] Actions =
    [
        ("task.status", "ステータス"),
        ("task.assign", "担当者"),
        ("task.due", "期日"),
        ("task.estimate", "工数"),
        ("task.plan", "計画"),
        ("task.delete", "削除"),
    ];

    private readonly TextBlock _count = new() { Style = AppResources.Style("Text.BodyStrong"), VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _keys = new() { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _escape = Hint("Esc", "で解除");

    public SelectionBar()
    {
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(0, 0, 0, 16);
        Visibility = Visibility.Collapsed;
        var border = new Border
        {
            Padding = new Thickness(16, 8, 16, 8),
            CornerRadius = AppResources.CornerRadius("Radius.Overlay"),
            Background = ThemeResources.Brush("AcrylicInAppFillColorDefaultBrush"),
            BorderBrush = ThemeResources.Brush("SurfaceStrokeColorFlyoutBrush"),
            BorderThickness = new Thickness(1),
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20 };
        panel.Children.Add(_count);
        panel.Children.Add(_keys);
        panel.Children.Add(_escape);
        border.Child = panel;
        Content = border;
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
    }

    /// <summary>選んでいる件数を示す。2 件以上のときだけ出す。</summary>
    public void Update(int count)
    {
        Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (count <= 1)
        {
            return;
        }

        _count.Text = $"{count} 件を選択中";

        // ヒントを出さない設定では、件数だけを示す
        _keys.Visibility = _escape.Visibility = Hints.Shown ? Visibility.Visible : Visibility.Collapsed;
        _keys.Children.Clear();
        var keymap = App.Current.Services.Keymap;
        foreach (var (id, label) in Actions)
        {
            var keys = keymap.Display(id);
            if (keys.Length > 0)
            {
                _keys.Children.Add(Hint(keys, label));
            }
        }

        AutomationProperties.SetName(this, $"{count} 件を選択中");
    }

    private static StackPanel Hint(string keys, string label) => new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 4,
        VerticalAlignment = VerticalAlignment.Center,
        Children =
        {
            Views.Dialogs.ShortcutHelpDialog.KeyCaps(keys),
            new TextBlock { Text = label, Style = AppResources.Style("Text.Caption"), VerticalAlignment = VerticalAlignment.Center },
        },
    };
}

/// <summary>キーのある操作の説明（「担当者を変える (A)」）を、いまのキーの割り当てから作る（UX 規約 UX-17）。</summary>
public static class KeyHints
{
    /// <summary>ツールチップの文言。キーを割り当てていなければ操作名だけ。</summary>
    public static string Tip(string actionId, string label)
    {
        var keys = App.Current.Services.Keymap.Display(actionId);
        return keys.Length > 0 ? $"{label} ({keys})" : label;
    }

    /// <summary>読み上げに添えるキー。</summary>
    public static string Keys(string actionId) => App.Current.Services.Keymap.Display(actionId);
}
