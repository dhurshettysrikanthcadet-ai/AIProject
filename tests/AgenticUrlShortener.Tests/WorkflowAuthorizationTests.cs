using System.Security.Claims;
using AgenticUrlShortener.Api.Workflows;

namespace AgenticUrlShortener.Tests;

public sealed class WorkflowAuthorizationTests
{
    [Theory]
    [InlineData("scope", "workflow.read workflow.approve")]
    [InlineData("scp", "workflow.read workflow.approve")]
    [InlineData("roles", "workflow.read workflow.approve")]
    public void HasPermission_AcceptsConfiguredPermissionClaims(string claimType, string value)
    {
        var principal = Principal(new Claim(claimType, value));

        Assert.True(WorkflowAuthorization.HasPermission(principal, "workflow.approve"));
        Assert.False(WorkflowAuthorization.HasPermission(principal, "workflow.operator"));
    }

    [Fact]
    public void HasPermission_RequiresVerifiedSubject()
    {
        var identity = new ClaimsIdentity([new Claim("scope", "workflow.approve")], "test");

        Assert.False(WorkflowAuthorization.HasPermission(new ClaimsPrincipal(identity), "workflow.approve"));
    }

    [Fact]
    public void GetActor_UsesSubjectClaim()
    {
        Assert.Equal("issuer-subject", WorkflowAuthorization.GetActor(Principal(new Claim("scope", "workflow.read"))));
    }

    private static ClaimsPrincipal Principal(Claim permission) =>
        new(new ClaimsIdentity([new Claim("sub", "issuer-subject"), permission], "test"));
}