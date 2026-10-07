using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Tasklabe.App.Services;

namespace Tasklabe.App.Controls;

/// <summary>コードで組み立てる画面（設定など）の、Fluent の設定カード（CardBackgroundFillColorDefaultBrush の面と枠）。</summary>
internal static class Cards
{
    /// <param name="padding">内側の余白（既定は 16, 12）。中身が自分で余白を持つときは 0 を渡す。</param>
    public static Border Create(UIElement child, Thickness? padding = null) => new()
    {
        Padding = padding ?? new Thickness(16, 12, 16, 12),
        Background = ThemeResources.Brush("CardBackgroundFillColorDefaultBrush"),
        BorderBrush = ThemeResources.Brush("CardStrokeColorDefaultBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = AppResources.CornerRadius("Radius.Card"),
        Child = child,
    };
}
