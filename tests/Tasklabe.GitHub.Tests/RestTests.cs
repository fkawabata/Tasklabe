using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Tasklabe.GitHub.Tests;

/// <summary>REST API の呼び出し（自動リンク、マイルストーン）の、要求の作り方と応答の扱い。</summary>
public class RestTests
{
    /// <summary>決まった状態を返し、受けた要求を覚える GitHub。</summary>
    private sealed class Reply(HttpStatusCode status, string? json = null) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public JsonNode? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(status)
            {
                Content = json is null ? null : new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>つながらない GitHub。</summary>
    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("接続できません");
    }

    private static GitHubApi Api(HttpMessageHandler github, string? token = "token") =>
        new(new HttpClient(github), GitHubHost.Default, () => token);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Autolink_is_posted_to_the_repository_with_the_token_and_the_rest_media_type()
    {
        var github = new Reply(HttpStatusCode.Created);

        var result = await Api(github).EnsureAutolinkAsync("me/repo", "TASK", Ct);

        Assert.Equal(AutolinkResult.Registered, result);
        Assert.Equal(HttpMethod.Post, github.Request!.Method);
        Assert.Equal("/repos/me/repo/autolinks", github.Request.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer token", github.Request.Headers.Authorization!.ToString());
        Assert.Contains("application/vnd.github+json", github.Request.Headers.Accept.ToString());
        Assert.Equal("TASK-", (string?)github.Body!["key_prefix"]);
        Assert.Equal("https://github.com/me/repo/issues/<num>", (string?)github.Body["url_template"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity, AutolinkResult.AlreadyRegistered)]
    [InlineData(HttpStatusCode.Forbidden, AutolinkResult.NotPermitted)]
    [InlineData(HttpStatusCode.NotFound, AutolinkResult.NotPermitted)]
    public async Task Autolink_status_is_read_as_a_result(HttpStatusCode status, AutolinkResult expected)
    {
        Assert.Equal(expected, await Api(new Reply(status)).EnsureAutolinkAsync("me/repo", "TASK", Ct));
    }

    [Fact]
    public async Task Autolink_unauthorized_is_an_authentication_error()
    {
        await Assert.ThrowsAsync<GitHubAuthenticationException>(
            () => Api(new Reply(HttpStatusCode.Unauthorized)).EnsureAutolinkAsync("me/repo", "TASK", Ct));
    }

    [Fact]
    public async Task Autolink_server_error_keeps_the_status()
    {
        var ex = await Assert.ThrowsAsync<GitHubHttpException>(
            () => Api(new Reply(HttpStatusCode.InternalServerError)).EnsureAutolinkAsync("me/repo", "TASK", Ct));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
    }

    [Fact]
    public async Task Milestone_without_permission_explains_the_required_role()
    {
        var ex = await Assert.ThrowsAsync<GitHubHttpException>(
            () => Api(new Reply(HttpStatusCode.Forbidden)).CreateMilestoneAsync("me/repo", "リリース", null, ct: Ct));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Contains("Write", ex.Message);
    }

    [Fact]
    public async Task Milestone_unauthorized_is_an_authentication_error()
    {
        await Assert.ThrowsAsync<GitHubAuthenticationException>(
            () => Api(new Reply(HttpStatusCode.Unauthorized)).DeleteMilestoneAsync("me/repo", 3, Ct));
    }

    [Fact]
    public async Task Milestone_delete_accepts_no_content()
    {
        var github = new Reply(HttpStatusCode.NoContent);

        await Api(github).DeleteMilestoneAsync("me/repo", 3, Ct);

        Assert.Equal(HttpMethod.Delete, github.Request!.Method);
        Assert.Equal("/repos/me/repo/milestones/3", github.Request.RequestUri!.AbsolutePath);
        Assert.Null(github.Body);
    }

    [Fact]
    public async Task Rest_call_without_connection_is_unavailable()
    {
        await Assert.ThrowsAsync<GitHubUnavailableException>(() => Api(new Offline()).EnsureAutolinkAsync("me/repo", "TASK", Ct));
        await Assert.ThrowsAsync<GitHubUnavailableException>(() => Api(new Offline()).DeleteMilestoneAsync("me/repo", 3, Ct));
    }

    [Fact]
    public async Task Rest_call_without_sign_in_is_an_authentication_error()
    {
        var github = new Reply(HttpStatusCode.Created);

        await Assert.ThrowsAsync<GitHubAuthenticationException>(() => Api(github, token: null).EnsureAutolinkAsync("me/repo", "TASK", Ct));
        Assert.Null(github.Request);
    }
}
