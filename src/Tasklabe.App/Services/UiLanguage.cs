using System.Globalization;
using Microsoft.Windows.Globalization;

namespace Tasklabe.App.Services;

/// <summary>
/// 表示の言語（要件 F-SET-08、技術設計書 7.2 節）。起動の最初に一度だけ決め、アプリを開いているあいだは変えない。
/// </summary>
public static class UiLanguage
{
    /// <summary>設定の値。Windows の表示言語に合わせる。</summary>
    public const string System = "system";

    public const string Japanese = "ja";

    public const string English = "en";

    /// <summary>決めた言語のタグ（ja-JP または en-US）。</summary>
    public static string Tag { get; private set; } = "ja-JP";

    public static bool IsJapanese => Tag == "ja-JP";

    /// <summary>
    /// 設定から言語を決め、文言の表（MRT Core）と .NET の書式の両方に効かせる。最初のウィンドウを作る前に呼ぶ。
    /// </summary>
    public static void Apply(string? setting)
    {
        var japanese = setting switch
        {
            Japanese => true,
            English => false,
            // 上書きする前の CurrentUICulture は、Windows の表示言語
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja",
        };
        Tag = japanese ? "ja-JP" : "en-US";
        ApplicationLanguages.PrimaryLanguageOverride = Tag;

        var culture = CultureInfo.GetCultureInfo(Tag);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
