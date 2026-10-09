using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Tasklabe.App.Services;
using Tasklabe.Core.Keyboard;

namespace Tasklabe.App.Controls;

/// <summary>
/// 確定のボタンの中身。ラベルのすぐ後ろに、割り当てたキーを小さなキーの形で添える（Enter は記号、修飾キーは名前）。
/// Enter では確定しないボタン（タスクの追加）で、確定のキーを説明の文にせずに示す。アクセント色のボタンの上に置く。
/// </summary>
internal static class KeyCaps
{
    /// <summary>Enter の記号（Segoe Fluent Icons の ReturnKey）。</summary>
    private const string EnterGlyph = "\uE751";

    /// <summary>ラベルとキーを並べた中身を作る。</summary>
    /// <param name="actionId">キーの割り当ての操作 id（"composer.submit" など）。</param>
    public static FrameworkElement Content(string label, string actionId) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        HorizontalAlignment = HorizontalAlignment.Center,
        Children =
        {
            new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center },
            For(actionId),
        },
    };

    /// <summary>割り当てたキー（最初のもの）を、キーの形の並びにする。割り当てがなければ空。</summary>
    public static StackPanel For(string actionId)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        if (App.Current.Services.Keymap.For(actionId) is not [var gesture, ..])
        {
            return panel;
        }

        // 読み上げは「Ctrl + Enter」とまとめて伝える
        AutomationProperties.SetName(panel, gesture.Display);
        foreach (var (flag, name) in new[] { (KeyModifiers.Ctrl, "Ctrl"), (KeyModifiers.Shift, "Shift"), (KeyModifiers.Alt, "Alt") })
        {
            if (gesture.Modifiers.HasFlag(flag))
            {
                panel.Children.Add(Cap(new TextBlock { Text = name, FontSize = 11 }));
            }
        }

        panel.Children.Add(Cap(gesture.Key == "Enter"
            ? new FontIcon { Glyph = EnterGlyph, FontSize = 11 }
            : new TextBlock { Text = KeyGesture.KeyDisplay(gesture.Key), FontSize = 11 }));
        return panel;
    }

    /// <summary>キーの形。ボタンの文字色を薄く敷いた角丸の小さな面に、文字か記号を載せる。</summary>
    private static Border Cap(FrameworkElement face)
    {
        face.VerticalAlignment = VerticalAlignment.Center;
        face.HorizontalAlignment = HorizontalAlignment.Center;
        var ink = ((SolidColorBrush)ThemeResources.Brush("TextOnAccentFillColorPrimaryBrush")).Color;
        return new Border
        {
            MinWidth = 20,
            Height = 18,
            Padding = new Thickness(5, 0, 5, 0),
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, ink.R, ink.G, ink.B)),
            Child = face,
        };
    }
}
