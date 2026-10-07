using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace Tasklabe.App.Services;

/// <summary>
/// 最後に使った入力がポインターかキーボードか。ピッカーをキーボードで開いたときだけ入力欄を置くのに使う
/// （UI デザイン設計書 4.1.2 節）。ウィンドウ・ダイアログ・ポップアップの根元で <see cref="Track"/> する。
/// </summary>
public static class LastInput
{
    /// <summary>最後の入力がポインター（マウス・タッチ・ペン）だった。</summary>
    public static bool WasPointer { get; private set; }

    /// <summary>
    /// 要素の中の押下とキー入力を記録する。ほかの要素が処理済みにした入力も受け取る。
    /// キーは PreviewKeyDown で記録する（キーの操作は PreviewKeyDown でピッカーを開くため、KeyDown では間に合わない）。
    /// </summary>
    public static void Track(UIElement root)
    {
        ArgumentNullException.ThrowIfNull(root);
        root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => WasPointer = true), true);
        root.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, _) => WasPointer = false), true);
    }
}
