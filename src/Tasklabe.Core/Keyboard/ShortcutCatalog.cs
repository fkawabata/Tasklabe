using Tasklabe.Core.Resources;

namespace Tasklabe.Core.Keyboard;

/// <summary>
/// ショートカットが働く範囲（要件 F-KEY-02）。どこにフォーカスがあるかで決まる。
/// </summary>
public enum ShortcutScope
{
    /// <summary>メインウィンドウのどこでも。</summary>
    Global,

    /// <summary>一覧・表・カンバン・ガントで選んでいるタスク。</summary>
    Task,

    /// <summary>計画の表（WBS）。</summary>
    Wbs,

    /// <summary>ガントチャート。</summary>
    Gantt,

    /// <summary>ガントチャートの日程の調整のプレビュー。</summary>
    Preview,

    /// <summary>カンバン。</summary>
    Kanban,

    /// <summary>タスクの追加ダイアログ。</summary>
    Composer,
}

/// <summary>割り当てを変えられるショートカット。</summary>
/// <param name="Id">設定に保存するときの名前。</param>
/// <param name="Category">一覧と設定画面での見出しの ID（<see cref="ShortcutCatalog.CategoryName"/>）。</param>
public sealed record ShortcutDefinition(string Id, string Category, ShortcutScope Scope, IReadOnlyList<KeyGesture> Defaults)
{
    /// <summary>操作の名前（UX 規約 UX-28）。ボタン・メニュー・ツールチップ・読み上げ・コマンドパレットで同じものを使う。</summary>
    public string Label => Strings.Action(Id);
}

/// <summary>割り当てを変えられないキー操作（一覧に載せて知らせる）。</summary>
/// <param name="Id">文言を引くための名前。</param>
public sealed record FixedShortcut(string Id, string Category)
{
    /// <summary>キーの表記。区切りの記号が言語で変わるため、文言の表から引く。</summary>
    public string Keys => Strings.FixedKeys(Id);

    public string Label => Strings.FixedLabel(Id);
}

/// <summary>ショートカットの一覧（UI デザイン設計書 4.1 節）。</summary>
public static class ShortcutCatalog
{
    public const string Global = "global";
    public const string Task = "task";
    public const string Wbs = "wbs";
    public const string Gantt = "gantt";
    public const string Kanban = "kanban";
    public const string Composer = "composer";

    /// <summary>ピッカー・ダイアログ・詳細パネルなど、開いている層の中で働くキー（UX 規約 UX-13）。</summary>
    public const string Layer = "layer";

    /// <summary>見出しの ID から、表示する見出し。</summary>
    public static string CategoryName(string category) => Strings.ShortcutCategory(category);

    private static ShortcutDefinition D(string id, string category, ShortcutScope scope, params string[] keys) =>
        new(id, category, scope, keys.Select(KeyGesture.Parse).ToList());

    public static IReadOnlyList<ShortcutDefinition> All { get; } =
    [
        // 全体
        D("task.new", Global, ShortcutScope.Global, "N", "Ctrl+N"),
        D("app.sync", Global, ShortcutScope.Global, "F5"),
        D("app.help", Global, ShortcutScope.Global, "Shift+Slash"),
        D("nav.settings", Global, ShortcutScope.Global, "Ctrl+Comma"),
        D("app.palette", Global, ShortcutScope.Global, "Ctrl+K"),
        D("view.filter", Global, ShortcutScope.Global, "F"),
        D("view.sort", Global, ShortcutScope.Global, "Y"),
        D("view.group", Global, ShortcutScope.Global, "G"),

        // 選んでいるタスク（画面を移らない操作は 1 文字にする）
        D("task.detail", Task, ShortcutScope.Task, "O"),
        D("task.addChild", Task, ShortcutScope.Task, "Shift+Insert", "Ctrl+Shift+Enter"),
        D("task.status", Task, ShortcutScope.Task, "S"),
        D("task.progress", Task, ShortcutScope.Task, "P"),
        D("task.toggleDone", Task, ShortcutScope.Task, "X"),
        D("task.assign", Task, ShortcutScope.Task, "A"),
        D("task.assignMe", Task, ShortcutScope.Task, "I"),
        D("task.due", Task, ShortcutScope.Task, "D"),
        D("task.estimate", Task, ShortcutScope.Task, "E"),
        D("task.rename", Task, ShortcutScope.Task, "R"),
        D("task.plan", Task, ShortcutScope.Task, "M"),
        D("task.predecessor", Task, ShortcutScope.Task, "B"),
        D("task.project", Task, ShortcutScope.Task, "Shift+P"),
        D("task.repository", Task, ShortcutScope.Task, "Shift+R"),
        D("task.branch", Task, ShortcutScope.Task, "Shift+B"),
        D("task.openInBrowser", Task, ShortcutScope.Task, "Shift+O"),
        D("task.copyKey", Task, ShortcutScope.Task, "Ctrl+Period"),
        D("task.copyUrl", Task, ShortcutScope.Task, "Ctrl+Shift+C"),
        D("task.delete", Task, ShortcutScope.Task, "Delete"),

        // 計画の表
        D("wbs.insertAfter", Wbs, ShortcutScope.Wbs, "Insert", "Ctrl+Enter"),
        D("wbs.indent", Wbs, ShortcutScope.Wbs, "Tab"),
        D("wbs.outdent", Wbs, ShortcutScope.Wbs, "Shift+Tab"),
        D("wbs.moveUp", Wbs, ShortcutScope.Wbs, "Alt+Up"),
        D("wbs.moveDown", Wbs, ShortcutScope.Wbs, "Alt+Down"),
        D("wbs.collapse", Wbs, ShortcutScope.Wbs, "Alt+Left"),
        D("wbs.expand", Wbs, ShortcutScope.Wbs, "Alt+Right"),
        D("wbs.bulk", Wbs, ShortcutScope.Wbs, "Ctrl+Shift+N"),

        // ガントチャート
        D("gantt.today", Gantt, ShortcutScope.Gantt, "T"),
        D("gantt.edit", Gantt, ShortcutScope.Gantt, "Ctrl+E"),
        D("gantt.scale", Gantt, ShortcutScope.Gantt, "V"),
        D("gantt.moveEarlier", Gantt, ShortcutScope.Gantt, "Shift+Left"),
        D("gantt.moveLater", Gantt, ShortcutScope.Gantt, "Shift+Right"),
        D("gantt.shrink", Gantt, ShortcutScope.Gantt, "Ctrl+Shift+Left"),
        D("gantt.extend", Gantt, ShortcutScope.Gantt, "Ctrl+Shift+Right"),
        D("preview.confirm", Gantt, ShortcutScope.Preview, "Ctrl+Enter"),

        // カンバン（ドラッグの代わり。UX 規約 UX-21）
        D("kanban.movePrev", Kanban, ShortcutScope.Kanban, "Shift+Left"),
        D("kanban.moveNext", Kanban, ShortcutScope.Kanban, "Shift+Right"),

        // タスクの追加（文字を入力中でも使えるよう Alt と組み合わせる）
        D("composer.submit", Composer, ShortcutScope.Composer, "Ctrl+Enter"),
        D("composer.project", Composer, ShortcutScope.Composer, "Alt+P"),
        D("composer.status", Composer, ShortcutScope.Composer, "Alt+S"),
        D("composer.assign", Composer, ShortcutScope.Composer, "Alt+A"),
        D("composer.due", Composer, ShortcutScope.Composer, "Alt+D"),
        D("composer.estimate", Composer, ShortcutScope.Composer, "Alt+E"),
        D("composer.kind", Composer, ShortcutScope.Composer, "Alt+M"),
        D("composer.detail", Composer, ShortcutScope.Composer, "Ctrl+D"),
    ];

