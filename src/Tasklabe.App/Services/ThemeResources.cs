using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace Tasklabe.App.Services;

/// <summary>
/// コードで作る要素に使う、テーマのリソース（色）。
/// Application.Current.Resources[key] は Windows 側のテーマの値を返すため、アプリ内で選んだテーマ（設定の「外観」）と
/// 異なると色がずれる。ここではメインウィンドウの実際のテーマ（ハイコントラストを含む）の辞書から引く。
/// XAML では {ThemeResource} を使い、このクラスは使わない。
/// </summary>
public static class ThemeResources
{
    private static readonly AccessibilitySettings Accessibility = new();

    public static Brush Brush(string key) => (Brush)Get(key);

    public static object Get(string key)
    {
        var resources = Application.Current.Resources;
        foreach (var theme in ThemeKeys())
        {
            if (Find(resources, theme, key) is { } value)
            {
                return value;
            }
        }

        return resources[key];
    }

    /// <summary>引く辞書の名前。WinUI の標準の辞書はダークを "Default" の名前で持つ。</summary>
    private static string[] ThemeKeys()
    {
        if (Accessibility.HighContrast)
        {
            return ["HighContrast"];
        }

        var root = App.Current.MainWindow?.Content as FrameworkElement;
        bool dark = root is not null
            ? root.ActualTheme == ElementTheme.Dark
            : Application.Current.RequestedTheme == ApplicationTheme.Dark;
        return dark ? ["Dark", "Default"] : ["Light"];
    }

    /// <summary>テーマの辞書を、後から読み込んだ辞書を優先して探す。</summary>
    private static object? Find(ResourceDictionary dictionary, string theme, string key)
    {
        if (dictionary.ThemeDictionaries.TryGetValue(theme, out var themed)
            && themed is ResourceDictionary themedDictionary && themedDictionary.TryGetValue(key, out var value))
        {
            return value;
        }

        for (int i = dictionary.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            if (Find(dictionary.MergedDictionaries[i], theme, key) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
