namespace Tasklabe.Core.Keyboard;

/// <summary>割り当てを変えたときの確認の結果。</summary>
public enum KeymapCheck
{
    Ok,

    /// <summary>画面の基本操作に使うキー。</summary>
    Reserved,

    /// <summary>同時に働く別の操作に割り当て済み。</summary>
    Conflict,
}

/// <summary>
/// キーの割り当て（要件 F-KEY-01〜04）。既定の割り当てに、利用者が変えた分を重ねる。
/// 利用者が変えた分は、操作の Id ごとに "N, Ctrl+N" の形（空文字は割り当てなし）で保存する。
/// </summary>
public sealed class Keymap
{
    private readonly Dictionary<string, IReadOnlyList<KeyGesture>> _gestures;

    public Keymap(IReadOnlyDictionary<string, string>? overrides = null)
    {
        _gestures = ShortcutCatalog.All.ToDictionary(d => d.Id, d => d.Defaults, StringComparer.Ordinal);
        foreach (var (id, text) in overrides ?? new Dictionary<string, string>())
        {
            if (_gestures.ContainsKey(id))
            {
                _gestures[id] = ParseList(text);
            }
        }
    }

    private Keymap(Dictionary<string, IReadOnlyList<KeyGesture>> gestures) => _gestures = gestures;

    public IReadOnlyList<KeyGesture> For(string id) => _gestures.GetValueOrDefault(id) ?? [];

    /// <summary>その範囲で、キーに割り当てた操作。</summary>
    public ShortcutDefinition? Find(KeyGesture gesture, ShortcutScope scope) =>
        ShortcutCatalog.All.FirstOrDefault(d => d.Scope == scope && For(d.Id).Contains(gesture));

    /// <summary>画面に出す表記（"N / Ctrl + N"）。割り当てがなければ空文字。</summary>
    public string Display(string id) => string.Join(" / ", For(id).Select(g => g.Display));

    /// <summary>操作 id にキーを割り当ててよいか。重なる場合は、その相手を返す。</summary>
    public (KeymapCheck Result, ShortcutDefinition? ConflictWith) Check(string id, KeyGesture gesture)
    {
        var definition = ShortcutCatalog.Find(id) ?? throw new ArgumentException($"不明な操作です: {id}", nameof(id));
        if (ShortcutCatalog.IsReserved(gesture, definition.Scope))
        {
            return (KeymapCheck.Reserved, null);
        }

        var other = ShortcutCatalog.All.FirstOrDefault(d => d.Id != id
            && ShortcutCatalog.CanOverlap(d.Scope, definition.Scope)
            && For(d.Id).Contains(gesture));
        return other is null ? (KeymapCheck.Ok, null) : (KeymapCheck.Conflict, other);
    }

    public Keymap With(string id, IReadOnlyList<KeyGesture> gestures)
    {
        var copy = new Dictionary<string, IReadOnlyList<KeyGesture>>(_gestures, StringComparer.Ordinal) { [id] = gestures.Distinct().ToList() };
        return new Keymap(copy);
    }

    public Keymap Reset(string id) =>
        ShortcutCatalog.Find(id) is { } d ? With(id, d.Defaults) : this;

    /// <summary>同時に働く範囲で、同じキーを割り当てた操作の組。</summary>
    public IReadOnlyList<(ShortcutDefinition A, ShortcutDefinition B, KeyGesture Gesture)> Conflicts()
    {
        var result = new List<(ShortcutDefinition, ShortcutDefinition, KeyGesture)>();
        var all = ShortcutCatalog.All;
        for (int i = 0; i < all.Count; i++)
        {
            for (int j = i + 1; j < all.Count; j++)
            {
                if (!ShortcutCatalog.CanOverlap(all[i].Scope, all[j].Scope))
                {
                    continue;
                }

                foreach (var g in For(all[i].Id).Intersect(For(all[j].Id)))
                {
                    result.Add((all[i], all[j], g));
                }
            }
        }

        return result;
    }

    /// <summary>既定から変えた分だけを保存用に取り出す。</summary>
    public Dictionary<string, string> ToOverrides()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var d in ShortcutCatalog.All)
        {
            var current = For(d.Id);
            if (!current.SequenceEqual(d.Defaults))
            {
                result[d.Id] = string.Join(", ", current);
            }
        }

        return result;
    }

    private static List<KeyGesture> ParseList(string text) =>
        text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(t => KeyGesture.TryParse(t, out var g) ? g : (KeyGesture?)null)
            .OfType<KeyGesture>()
            .ToList();
}
