using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Tasklabe.App.Services;

/// <summary>
/// コードで使う画面の文言（技術設計書 7.1 節）。文言は Strings/ja-JP と Strings/en-US の Resources.resw に置き、
/// キーで引く。XAML の文言は x:Uid で引く。
/// </summary>
public static class Texts
{
    private static readonly ResourceLoader Loader = new();

    public static string Get(string key) => Loader.GetString(key);

    /// <summary>値を差し込む文言。値の場所は文言の側に {0}、{1} で書く（UX 規約 UX-35）。</summary>
    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Loader.GetString(key), args);

    /// <summary>英語で単数と複数の形が分かれる文言。キーに _One と _Other を付けた 2 つを持つ。</summary>
    public static string Count(string key, int count, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Loader.GetString(count == 1 ? key + "_One" : key + "_Other"), [count, .. args]);
}
