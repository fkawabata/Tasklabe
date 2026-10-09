using System.Text;

namespace Tasklabe.Core.Domain;

/// <summary>
/// 担当者の表し方。GitHub の利用者はログイン名で、GitHub を使わない人（名前だけのメンバー）は名前の前に印を付けて、
/// タスクの担当者の同じ一覧に並べる。名前だけのメンバーは Issue の Assignees に入れられないため、
/// Project のテキストフィールド <see cref="ProjectConventions.Fields.GuestAssignees"/> に名前をカンマで区切って保存する。
/// </summary>
public static class People
{
    /// <summary>名前だけのメンバーの印。GitHub のログイン名に使えない文字にする。</summary>
    public const char GuestMark = '~';

    public const int MaxGuestNameLength = 40;

    public static bool IsGuest(string assignee) => assignee.Length > 0 && assignee[0] == GuestMark;

    /// <summary>名前だけのメンバーを、担当者の一覧に入れる形にする。</summary>
    public static string Guest(string name) => GuestMark + name;

    /// <summary>印を除いた名前（ログイン名、またはメンバーの名前）。アバターや負荷の行に使う。</summary>
    public static string Name(string assignee) => IsGuest(assignee) ? assignee[1..] : assignee;

    /// <summary>画面に出す形。GitHub の利用者は @ログイン名、名前だけのメンバーは名前のまま。</summary>
    public static string Display(string assignee) => IsGuest(assignee) ? assignee[1..] : "@" + assignee;

    /// <summary>GitHub の利用者だけを取り出す。</summary>
    public static IReadOnlyList<string> Logins(IEnumerable<string> assignees) => [.. assignees.Where(a => !IsGuest(a))];

    /// <summary>入力した名前を整える（前後と続く空白を詰め、全角の英数字を半角にする）。</summary>
    public static string NormalizeGuestName(string text) =>
        string.Join(' ', text.Normalize(NormalizationForm.FormKC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>名前だけのメンバーの名前に使えないものなら、理由を返す。</summary>
    public static string? GuestNameProblem(string name) =>
        name.Length == 0 ? "名前を入力してください"
        : name.Contains(',', StringComparison.Ordinal) ? "名前にカンマは使えません"
        : name[0] is '@' or GuestMark ? $"名前の先頭に {name[0]} は使えません"
        : name.Length > MaxGuestNameLength ? $"名前は {MaxGuestNameLength} 文字までです"
        : null;

    /// <summary>担当者のうち名前だけのメンバーを、保存するテキスト（名前を昇順に並べてカンマで区切る）にする。いなければ null。</summary>
    public static string? GuestNames(IEnumerable<string> assignees)
    {
        var names = assignees.Where(IsGuest).Select(Name).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        return names.Count == 0 ? null : string.Join(", ", names);
    }

    /// <summary>保存したテキストから、名前だけのメンバーを担当者の形で読む。GitHub で手で書いた改行の区切りも受け付ける。</summary>
    public static IReadOnlyList<string> ParseGuestNames(string? text) => text is null
        ? []
        : [.. text.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeGuestName)
            .Where(n => GuestNameProblem(n) is null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(Guest)];
}
