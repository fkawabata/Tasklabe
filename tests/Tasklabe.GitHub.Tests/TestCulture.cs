using System.Globalization;
using System.Runtime.CompilerServices;

namespace Tasklabe.GitHub.Tests;

/// <summary>文言を確かめるテストは日本語の文言で書くため、Windows の表示言語によらず日本語で動かす。</summary>
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void UseJapanese()
    {
        var culture = CultureInfo.GetCultureInfo("ja-JP");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }
}