    /// <summary>割り当てを変えられない操作。</summary>
    public static IReadOnlyList<FixedShortcut> Fixed { get; } =
    [
        new("close", Global),
        new("focusPane", Global),
        new("navigate", Global),
        new("switchView", Global),
        new("undo", Global),
        new("redo", Global),
        new("select", Task),
        new("extend", Task),
        new("moveFocus", Task),
        new("selectAll", Task),
        new("open", Task),
        new("toggleDone", Task),
        new("menu", Task),
        new("editCell", Wbs),
        new("moveCell", Wbs),
        new("chooseNth", Layer),
        new("moveOption", Layer),
        new("confirm", Layer),
        new("nextField", Layer),
        new("closeLayer", Layer),
    ];

    public static ShortcutDefinition? Find(string id) => All.FirstOrDefault(d => d.Id == id);

    /// <summary>
    /// 二つの範囲が同時に働きうるか。追加ダイアログは他と重ならない。計画の表・ガント・カンバンは同時に表示しない。
    /// </summary>
    public static bool CanOverlap(ShortcutScope a, ShortcutScope b)
    {
        if (a == ShortcutScope.Composer || b == ShortcutScope.Composer)
        {
            return a == b;
        }

        // 計画の表・ガント・カンバンは同時に表示しない
        static int View(ShortcutScope s) => s switch
        {
            ShortcutScope.Wbs => 1,
            ShortcutScope.Gantt or ShortcutScope.Preview => 2,
            ShortcutScope.Kanban => 3,
            _ => 0,
        };

        return View(a) == 0 || View(b) == 0 || View(a) == View(b);
    }

    /// <summary>
    /// 割り当てに使えないキー（画面の基本操作と、元に戻す・やり直すに使うもの）。
    /// 計画の表では Tab を字下げに使う。
    /// </summary>
    public static bool IsReserved(KeyGesture gesture, ShortcutScope scope)
    {
        var key = gesture.Key;
        var mods = gesture.Modifiers;
        if (key is "Escape" || (mods == KeyModifiers.None && key is "Enter" or "Up" or "Down" or "Left" or "Right" or "Space"))
        {
            return true;
        }

        // 元に戻す・やり直すは Windows の標準の割り当てに従う
        if ((key is "Z" && mods is KeyModifiers.Ctrl or (KeyModifiers.Ctrl | KeyModifiers.Shift)) || (key is "Y" && mods == KeyModifiers.Ctrl))
        {
            return true;
        }

        if (key is "Tab" && mods is KeyModifiers.None or KeyModifiers.Shift)
        {
            return scope != ShortcutScope.Wbs;
        }

        bool digit = key.StartsWith("Number", StringComparison.Ordinal);
        return digit && (mods == KeyModifiers.Alt || mods == KeyModifiers.Ctrl
            || (mods == KeyModifiers.None && scope == ShortcutScope.Composer));
    }

    /// <summary>修飾キーだけのキー。単独では割り当てない。</summary>
    public static bool IsModifierKey(string key) =>
        key is "Control" or "Shift" or "Menu" or "LeftControl" or "RightControl" or "LeftShift" or "RightShift"
            or "LeftMenu" or "RightMenu" or "LeftWindows" or "RightWindows";
}
