using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace Tasklabe.Core.Resources;

/// <summary>
/// Core の画面の文言（技術設計書 7.1 節）。英語を Strings.resx、日本語を Strings.ja.resx に置き、
/// プロパティの名前をキーとして引く。表示の言語は CultureInfo.CurrentUICulture に従う。
/// </summary>
internal static class Strings
{
    private static readonly ResourceManager Manager = new("Tasklabe.Core.Resources.Strings", typeof(Strings).Assembly);

    private static string Get([CallerMemberName] string key = "") => Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    private static string Format(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Get(key), args);

    /// <summary>操作の名前（UX 規約 UX-28）。キーは「Action_」と操作の ID（. は _ にする）。</summary>
    public static string Action(string id) => Get("Action_" + id.Replace('.', '_'));

    /// <summary>ショートカットの一覧の見出し。</summary>
    public static string ShortcutCategory(string category) => Get("ShortcutCategory_" + category);

    /// <summary>割り当てを変えられないキー操作のキーの表記。</summary>
    public static string FixedKeys(string id) => Get("Fixed_" + id + "_Keys");

    /// <summary>割り当てを変えられないキー操作の名前。</summary>
    public static string FixedLabel(string id) => Get("Fixed_" + id + "_Label");

    /// <summary>祝日の名前。キーは「Holiday_」と祝日の ID。</summary>
    public static string Holiday(string id) => Get("Holiday_" + id);

    /// <summary>タスクの項目の名前（元に戻す・競合の知らせで使う）。</summary>
    public static string Field(string field) => Get("Field_" + field);

    public static string BranchNameEmpty => Get();

    public static string BranchNameInvalidCharacters => Get();

    public static string BranchNameInvalidForm => Get();

    public static string GuestNameEmpty => Get();

    public static string GuestNameComma => Get();

    public static string GuestNameLeadingMark(char mark) => Format(nameof(GuestNameLeadingMark), mark);

    public static string GuestNameTooLong(int max) => Format(nameof(GuestNameTooLong), max);

    public static string ProjectKeyInvalid(int min, int max) => Format(nameof(ProjectKeyInvalid), min, max);

    public static string ProjectKeyReservedForInbox(string key) => Format(nameof(ProjectKeyReservedForInbox), key);

    public static string ProjectKeyInUse(string key) => Format(nameof(ProjectKeyInUse), key);

    public static string ProjectKeyUsedByAutolink(string key) => Format(nameof(ProjectKeyUsedByAutolink), key);

    public static string UndoPredecessorAndSchedule(string title) => Format(nameof(UndoPredecessorAndSchedule), title);

    public static string UndoField(string title, string field) => Format(nameof(UndoField), title, field);

    public static string UndoPredecessorAndOthers(string title, int count) =>
        Format(count == 1 ? "UndoPredecessorAndOthers_One" : "UndoPredecessorAndOthers_Other", title, count);

    public static string UndoTasks(int count) => Format(count == 1 ? "UndoTasks_One" : "UndoTasks_Other", count);

    public static string Untitled => Get();

    public static string OutlineOther => Get();

    public static string OutlineUnassigned => Get();

    public static string OutlineMilestones => Get();

    public static string KanbanUnsetColumn => Get();

    public static string EffortDays(double days) => Format(nameof(EffortDays), days);

    public static string StatusNameEmpty => Get();

    public static string StatusNameDuplicate => Get();

    public static string StatusNeedsNotStarted => Get();

    public static string StatusNeedsDone => Get();

    public static string BulkPlanEndBeforeStart => Get();
}
