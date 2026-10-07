using System.Net;

namespace Tasklabe.GitHub;

public class GitHubException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>トークンが無効または失効している。</summary>
public sealed class GitHubAuthenticationException(string message) : GitHubException(message);

/// <summary>ネットワークに接続できない。</summary>
public sealed class GitHubUnavailableException(string message, Exception? inner = null) : GitHubException(message, inner);

/// <summary>GraphQL がエラーを返した。</summary>
public sealed class GitHubGraphQLException(string message, IReadOnlyList<string> types) : GitHubException(message)
{
    public IReadOnlyList<string> ErrorTypes { get; } = types;
}

public sealed class GitHubHttpException(HttpStatusCode status, string message) : GitHubException(message)
{
    public HttpStatusCode StatusCode { get; } = status;
}
