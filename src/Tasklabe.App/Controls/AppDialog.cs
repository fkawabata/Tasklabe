using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Tasklabe.App.Services;

namespace Tasklabe.App.Controls;

/// <summary>
/// ダイアログの共通の形（UX 規約 UX-08、UX-11、UX-17、UX-28）。
/// 主ボタンは操作の名前で書き、下端に中で使えるキーを 1 行で示す。取り消せない操作は安全な側（キャンセル）を既定にする。
/// </summary>
public static class AppDialog
{
    /// <summary>少ない項目で何かを作る・移す・変えるダイアログ。</summary>
    /// <param name="primary">主ボタンの文言（操作の名前。「OK」は使わない）。</param>
    /// <param name="multiline">複数行の入力欄を持つ（主ボタンは Ctrl + Enter で押す）。</param>
    public static ContentDialog Create(XamlRoot root, string title, UIElement content, string primary, bool multiline = false,
        string? extraHint = null)
    {
        var enter = multiline ? "Ctrl + Enter" : "Enter";
        return new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = WithHint(content, Texts.Format("Dialog_Hint", enter, primary) + (extraHint is null ? "" : Texts.Get("Common_HintSeparator") + extraHint)),
            PrimaryButtonText = primary,
            CloseButtonText = Texts.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
    }

    /// <summary>取り消せない操作の確認。既定のボタン（Enter）はキャンセルとする（UX-11）。</summary>
    /// <param name="verb">主ボタンの文言（「削除」「破棄」「アーカイブ」）。</param>
    public static ContentDialog Confirm(XamlRoot root, string title, string message, string verb) => new()
    {
        XamlRoot = root,
        Title = title,
        Content = WithHint(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            Texts.Format("Dialog_ConfirmHint", verb)),
        PrimaryButtonText = verb,
        CloseButtonText = Texts.Get("Common_Cancel"),
        DefaultButton = ContentDialogButton.Close,
    };

    /// <summary>見るだけのダイアログ（キーの一覧など）。</summary>
    public static ContentDialog Info(XamlRoot root, string title, UIElement content) => new()
    {
        XamlRoot = root,
        Title = title,
        Content = WithHint(content, Texts.Get("Dialog_InfoHint")),
        CloseButtonText = Texts.Get("Common_Close"),
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
