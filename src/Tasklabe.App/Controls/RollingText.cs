using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Tasklabe.Animation;

namespace Tasklabe.App.Controls;

/// <summary>
/// 空白で区切った語ごとに、転がって入れ替わる文字（UI デザイン設計書 2.7 節）。
/// 変わった語だけが動き、後の日付になった語は下から上がり、前の日付になった語は上から下りる。変わらない語は止まったまま。
/// </summary>
public sealed partial class RollingText : UserControl
{
    private static readonly TimeSpan EnterDuration = TimeSpan.FromMilliseconds(260);
    private static readonly TimeSpan ExitDuration = TimeSpan.FromMilliseconds(140);

    /// <summary>語が入れ替わるときの上下のずれ（文字の高さの半分弱）。</summary>
    private const float Rise = 7;

    private readonly StackPanel _words = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly List<(Grid Slot, TextBlock Word)> _slots = [];

    public RollingText()
    {
        Content = _words;
        IsTabStop = false;
    }

    public string Text { get; private set; } = "";

    /// <summary>語の文字に当てるスタイル。</summary>
    public Style? WordStyle { get; set; }

    /// <summary>文字の色。null なら親から引き継ぐ。</summary>
    public Brush? WordForeground
    {
        get;
        set
        {
            field = value;
            foreach (var (_, word) in _slots)
            {
                Apply(word);
            }
        }
    }

    /// <summary>文字を変える。</summary>
    /// <param name="direction">1 なら後の値へ（下から上がる）、-1 なら前の値へ（上から下りる）。</param>
    public void SetText(string text, int direction = 1, bool animate = true)
    {
        if (text == Text)
        {
            return;
        }

        Text = text;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        animate = animate && Motion.IsEnabled && IsLoaded;

        // 同じ位置の語を入れ替える
        for (int i = 0; i < Math.Min(words.Length, _slots.Count); i++)
        {
            var (slot, old) = _slots[i];
            if (old.Text == words[i])
            {
                continue;
            }

            var next = NewWord(words[i]);
            slot.Children.Add(next);
            _slots[i] = (slot, next);
            if (animate)
            {
                Leave(old, direction, () => slot.Children.Remove(old));
                Enter(next, direction);
            }
            else
            {
                slot.Children.Remove(old);
            }
        }

        // 増えた語は枠ごと足す
        for (int i = _slots.Count; i < words.Length; i++)
        {
            var word = NewWord(words[i]);
            var slot = new Grid { Children = { word } };
            _words.Children.Add(slot);
            _slots.Add((slot, word));
            if (animate)
            {
                Enter(word, direction);
            }
        }

        // 減った語は枠ごと外す
        while (_slots.Count > words.Length)
        {
            var (slot, word) = _slots[^1];
            _slots.RemoveAt(_slots.Count - 1);
            if (animate)
            {
                Leave(word, direction, () => _words.Children.Remove(slot));
            }
            else
            {
                _words.Children.Remove(slot);
            }
        }
    }

    private TextBlock NewWord(string text)
    {
        var word = new TextBlock { Text = text };
        Apply(word);
        return word;
    }

    private void Apply(TextBlock word)
    {
        if (WordStyle is not null)
        {
            word.Style = WordStyle;
        }

        if (WordForeground is not null)
        {
            word.Foreground = WordForeground;
        }
        else
        {
            word.ClearValue(TextBlock.ForegroundProperty);
        }
    }

    private static void Enter(TextBlock word, int direction) =>
        Motion.Reveal(word, new Vector3(0, direction * Rise, 0), EnterDuration);

    private static void Leave(TextBlock word, int direction, Action removed) =>
        Motion.Conceal(word, new Vector3(0, -direction * Rise, 0), ExitDuration, removed);
}
