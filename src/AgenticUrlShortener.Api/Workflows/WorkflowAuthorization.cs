using System.Security.Claims;

namespace AgenticUrlShortener.Api.Workflows;

public static class WorkflowAuthorization
{
    public static bool HasPermission(ClaimsPrincipal principal, string permission)
    {
        if (string.IsNullOrWhiteSpace(GetSubject(principal))) return false;

        return principal.Claims
            .Where(claim => claim.Type is "scope" or "scp" or "roles" or ClaimTypes.Role)
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(permission, StringComparer.Ordinal);
    }

    public static string GetActor(ClaimsPrincipal principal) =>
        GetSubject(principal) ?? throw new UnauthorizedAccessException("A validated subject claim is required.");

    private static string? GetSubject(ClaimsPrincipal principal) =>
        principal.FindFirst("sub")?.Value is { Length: > 0 } subject ? subject : null;
}