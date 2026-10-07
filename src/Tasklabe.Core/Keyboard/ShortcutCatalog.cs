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
/// <param name="Category">一覧と設定画面での見出し。</param>
public sealed record ShortcutDefinition(string Id, string Category, string Label, ShortcutScope Scope, IReadOnlyList<KeyGesture> Defaults);

/// <summary>割り当てを変えられないキー操作（一覧に載せて知らせる）。</summary>
public sealed record FixedShortcut(string Keys, string Category, string Label);

/// <summary>ショートカットの一覧（UI デザイン設計書 4.1 節）。</summary>
public static class ShortcutCatalog
{
    public const string Global = "全体";
    public const string Task = "選んでいるタスク";
    public const string Wbs = "計画の表";
    public const string Gantt = "ガントチャート";
    public const string Kanban = "カンバン";
    public const string Composer = "タスクの追加";

    /// <summary>ピッカー・ダイアログ・詳細パネルなど、開いている層の中で働くキー（UX 規約 UX-13）。</summary>
    public const string Layer = "ピッカー・ダイアログ";

    private static ShortcutDefinition D(string id, string category, string label, ShortcutScope scope, params string[] keys) =>
        new(id, category, label, scope, keys.Select(KeyGesture.Parse).ToList());

    public static IReadOnlyList<ShortcutDefinition> All { get; } =
    [
        // 全体
        D("task.new", Global, "タスクを追加", ShortcutScope.Global, "N", "Ctrl+N"),
        D("app.sync", Global, "今すぐ同期", ShortcutScope.Global, "F5"),
        D("app.help", Global, "キーの一覧を開く", ShortcutScope.Global, "Shift+Slash"),
        D("nav.settings", Global, "設定を開く", ShortcutScope.Global, "Ctrl+Comma"),
        D("app.palette", Global, "コマンドパレットを開く", ShortcutScope.Global, "Ctrl+K"),
        D("view.filter", Global, "絞り込む", ShortcutScope.Global, "F"),
        D("view.sort", Global, "並び順を選ぶ", ShortcutScope.Global, "Y"),
        D("view.group", Global, "グループを選ぶ", ShortcutScope.Global, "G"),

        // 選んでいるタスク（画面を移らない操作は 1 文字にする）
        D("task.detail", Task, "詳細を開く", ShortcutScope.Task, "O"),
        D("task.addChild", Task, "子タスクを追加", ShortcutScope.Task, "Shift+Insert", "Ctrl+Shift+Enter"),
        D("task.status", Task, "ステータスを変える", ShortcutScope.Task, "S"),
        D("task.progress", Task, "進捗率を変える", ShortcutScope.Task, "P"),
        D("task.toggleDone", Task, "完了にする／戻す", ShortcutScope.Task, "X"),
        D("task.assign", Task, "担当者を変える", ShortcutScope.Task, "A"),
        D("task.assignMe", Task, "自分を担当にする／外す", ShortcutScope.Task, "I"),
        D("task.due", Task, "期日を決める", ShortcutScope.Task, "D"),
        D("task.estimate", Task, "想定工数を決める", ShortcutScope.Task, "E"),
        D("task.rename", Task, "タイトルを変える", ShortcutScope.Task, "R"),
        D("task.plan", Task, "計画に移す／課題へ戻す", ShortcutScope.Task, "M"),
        D("task.predecessor", Task, "先行タスクを追加", ShortcutScope.Task, "B"),
        D("task.project", Task, "別のプロジェクトへ移す", ShortcutScope.Task, "Shift+P"),
        D("task.repository", Task, "作業するリポジトリを選ぶ", ShortcutScope.Task, "Shift+R"),
        D("task.branch", Task, "ブランチを作る・選ぶ", ShortcutScope.Task, "Shift+B"),
        D("task.openInBrowser", Task, "GitHub で開く", ShortcutScope.Task, "Shift+O"),
        D("task.copyKey", Task, "番号をコピー", ShortcutScope.Task, "Ctrl+Period"),
        D("task.copyUrl", Task, "Issue の URL をコピー", ShortcutScope.Task, "Ctrl+Shift+C"),
        D("task.delete", Task, "削除", ShortcutScope.Task, "Delete"),

        // 計画の表
        D("wbs.insertAfter", Wbs, "下に行を追加", ShortcutScope.Wbs, "Insert", "Ctrl+Enter"),
        D("wbs.indent", Wbs, "字下げ（上の行の子にする）", ShortcutScope.Wbs, "Tab"),
        D("wbs.outdent", Wbs, "字上げ", ShortcutScope.Wbs, "Shift+Tab"),
        D("wbs.moveUp", Wbs, "行を上へ", ShortcutScope.Wbs, "Alt+Up"),
        D("wbs.moveDown", Wbs, "行を下へ", ShortcutScope.Wbs, "Alt+Down"),
        D("wbs.collapse", Wbs, "折りたたむ", ShortcutScope.Wbs, "Alt+Left"),
        D("wbs.expand", Wbs, "展開する", ShortcutScope.Wbs, "Alt+Right"),
        D("wbs.bulk", Wbs, "計画をまとめて入力", ShortcutScope.Wbs, "Ctrl+Shift+N"),

        // ガントチャート
        D("gantt.today", Gantt, "今日へ移動", ShortcutScope.Gantt, "T"),
        D("gantt.edit", Gantt, "編集モードに入る／抜ける", ShortcutScope.Gantt, "Ctrl+E"),
        D("gantt.scale", Gantt, "表示の単位を切り替える（日・週・月）", ShortcutScope.Gantt, "V"),
        D("gantt.moveEarlier", Gantt, "予定を 1 日前へ", ShortcutScope.Gantt, "Shift+Left"),
        D("gantt.moveLater", Gantt, "予定を 1 日後へ", ShortcutScope.Gantt, "Shift+Right"),
        D("gantt.shrink", Gantt, "終わりを 1 日縮める", ShortcutScope.Gantt, "Ctrl+Shift+Left"),
        D("gantt.extend", Gantt, "終わりを 1 日延ばす", ShortcutScope.Gantt, "Ctrl+Shift+Right"),
        D("preview.confirm", Gantt, "日程の調整を確定", ShortcutScope.Preview, "Ctrl+Enter"),

        // カンバン（ドラッグの代わり。UX 規約 UX-21）
        D("kanban.movePrev", Kanban, "カードを前の列へ移す", ShortcutScope.Kanban, "Shift+Left"),
        D("kanban.moveNext", Kanban, "カードを次の列へ移す", ShortcutScope.Kanban, "Shift+Right"),

        // タスクの追加（文字を入力中でも使えるよう Alt と組み合わせる）
        D("composer.submit", Composer, "追加する", ShortcutScope.Composer, "Ctrl+Enter"),
        D("composer.project", Composer, "追加先を選ぶ", ShortcutScope.Composer, "Alt+P"),
        D("composer.status", Composer, "ステータスを選ぶ", ShortcutScope.Composer, "Alt+S"),
        D("composer.assign", Composer, "担当者を選ぶ", ShortcutScope.Composer, "Alt+A"),
        D("composer.due", Composer, "期日を選ぶ", ShortcutScope.Composer, "Alt+D"),
        D("composer.estimate", Composer, "工数を選ぶ", ShortcutScope.Composer, "Alt+E"),
        D("composer.kind", Composer, "区分を選ぶ", ShortcutScope.Composer, "Alt+M"),
        D("composer.detail", Composer, "詳細パネルで入力を続ける", ShortcutScope.Composer, "Ctrl+D"),
    ];

