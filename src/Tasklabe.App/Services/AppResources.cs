using Microsoft.UI.Xaml;

namespace Tasklabe.App.Services;

/// <summary>
/// コードで作る要素に使う、アプリのリソース（スタイル、角の丸み、寸法）。テーマで変わる色は <see cref="ThemeResources"/> から引く。
/// </summary>
public static class AppResources
{
    public static Style Style(string key) => Get<Style>(key);

    public static CornerRadius CornerRadius(string key) => Get<CornerRadius>(key);

    public static T Get<T>(string key) => (T)Application.Current.Resources[key];
}
