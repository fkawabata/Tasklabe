using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Tasklabe.Core.Keyboard;
using Windows.System;
using Windows.UI.Core;

namespace Tasklabe.App.Services;

/// <summary>押されたキーを、キー割り当てで使う組み合わせ（<see cref="KeyGesture"/>）にする。</summary>
public static class KeyInput
{
    /// <summary>名前のない記号のキー（OEM キー）。</summary>
    private static readonly Dictionary<VirtualKey, string> OemNames = new()
    {
        [(VirtualKey)191] = "Slash",
        [(VirtualKey)188] = "Comma",
        [(VirtualKey)190] = "Period",
        [(VirtualKey)189] = "Minus",
    };

    /// <summary>いまの修飾キーの状態と合わせて組み合わせにする。修飾キーだけのときは null。</summary>
    public static KeyGesture? From(VirtualKey key)
    {
        var name = NameOf(key);
        if (name is null || ShortcutCatalog.IsModifierKey(name))
        {
            return null;
        }

        var modifiers = KeyModifiers.None;
        if (IsDown(VirtualKey.Control))
        {
            modifiers |= KeyModifiers.Ctrl;
        }

        if (IsDown(VirtualKey.Shift))
        {
            modifiers |= KeyModifiers.Shift;
        }

        if (IsDown(VirtualKey.Menu))
        {
            modifiers |= KeyModifiers.Alt;
        }

        return new KeyGesture(name, modifiers);
    }

    /// <summary>数字キー（最上段とテンキー）なら 1〜9、0 は 10 として返す。</summary>
    public static int? Digit(VirtualKey key) => key switch
    {
        >= VirtualKey.Number1 and <= VirtualKey.Number9 => key - VirtualKey.Number0,
        >= VirtualKey.NumberPad1 and <= VirtualKey.NumberPad9 => key - VirtualKey.NumberPad0,
        VirtualKey.Number0 or VirtualKey.NumberPad0 => 10,
        _ => null,
    };

    /// <summary>文字を入力しているところ（矢印キーや 1 文字のキーを入力欄に任せるところ）か。</summary>
    public static bool IsTextInput(object? focused) =>
        focused is TextBox or RichEditBox or AutoSuggestBox or PasswordBox
        || focused is FrameworkElement { Parent: NumberBox }
        || (focused as ComboBox)?.IsEditable == true;

    public static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private static string? NameOf(VirtualKey key)
    {
        if (OemNames.TryGetValue(key, out var oem))
        {
            return oem;
        }

        var name = key.ToString();
        return int.TryParse(name, out _) ? null : name;
    }
}