    /// <summary>割り当てを変えられない操作。</summary>
    public static IReadOnlyList<FixedShortcut> Fixed { get; } =
    [
        new("Esc", Global, "一番内側の 1 層を閉じる（ピッカー → ダイアログ → 詳細パネル → 選択や編集モード）"),
        new("F6", Global, "詳細パネルと画面のあいだでフォーカスを移す（パネルがなければナビゲーションと画面）"),
        new("Alt + 1〜9", Global, "ナビゲーションの n 番目へ移る（1 はマイタスク）"),
        new("Ctrl + 1〜5", Global, "ビューを切り替える"),
        new("Ctrl + Z", Global, "元に戻す（文字の入力中は入力の取り消し）"),
        new("Ctrl + Y / Ctrl + Shift + Z", Global, "やり直す"),
        new("↑ / ↓", Task, "タスクを選ぶ"),
        new("Shift + ↑ / ↓", Task, "複数のタスクを範囲で選ぶ"),
        new("Ctrl + ↑ / ↓、Ctrl + Space", Task, "選択を変えずに移る、フォーカスのあるタスクを選ぶ・外す"),
        new("Ctrl + A", Task, "すべて選ぶ"),
        new("Enter", Task, "詳細を開く（計画の表ではセルを編集）"),
        new("Space", Task, "完了にする／戻す（一覧と表）"),
        new("Shift + F10", Task, "タスクのメニューを開く"),
        new("F2", Wbs, "セルを編集"),
        new("← / →", Wbs, "セルを移る"),
        new("1〜9、0", Layer, "n 番目の候補を選ぶ（複数を選ぶピッカーでは選ぶ・外すを切り替える）"),
        new("↑ / ↓、Home / End", Layer, "候補を移る"),
        new("Enter", Layer, "決める"),
        new("Tab / Shift + Tab", Layer, "入力欄と候補、次の項目・前の項目へ移る"),
        new("Esc", Layer, "閉じる（複数を選ぶピッカーでは、選んだ内容を反映して閉じる）"),
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
