using Microsoft.UI.Xaml;
using Tasklabe.App.Services;

namespace Tasklabe.App.Controls;

/// <summary>
/// 状態によって切り替える文字のスタイル。色はスタイルの ThemeResource で決め、アプリ内のテーマの切り替えにも追従させる。
/// </summary>
public static class TextStyles
{
    /// <summary>タスク名。完了・中止したものは副テキストの色で目立たなくする（透明度では 4.5:1 のコントラストを保てないため。要件 NF-10）。</summary>
    public static Style Title(bool muted) => AppResources.Style(muted ? "Text.Body.Muted" : "Text.Body");

    /// <summary>計画の表の値。中止したタスクの値は集計に入らないため、タイトルと同じく副テキストの色で目立たなくする。</summary>
    public static Style Value(bool muted) => Title(muted);
}
