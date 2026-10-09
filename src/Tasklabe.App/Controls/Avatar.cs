using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Tasklabe.Core.Domain;

namespace Tasklabe.App.Controls;

/// <summary>
/// 人を示す丸いアバター（ログイン名の頭文字と色）。カードの担当者と、担当者のピッカーの候補で同じ見た目にする。
/// 同じ人は、どこでも同じ色にする。
/// </summary>
internal static class Avatar
{
    /// <summary>白い文字が読める濃さの色（Fluent の人物の色から選んだもの）。</summary>
    private static readonly Windows.UI.Color[] Palette =
    [
        Windows.UI.Color.FromArgb(255, 0x00, 0x78, 0xD4),
        Windows.UI.Color.FromArgb(255, 0x03, 0x83, 0x87),
        Windows.UI.Color.FromArgb(255, 0x49, 0x82, 0x05),
        Windows.UI.Color.FromArgb(255, 0xCA, 0x50, 0x10),
        Windows.UI.Color.FromArgb(255, 0xA4, 0x26, 0x2C),
        Windows.UI.Color.FromArgb(255, 0x87, 0x64, 0xB8),
        Windows.UI.Color.FromArgb(255, 0xC2, 0x39, 0xB3),
        Windows.UI.Color.FromArgb(255, 0x4F, 0x6B, 0xED),
    ];

    /// <summary>頭文字（ログイン名、または名前だけのメンバーの名前の先頭の 1 文字）。</summary>
    public static string Initial(string login) => People.Name(login) is { Length: > 0 } name ? name[..1].ToUpperInvariant() : "";

    /// <summary>色。</summary>
    public static SolidColorBrush Brush(string login) =>
        new(Palette[(int)((uint)People.Name(login).ToUpperInvariant().Sum(c => c) % Palette.Length)]);

    /// <summary>アバターの要素（カードと同じ 20 px の丸）。</summary>
    public static FrameworkElement Create(string login, double size = 20)
    {
        var circle = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = Brush(login),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = Initial(login),
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        return circle;
    }
}
