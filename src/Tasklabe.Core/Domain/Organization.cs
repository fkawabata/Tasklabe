namespace Tasklabe.Core.Domain;

/// <summary>Organization のプロジェクトを取得できない理由（要件 M3、技術設計書 6.1 節）。</summary>
public enum OrganizationAccessProblem
{
    None,

    /// <summary>組織の OAuth App アクセス制限により、Tasklabe が承認されていない。</summary>
    OAuthAppNotApproved,

    /// <summary>SAML シングルサインオンの承認がトークンに付与されていない。</summary>
    SamlSsoRequired,

    /// <summary>プロジェクトを参照する権限がない。</summary>
    Forbidden,
}

/// <summary>利用者が所属する Organization と、そのアクセス状態。</summary>
public sealed record Organization(
    string Id,
    string Login,
    string? Name,
    bool CanCreateProjects,
    bool CanCreateRepositories,
    OrganizationAccessProblem Problem = OrganizationAccessProblem.None,
    string? ProblemDetail = null)
{
    public bool IsAccessible => Problem == OrganizationAccessProblem.None;

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Name) || string.Equals(Name.Trim(), Login, StringComparison.OrdinalIgnoreCase)
            ? Login
            : $"{Name.Trim()} ({Login})";
}

/// <summary>チームのタスクを割り当てられる利用者。</summary>
public sealed record Assignee(string Id, string Login, string? Name)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Login : $"{Name.Trim()} (@{Login})";
}
