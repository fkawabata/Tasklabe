using System.Net;
using System.Text;

namespace Tasklabe.GitHub.Tests;

public class ProjectAccessTests
{
    private sealed class Reply(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }

    private static Task<ProjectAccess> AccessAsync(bool canUpdate, string? permission) =>
        new GitHubApi(new HttpClient(new Reply($$"""
            { "data": { "node": { "id": "PVT_1", "url": "https://github.com/orgs/o/projects/1", "viewerCanUpdate": {{(canUpdate ? "true" : "false")}},
              "repositories": { "nodes": [ { "id": "R_1", "nameWithOwner": "o/repo", "viewerPermission": {{(permission is null ? "null" : $"\"{permission}\"")}} } ] } } } }
            """)), GitHubHost.Default, () => "token").GetProjectAccessAsync("PVT_1", TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("ADMIN", true)]
    [InlineData("MAINTAIN", true)]
    [InlineData("WRITE", true)]
    [InlineData("TRIAGE", false)]
    [InlineData("READ", false)]
    [InlineData(null, false)]
    public async Task Repository_needs_write_permission_to_create_tasks(string? permission, bool canWrite)
    {
        var access = await AccessAsync(canUpdate: true, permission);

        Assert.Equal(canWrite, access.CanWriteRepository);
        Assert.Equal("o/repo", access.RepositoryNameWithOwner);
    }

    [Fact]
    public async Task Read_only_project_is_reported()
    {
        var access = await AccessAsync(canUpdate: false, "WRITE");

        Assert.False(access.CanUpdateProject);
        Assert.True(access.CanWriteRepository);
    }
}
