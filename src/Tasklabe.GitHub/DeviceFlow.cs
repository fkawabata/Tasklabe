using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Tasklabe.GitHub.Resources;

namespace Tasklabe.GitHub;

public sealed record DeviceCode(string UserCode, Uri VerificationUri, string Code, TimeSpan Interval, DateTimeOffset ExpiresAt);

/// <summary>OAuth Device Flow によるトークンの取得。</summary>
public sealed class DeviceFlow(HttpClient http, GitHubHost host)
{
    public async Task<DeviceCode> RequestCodeAsync(CancellationToken cancellationToken = default)
    {
        var response = await PostAsync(host.DeviceCodeEndpoint, new Dictionary<string, string>
        {
            ["client_id"] = host.ClientId,
            ["scope"] = GitHubHost.Scopes,
        }, cancellationToken).ConfigureAwait(false);

        if (response.DeviceCode is null || response.UserCode is null || response.VerificationUri is null)
        {
            throw new GitHubException(response.ErrorDescription ?? response.Error ?? Strings.DeviceCodeFailed);
        }

        return new DeviceCode(
            response.UserCode,
            new Uri(response.VerificationUri),
            response.DeviceCode,
            TimeSpan.FromSeconds(Math.Max(response.Interval, 5)),
            DateTimeOffset.UtcNow.AddSeconds(response.ExpiresIn));
    }

    /// <summary>利用者がブラウザで承認するまで待ち、アクセストークンを返す。</summary>
    public async Task<string> WaitForTokenAsync(DeviceCode code, CancellationToken cancellationToken = default)
    {
        var interval = code.Interval;

        while (DateTimeOffset.UtcNow < code.ExpiresAt)
        {
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);

            var response = await PostAsync(host.AccessTokenEndpoint, new Dictionary<string, string>
            {
                ["client_id"] = host.ClientId,
                ["device_code"] = code.Code,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            }, cancellationToken).ConfigureAwait(false);

            if (response.AccessToken is { Length: > 0 } token)
            {
                return token;
            }

            switch (response.Error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    continue;
                case "access_denied":
                    throw new GitHubAuthenticationException(Strings.SignInCanceled);
                case "expired_token":
                    throw new GitHubAuthenticationException(Strings.CodeExpired);
                default:
                    throw new GitHubAuthenticationException(response.ErrorDescription ?? response.Error ?? Strings.SignInFailed);
            }
        }

        throw new GitHubAuthenticationException(Strings.CodeExpired);
    }

    private async Task<OAuthResponse> PostAsync(Uri uri, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.ParseAdd("application/json");

        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.OAuthResponse, cancellationToken).ConfigureAwait(false)
                ?? throw new GitHubException(Strings.EmptyResponse);
        }
        catch (HttpRequestException ex)
        {
            throw new GitHubUnavailableException(Strings.CannotConnect, ex);
        }
    }
}

internal sealed class OAuthResponse
{
    [JsonPropertyName("device_code")] public string? DeviceCode { get; set; }
    [JsonPropertyName("user_code")] public string? UserCode { get; set; }
    [JsonPropertyName("verification_uri")] public string? VerificationUri { get; set; }
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("interval")] public int Interval { get; set; }
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
}
