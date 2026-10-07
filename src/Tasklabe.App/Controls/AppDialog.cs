using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Tasklabe.App.Services;

namespace Tasklabe.App.Controls;

/// <summary>
/// ダイアログの共通の形（UX 規約 UX-08、UX-11、UX-17、UX-28）。
/// 主ボタンは操作の動詞で書き、下端に中で使えるキーを 1 行で示す。取り消せない操作は安全な側（キャンセル）を既定にする。
/// </summary>
public static class AppDialog
{
    /// <summary>少ない項目で何かを作る・移す・変えるダイアログ。</summary>
    /// <param name="primary">主ボタンの文言（動詞。「OK」は使わない）。</param>
    /// <param name="multiline">複数行の入力欄を持つ（主ボタンは Ctrl + Enter で押す）。</param>
    public static ContentDialog Create(XamlRoot root, string title, UIElement content, string primary, bool multiline = false,
        string? extraHint = null)
    {
        var enter = multiline ? "Ctrl + Enter" : "Enter";
        return new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = WithHint(content, $"{enter} で{primary}　Esc でキャンセル" + (extraHint is null ? "" : "　" + extraHint)),
            PrimaryButtonText = primary,
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
        };
    }

    /// <summary>取り消せない操作の確認。既定のボタン（Enter）はキャンセルとする（UX-11）。</summary>
    /// <param name="verb">主ボタンの動詞（「削除」「破棄」「アーカイブ」）。</param>
    public static ContentDialog Confirm(XamlRoot root, string title, string message, string verb) => new()
    {
        XamlRoot = root,
        Title = title,
        Content = WithHint(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            $"Enter でキャンセル　Tab で「{verb}」へ移って Enter で{verb}"),
        PrimaryButtonText = verb,
        CloseButtonText = "キャンセル",
        DefaultButton = ContentDialogButton.Close,
    };

    /// <summary>見るだけのダイアログ（キーの一覧など）。</summary>
    public static ContentDialog Info(XamlRoot root, string title, UIElement content) => new()
    {
        XamlRoot = root,
        Title = title,
        Content = WithHint(content, "Enter か Esc で閉じる"),
        CloseButtonText = "閉じる",
        DefaultButton = ContentDialogButton.Close,
    };

    /// <summary>内容の下に、使えるキーを示す 1 行を添える。ヒントを出さない設定なら、内容だけにする。</summary>
    public static UIElement WithHint(UIElement content, string hint)
    {
        if (!Services.Hints.Shown)
        {
            return content;
        }

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(content);
        panel.Children.Add(HintText(hint));
        return panel;
    }

    public static TextBlock HintText(string hint) => new()
    {
        Text = hint,
        Style = AppResources.Style("Text.Caption"),
        Foreground = Services.ThemeResources.Brush("TextFillColorTertiaryBrush"),
        TextWrapping = TextWrapping.Wrap,
        Visibility = Services.Hints.Shown ? Visibility.Visible : Visibility.Collapsed,
    };
}
