using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace Tasklabe.GitHub.Resources;

/// <summary>
/// GitHub との通信で利用者に示す文言（技術設計書 7.1 節）。英語を Strings.resx、日本語を Strings.ja.resx に置き、
/// プロパティの名前をキーとして引く。表示の言語は CultureInfo.CurrentUICulture に従う。
/// </summary>
internal static class Strings
{
    private static readonly ResourceManager Manager = new("Tasklabe.GitHub.Resources.Strings", typeof(Strings).Assembly);

    private static string Get([CallerMemberName] string key = "") => Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    private static string Format(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Get(key), args);

    public static string DeviceCodeFailed => Get();

    public static string SignInCanceled => Get();

    public static string CodeExpired => Get();

    public static string SignInFailed => Get();

    public static string NotSignedIn => Get();

    public static string EmptyResponse => Get();

    public static string CannotConnect => Get();

    public static string AuthenticationInvalid => Get();

    public static string AuthenticationInvalidDetail(string? requestId, string detail) => Format(nameof(AuthenticationInvalidDetail), requestId, detail);

    public static string HttpError(int status) => Format(nameof(HttpError), status);

    public static string ViewerFailed => Get();

    public static string IssueCreateFailed => Get();

    public static string IssueTransferFailed => Get();

    public static string AddToProjectFailed => Get();

    public static string ProjectNotFound => Get();

    public static string ProjectCreateFailed => Get();

    public static string FieldMissing(string name) => Format(nameof(FieldMissing), name);

    public static string FieldAddFailed(string name) => Format(nameof(FieldAddFailed), name);

    public static string StatusFieldMissing => Get();

    public static string KindOptionMissing(string value) => Format(nameof(KindOptionMissing), value);

    public static string ScheduleOptionMissing(string value) => Format(nameof(ScheduleOptionMissing), value);

    /// <summary>Project の「日程の扱い」の選択肢の説明。作るときに一度だけ書く。</summary>
    public static string NonBlockingOptionDescription => Get();

    public static string TaskNotFound => Get();

    public static string MilestoneCreateFailed => Get();

    public static string MilestoneUpdateFailed => Get();

    public static string MilestoneIdUnreadable => Get();

    public static string MilestonePermission => Get();

    public static string RepositoryCreateFailed => Get();

    public static string RepositoryNotFound(string name) => Format(nameof(RepositoryNotFound), name);

    public static string RepositoryHasNoCommits(string name) => Format(nameof(RepositoryHasNoCommits), name);

    public static string BranchCreateFailed => Get();
}
