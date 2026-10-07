using Tasklabe.Core.Domain;

namespace Tasklabe.GitHub.Tests;

public class AccessProblemTests
{
    [Fact]
    public void OAuth_app_restriction_is_detected()
    {
        var ex = new GitHubGraphQLException(
            "Although you appear to have the correct authorization credentials, the `acme` organization has enabled OAuth App access restrictions, "
            + "meaning that data access to third-parties is limited.",
            ["FORBIDDEN"]);

        Assert.Equal(OrganizationAccessProblem.OAuthAppNotApproved, GitHubApi.ClassifyAccessProblem(ex));
    }

    [Fact]
    public void Saml_enforcement_is_detected()
    {
        var ex = new GitHubGraphQLException(
            "Resource protected by organization SAML enforcement. You must grant your OAuth token access to this organization.",
            ["FORBIDDEN"]);

        Assert.Equal(OrganizationAccessProblem.SamlSsoRequired, GitHubApi.ClassifyAccessProblem(ex));
    }

    [Fact]
    public void Other_forbidden_errors_are_generic()
    {
        var ex = new GitHubGraphQLException("Resource not accessible by integration", ["FORBIDDEN"]);

        Assert.Equal(OrganizationAccessProblem.Forbidden, GitHubApi.ClassifyAccessProblem(ex));
    }

    [Fact]
    public void Unrelated_errors_are_not_access_problems()
    {
        var ex = new GitHubGraphQLException("Something went wrong", ["INTERNAL"]);

        Assert.Equal(OrganizationAccessProblem.None, GitHubApi.ClassifyAccessProblem(ex));
    }
}
