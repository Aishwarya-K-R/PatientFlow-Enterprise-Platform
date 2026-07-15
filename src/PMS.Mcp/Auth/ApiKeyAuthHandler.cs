using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace PatientFlow.Mcp.Auth;

/// <summary>
/// Authenticates MCP requests using an <c>X-Api-Key</c> header.
///
/// Design:
///  - Per-agent keys (not one shared secret) so we can attribute every tool
///    call to a named agent in logs/audit and revoke a single agent's access
///    without rotating everyone else's key.
///  - Validation is a plain dictionary lookup — no hashing here because the
///    keys are already high-entropy random strings, not user passwords.
///    (If keys are ever derived from user secrets, switch to a hashed compare.)
///  - Health/metrics endpoints are handled by <see cref="AuthenticateAsync"/>
///    returning <c>NoResult</c> when no header is present; the actual gating
///    happens via <c>[Authorize]</c> / endpoint policies, so unauthenticated
///    scrapes of <c>/health</c> and <c>/metrics</c> still work.
/// </summary>
public sealed class ApiKeyAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    private readonly ApiKeyAuthOptions _keyOptions;

    public ApiKeyAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<ApiKeyAuthOptions> keyOptions)
        : base(options, logger, encoder)
    {
        _keyOptions = keyOptions.Value;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // No header present -> let the request through as anonymous. Endpoints
        // that require auth ([Authorize]) will produce a 401 via the challenge
        // path; endpoints that don't (health, metrics) work unaffected.
        if (!Request.Headers.TryGetValue(HeaderName, out var provided) || provided.Count == 0)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var key = provided.ToString();

        if (string.IsNullOrWhiteSpace(key) ||
            !_keyOptions.Keys.TryGetValue(key, out var agentName))
        {
            Logger.LogWarning("MCP request rejected: unknown or empty API key");
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key"));
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, agentName),
            new Claim(ClaimTypes.Role, "mcp-agent"),
            // Scope claim keeps future expansion cheap: today every key gets
            // mcp:read; when we add write tools we add mcp:write on selected
            // keys without changing the handler.
            new Claim("scope", "mcp:read"),
            new Claim("agent_id", agentName)
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers["WWW-Authenticate"] = $"{SchemeName} realm=\"mcp\"";
        return Task.CompletedTask;
    }
}
