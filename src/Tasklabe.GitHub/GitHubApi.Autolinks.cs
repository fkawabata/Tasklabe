using System.Net;
using System.Text.Json.Nodes;

namespace Tasklabe.GitHub;

/// <summary>リポジトリの自動リンクの登録の結果。</summary>
public enum AutolinkResult
{
    /// <summary>新しく登録した。</summary>
    Registered,

    /// <summary>同じキーが既に登録してあった。</summary>
    AlreadyRegistered,

    /// <summary>リポジトリの管理者でないため登録できなかった。</summary>
    NotPermitted,
}

public sealed partial class GitHubApi
{
    /// <summary>
    /// リポジトリの自動リンク（Autolink references）に、タスクの番号のキーを登録する（要件 F-TSK-16）。
    /// 登録すると、コミットやコメントに書いた「キー-番号」が、そのリポジトリの Issue へのリンクになる。
    /// GraphQL には自動リンクの操作がないため、REST API を使う。登録にはリポジトリの管理者の権限が要る。
    /// </summary>
    public async Task<AutolinkResult> EnsureAutolinkAsync(string nameWithOwner, string key, CancellationToken ct = default)
    {
        var (owner, name) = Split(nameWithOwner);
        var body = new JsonObject
        {
            ["key_prefix"] = key + "-",
            ["url_template"] = $"https://{_host.Name}/{owner}/{name}/issues/<num>",
            ["is_alphanumeric"] = false,
        };

        using var response = await SendRestAsync(HttpMethod.Post, $"repos/{owner}/{name}/autolinks", body, ct).ConfigureAwait(false);
        return response.StatusCode switch
        {
            HttpStatusCode.Created => AutolinkResult.Registered,

            // 同じ key_prefix が既にあると 422 を返す
            HttpStatusCode.UnprocessableEntity => AutolinkResult.AlreadyRegistered,

            // 管理者でないと 403、リポジトリが見えないと 404 を返す
            HttpStatusCode.Forbidden or HttpStatusCode.NotFound => AutolinkResult.NotPermitted,
            HttpStatusCode.Unauthorized => throw new GitHubAuthenticationException("GitHub の認証が無効になりました（401）。"),
            _ => throw new GitHubHttpException(response.StatusCode, $"GitHub がエラーを返しました（{(int)response.StatusCode}）。"),
        };
    }
}
