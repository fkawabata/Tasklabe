using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Tasklabe.GitHub.GraphQL;

/// <summary>GitHub GraphQL API への最小限のクライアント。</summary>
internal sealed class GraphQLClient(HttpClient http, GitHubHost host, Func<string?> tokenProvider)
{
    /// <summary>直近の応答で得たレート制限の残量。</summary>
    public RateLimitDto? LastRateLimit { get; private set; }

    public async Task<T> SendAsync<T>(
        string query,
        JsonObject? variables,
        JsonTypeInfo<GraphQLResponse<T>> typeInfo,
        CancellationToken cancellationToken)
        where T : class
    {
        var token = tokenProvider() ?? throw new GitHubAuthenticationException("サインインしていません。");

        var body = new JsonObject { ["query"] = query };
        if (variables is not null)
        {
            body["variables"] = variables;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, host.GraphQLEndpoint)
        {
            Content = JsonContent.Create(body, GitHubJsonContext.Default.JsonObject),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new GitHubUnavailableException("GitHub に接続できません。", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // 調査用に GitHub のリクエスト ID と応答の要約を残す（トークンは含めない）
                var requestId = response.Headers.TryGetValues("X-GitHub-Request-Id", out var ids) ? ids.FirstOrDefault() : null;
                var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new GitHubAuthenticationException(
                    $"GitHub の認証が無効になりました（401, request-id: {requestId}, {detail[..Math.Min(detail.Length, 200)]}）。");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new GitHubHttpException(response.StatusCode, $"GitHub がエラーを返しました（{(int)response.StatusCode}）。");
            }

            var result = await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken).ConfigureAwait(false)
                ?? throw new GitHubException("GitHub から空の応答が返されました。");

            if (result.Errors is { Count: > 0 } errors)
            {
                throw new GitHubGraphQLException(
                    string.Join(" / ", errors.Select(e => e.Message)),
                    errors.Select(e => e.Type ?? "").ToList());
            }

            var data = result.Data ?? throw new GitHubException("GitHub から空の応答が返されました。");
            if (data is ViewerData { RateLimit: { } r1 }) LastRateLimit = r1;
            if (data is ViewerProjectsData { RateLimit: { } r2 }) LastRateLimit = r2;
            if (data is ProjectNodeData { RateLimit: { } r3 }) LastRateLimit = r3;
            if (data is OrganizationData { RateLimit: { } r4 }) LastRateLimit = r4;
            return data;
        }
    }
}
