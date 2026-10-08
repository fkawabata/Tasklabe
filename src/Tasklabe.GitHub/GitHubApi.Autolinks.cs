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

    /// <summary>
    /// リポジトリの自動リンクのうち、このリポジトリの Issue 以外へつなぐキー（key_prefix が「キー-」のもの）。
    /// 一覧を読むにはリポジトリの管理者の権限が要るため、読めないときは null を返す。
    /// </summary>
    public async Task<IReadOnlyList<string>?> GetForeignAutolinkKeysAsync(string nameWithOwner, CancellationToken ct = default)
    {
        var (owner, name) = Split(nameWithOwner);
        using var response = await SendRestAsync(HttpMethod.Get, $"repos/{owner}/{name}/autolinks", null, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            return null;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new GitHubAuthenticationException("GitHub の認証が無効になりました（401）。");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new GitHubHttpException(response.StatusCode, $"GitHub がエラーを返しました（{(int)response.StatusCode}）。");
        }

        var own = $"https://{_host.Name}/{owner}/{name}/issues/<num>";
        var keys = new List<string>();
        if (JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) is JsonArray links)
        {
            foreach (var link in links.OfType<JsonObject>())
            {
                var prefix = link["key_prefix"]?.GetValue<string>();
                var url = link["url_template"]?.GetValue<string>();
                if (prefix is { Length: > 1 } && prefix.EndsWith('-') && !string.Equals(url, own, StringComparison.OrdinalIgnoreCase))
                {
                    keys.Add(prefix[..^1].ToUpperInvariant());
                }
            }
        }

        return keys;
    }
}
