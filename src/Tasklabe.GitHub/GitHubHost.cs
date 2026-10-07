namespace Tasklabe.GitHub;

/// <summary>接続プロファイル（技術設計書 6.1 節）。</summary>
public sealed record GitHubHost(string Name, string ClientId)
{
    /// <summary>
    /// 既定の接続先。Release ビルドは公開用の OAuth App、Debug ビルドは開発用の OAuth App で認証する。
    /// Client ID は Device Flow で公開される値であり、秘密情報ではない。
    /// </summary>
    public static GitHubHost Default { get; } = new("github.com", DefaultClientId);

#if DEBUG
    private const string DefaultClientId = "Ov23lii89EebgTmnrWke";
#else
    private const string DefaultClientId = "Ov23li6xpoAd7OB9Wqeb";
#endif

    public const string Scopes = "repo project read:org";

    public bool IsDotCom => Name.Equals("github.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>GitHub.com とデータレジデンシー（*.ghe.com）は api. サブドメイン、Enterprise Server は /api 配下。</summary>
    public Uri GraphQLEndpoint => IsDotCom || Name.EndsWith(".ghe.com", StringComparison.OrdinalIgnoreCase)
        ? new Uri($"https://api.{Name}/graphql")
        : new Uri($"https://{Name}/api/graphql");

    /// <summary>REST API の起点。GitHub.com とデータレジデンシーは api. サブドメイン、Enterprise Server は /api/v3 配下。</summary>
    public Uri RestEndpoint => IsDotCom || Name.EndsWith(".ghe.com", StringComparison.OrdinalIgnoreCase)
        ? new Uri($"https://api.{Name}/")
        : new Uri($"https://{Name}/api/v3/");

    public Uri DeviceCodeEndpoint => new($"https://{Name}/login/device/code");

    public Uri AccessTokenEndpoint => new($"https://{Name}/login/oauth/access_token");
}
