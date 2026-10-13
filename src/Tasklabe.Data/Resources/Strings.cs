using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace Tasklabe.Data.Resources;

/// <summary>
/// 同期と保存で利用者に示す文言（技術設計書 7.1 節）。英語を Strings.resx、日本語を Strings.ja.resx に置き、
/// プロパティの名前をキーとして引く。表示の言語は CultureInfo.CurrentUICulture に従う。
/// </summary>
internal static class Strings
{
    private static readonly ResourceManager Manager = new("Tasklabe.Data.Resources.Strings", typeof(Strings).Assembly);

    private static string Get([CallerMemberName] string key = "") => Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    private static string Format(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Get(key), args);

    /// <summary>名前を並べるときの区切り（日本語は「、」、英語は「, 」）。</summary>
    public static string ListSeparator => Get();

    public static string PullProblems(string projects) => Format(nameof(PullProblems), projects);

    public static string SyncUnexpectedError => Get();

    public static string GitHubNotResponding(string detail) => Format(nameof(GitHubNotResponding), detail);

    public static string OrganizationNotApproved => Get();

    public static string SamlRequired => Get();

    public static string NoEditPermission => Get();

    public static string NotFoundOnGitHub => Get();

    public static string UnexpectedResponse => Get();

    public static string NewTaskUnreadable => Get();

    public static string ProjectHasNoRepository => Get();

    public static string IssueNotFound => Get();

    public static string TaskRepositoryNotFound => Get();

    public static string CannotAssign(string login) => Format(nameof(CannotAssign), login);

    public static string ProjectNotFound => Get();

    public static string PreparingPersonalRepository => Get();

    public static string PreparingInbox => Get();

    /// <summary>個人用リポジトリの説明。作るときに一度だけ書く。</summary>
    public static string PersonalRepositoryDescription => Get();

    /// <summary>チームプロジェクトのリポジトリの説明。作るときに一度だけ書く。</summary>
    public static string TeamRepositoryDescription(string title) => Format(nameof(TeamRepositoryDescription), title);

    public static string StatusInUse(string name, int count) =>
        Format(count == 1 ? "StatusInUse_One" : "StatusInUse_Other", name, count);

    public static string MilestonesNeedRepository => Get();
}
