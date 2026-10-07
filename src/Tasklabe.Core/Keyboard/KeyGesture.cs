using System.Globalization;

namespace Tasklabe.Core.Keyboard;

[Flags]
public enum KeyModifiers
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
}

/// <summary>
/// キーの組み合わせ（要件 F-KEY-01）。<see cref="Key"/> は Windows の仮想キーの名前
/// （"N"、"F5"、"Number1"、"Enter"、"Slash" など）とする。
/// </summary>
public readonly record struct KeyGesture(string Key, KeyModifiers Modifiers = KeyModifiers.None)
{
    private static readonly Dictionary<string, string> DisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Escape"] = "Esc",
        ["Back"] = "Backspace",
        ["Left"] = "←",
        ["Right"] = "→",
        ["Up"] = "↑",
        ["Down"] = "↓",
        ["Slash"] = "/",
        ["Comma"] = ",",
        ["Period"] = ".",
        ["Minus"] = "-",
        ["PageUp"] = "PgUp",
        ["PageDown"] = "PgDn",
    };

    /// <summary>Ctrl または Alt を含むか。文字を入力中でも働かせてよいキーかの判断に使う。</summary>
    public bool HasCommandModifier => (Modifiers & (KeyModifiers.Ctrl | KeyModifiers.Alt)) != 0;

    /// <summary>"Ctrl+Shift+N" の形で読み取る。修飾キーは Ctrl（Control）、Shift、Alt。</summary>
    public static bool TryParse(string? text, out KeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var modifiers = KeyModifiers.None;
        foreach (var part in parts[..^1])
        {
            modifiers |= part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => KeyModifiers.Ctrl,
                "SHIFT" => KeyModifiers.Shift,
                "ALT" or "MENU" => KeyModifiers.Alt,
                _ => (KeyModifiers)(-1),
            };
            if (modifiers < 0)
            {
                return false;
            }
        }

        // 修飾キーだけ（"Ctrl+" など）は組み合わせとして認めない
        if (parts[^1].ToUpperInvariant() is "CTRL" or "CONTROL" or "SHIFT" or "ALT" or "MENU"
            || NormalizeKey(parts[^1]) is not { } key)
        {
            return false;
        }

        gesture = new KeyGesture(key, modifiers);
        return true;
    }

    public static KeyGesture Parse(string text) =>
        TryParse(text, out var g) ? g : throw new FormatException($"キーの組み合わせとして読めません: {text}");

    /// <summary>保存用の表記（"Ctrl+Shift+N"）。</summary>
    public override string ToString() => string.Join('+', ModifierNames().Append(Key));

    /// <summary>画面に出す表記（"Ctrl + N"、"?"、"Shift + ←"）。</summary>
    public string Display
    {
        get
        {
            // Shift + / は、どの配列でも「?」と書いたほうが伝わる
            if (Key == "Slash" && Modifiers == KeyModifiers.Shift)
            {
                return "?";
            }

            return string.Join(" + ", ModifierNames().Append(KeyDisplay(Key)));
        }
    }

    /// <summary>押している修飾キーの名前（Ctrl、Shift、Alt の順）。</summary>
    private IEnumerable<string> ModifierNames()
    {
        if (Modifiers.HasFlag(KeyModifiers.Ctrl))
        {
            yield return "Ctrl";
        }

        if (Modifiers.HasFlag(KeyModifiers.Shift))
        {
            yield return "Shift";
        }

        if (Modifiers.HasFlag(KeyModifiers.Alt))
        {
            yield return "Alt";
        }
    }

    /// <summary>キーの名前の表示（"Number1" → "1"）。</summary>
    public static string KeyDisplay(string key)
    {
        if (key.StartsWith("Number", StringComparison.Ordinal) && key.Length == 7 && char.IsDigit(key[6]))
        {
            return key[6..];
        }

        return DisplayNames.TryGetValue(key, out var name) ? name : key;
    }

    /// <summary>表記の揺れをそろえる（"n" → "N"、"1" → "Number1"、"Esc" → "Escape"）。</summary>
    private static string? NormalizeKey(string key)
    {
        if (key.Length == 1)
        {
            char c = key[0];
            if (char.IsAsciiLetter(c))
            {
                return char.ToUpperInvariant(c).ToString();
            }

            if (char.IsAsciiDigit(c))
            {
                return "Number" + c;
            }

            return c switch
            {
                '/' => "Slash",
                ',' => "Comma",
                '.' => "Period",
                '-' => "Minus",
                _ => null,
            };
        }

        return key.ToUpperInvariant() switch
        {
            "ESC" => "Escape",
            "RETURN" => "Enter",
            "BACKSPACE" => "Back",
            "DEL" => "Delete",
            "INS" => "Insert",
            _ => char.ToUpper(key[0], CultureInfo.InvariantCulture) + key[1..],
        };
    }
}
