using Microsoft.AspNetCore.Authorization;

namespace PatientFlow.Mcp.Auth;

/// <summary>
/// Central place to declare the authorization policies used by MCP endpoints.
///
/// Keeping this as a static helper (instead of just inlining
/// <c>AddAuthorization</c> in Program.cs) means new scopes/policies land in
/// one file rather than being scattered as string literals across handlers.
/// </summary>
public static class McpAuthorizationPolicy
{
    /// <summary>
    /// Applied to the MCP protocol endpoints. Requires a valid API key
    /// (authenticated via <see cref="ApiKeyAuthHandler"/>) AND the
    /// <c>mcp:read</c> scope. Future write-capable policies would add
    /// <c>RequireClaim("scope", "mcp:write")</c> alongside.
    /// </summary>
    public const string RequireMcpRead = "RequireMcpRead";

    public static AuthorizationOptions AddMcpPolicies(this AuthorizationOptions options)
    {
        options.AddPolicy(RequireMcpRead, policy =>
        {
            policy.AddAuthenticationSchemes(ApiKeyAuthHandler.SchemeName);
            policy.RequireAuthenticatedUser();
            policy.RequireRole("mcp-agent");
            policy.RequireClaim("scope", "mcp:read");
        });

        return options;
    }
}
