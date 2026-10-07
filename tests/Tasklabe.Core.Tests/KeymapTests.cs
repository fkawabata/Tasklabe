using Tasklabe.Core.Keyboard;

namespace Tasklabe.Core.Tests;

public class KeymapTests
{
    [Theory]
    [InlineData("N", "N", KeyModifiers.None)]
    [InlineData("n", "N", KeyModifiers.None)]
    [InlineData("Ctrl+Shift+C", "C", KeyModifiers.Ctrl | KeyModifiers.Shift)]
    [InlineData("control + enter", "Enter", KeyModifiers.Ctrl)]
    [InlineData("Alt+1", "Number1", KeyModifiers.Alt)]
    [InlineData("Shift+/", "Slash", KeyModifiers.Shift)]
    [InlineData("Esc", "Escape", KeyModifiers.None)]
    public void Gestures_are_parsed_into_a_canonical_form(string text, string key, KeyModifiers modifiers)
    {
        Assert.True(KeyGesture.TryParse(text, out var g));
        Assert.Equal(new KeyGesture(key, modifiers), g);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Hyper+N")]
    [InlineData("Ctrl+@")]
    public void Invalid_gestures_are_rejected(string text) => Assert.False(KeyGesture.TryParse(text, out _));

    [Theory]
    [InlineData("Ctrl+Shift+N", "Ctrl + Shift + N")]
    [InlineData("Shift+Slash", "?")]
    [InlineData("Number3", "3")]
    [InlineData("Shift+Left", "Shift + ←")]
    [InlineData("Ctrl+Comma", "Ctrl + ,")]
    public void Gestures_have_readable_labels(string text, string display) =>
        Assert.Equal(display, KeyGesture.Parse(text).Display);

    [Fact]
    public void Canonical_text_round_trips()
    {
        var g = KeyGesture.Parse("Alt+Shift+Ctrl+X");
        Assert.Equal("Ctrl+Shift+Alt+X", g.ToString());
        Assert.Equal(g, KeyGesture.Parse(g.ToString()));
    }

    [Fact]
    public void Default_keymap_has_no_conflicts_or_reserved_keys()
    {
        var keymap = new Keymap();

        Assert.Empty(keymap.Conflicts());
        foreach (var d in ShortcutCatalog.All)
        {
            Assert.All(d.Defaults, g => Assert.False(ShortcutCatalog.IsReserved(g, d.Scope), $"{d.Id} {g}"));
        }
    }

    [Fact]
    public void Views_that_are_never_shown_together_can_share_keys()
    {
        // 計画の表・ガント・カンバンは同時に表示しないため、Shift + ← / → をそれぞれ別の意味に使える（UX 規約 UX-13）
        Assert.False(ShortcutCatalog.CanOverlap(ShortcutScope.Kanban, ShortcutScope.Gantt));
        Assert.False(ShortcutCatalog.CanOverlap(ShortcutScope.Kanban, ShortcutScope.Wbs));
        Assert.True(ShortcutCatalog.CanOverlap(ShortcutScope.Kanban, ShortcutScope.Task));
        Assert.True(ShortcutCatalog.CanOverlap(ShortcutScope.Kanban, ShortcutScope.Global));
        Assert.Equal(new Keymap().For("kanban.moveNext"), new Keymap().For("gantt.moveLater"));
    }

    [Theory]
    [InlineData("Ctrl+Z")]
    [InlineData("Ctrl+Y")]
    [InlineData("Ctrl+Shift+Z")]
    public void Undo_and_redo_keys_cannot_be_reassigned(string key)
    {
        Assert.Equal(KeymapCheck.Reserved, new Keymap().Check("task.detail", KeyGesture.Parse(key)).Result);
    }

    [Fact]
    public void Actions_that_stay_on_the_screen_use_single_keys()
    {
        // 選んでいるタスクへの操作は、画面を移らないので修飾キーなしの 1 文字を既定にする（要件 F-KEY-02）
        string[] singleKey = ["task.status", "task.progress", "task.assign", "task.assignMe", "task.due", "task.estimate", "task.plan", "task.detail"];
        foreach (var id in singleKey)
        {
            var g = new Keymap().For(id)[0];
            Assert.Equal(KeyModifiers.None, g.Modifiers);
            Assert.Single(g.Key);
        }
    }

    [Fact]
    public void Overrides_replace_defaults_and_are_saved_as_differences()
    {
        var keymap = new Keymap(new Dictionary<string, string> { ["task.status"] = "Ctrl+Shift+S", ["app.sync"] = "" });

        Assert.Equal([KeyGesture.Parse("Ctrl+Shift+S")], keymap.For("task.status"));
        Assert.Empty(keymap.For("app.sync"));
        Assert.Equal("task.status", keymap.Find(KeyGesture.Parse("Ctrl+Shift+S"), ShortcutScope.Task)?.Id);
        Assert.Equal(
            new Dictionary<string, string> { ["task.status"] = "Ctrl+Shift+S", ["app.sync"] = "" },
            keymap.ToOverrides());
        Assert.Empty(keymap.Reset("task.status").Reset("app.sync").ToOverrides());
    }

    [Fact]
    public void Conflicts_are_found_only_between_scopes_that_can_be_active_together()
    {
        var keymap = new Keymap();

        // 選んでいるタスクの S と重なる
        var (result, other) = keymap.Check("task.due", KeyGesture.Parse("S"));
        Assert.Equal(KeymapCheck.Conflict, result);
        Assert.Equal("task.status", other?.Id);

        // 追加ダイアログは他の画面と同時には働かない
        Assert.Equal(KeymapCheck.Ok, keymap.Check("composer.status", KeyGesture.Parse("S")).Result);

        // 計画の表とガントは同時に表示しない（Ctrl+Enter を両方で使える）
        Assert.Equal(KeymapCheck.Ok, keymap.Check("preview.confirm", KeyGesture.Parse("Ctrl+Enter")).Result);
    }

    [Fact]
    public void Basic_navigation_keys_are_reserved()
    {
        var keymap = new Keymap();

        Assert.Equal(KeymapCheck.Reserved, keymap.Check("task.status", KeyGesture.Parse("Escape")).Result);
        Assert.Equal(KeymapCheck.Reserved, keymap.Check("task.status", KeyGesture.Parse("Down")).Result);
        Assert.Equal(KeymapCheck.Reserved, keymap.Check("task.status", KeyGesture.Parse("Alt+3")).Result);
        Assert.Equal(KeymapCheck.Reserved, keymap.Check("task.status", KeyGesture.Parse("Tab")).Result);
        Assert.Equal(KeymapCheck.Ok, keymap.Check("wbs.indent", KeyGesture.Parse("Tab")).Result);
        Assert.Equal(KeymapCheck.Reserved, keymap.Check("composer.status", KeyGesture.Parse("2")).Result);
    }

    [Fact]
    public void Every_action_has_a_unique_id_and_at_least_one_default()
    {
        Assert.Equal(ShortcutCatalog.All.Count, ShortcutCatalog.All.Select(d => d.Id).Distinct().Count());
        Assert.All(ShortcutCatalog.All, d => Assert.NotEmpty(d.Defaults));
    }
}
