using System.Security.Claims;

namespace PatientFlow.Patient.Middleware;

/// <summary>
/// Authenticates trusted internal callers via a shared API key header.
/// Runs BEFORE UseAuthorization so [Authorize(Roles="ADMIN")] sees a valid
/// principal when the X-Internal-Api-Key header matches the configured key.
///
/// Intended for service-to-service calls (e.g. AI cache warmup hitting
/// /patients/all). User-facing requests are unaffected: no header means
/// fall through to normal JWT validation.
/// </summary>
public sealed class InternalApiKeyMiddleware
{
    public const string HeaderName = "X-Internal-Api-Key";
    public const string ConfigKey = "InternalApiKey";

    private readonly RequestDelegate _next;
    private readonly string? _configuredKey;

    public InternalApiKeyMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        _configuredKey = configuration[ConfigKey];
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (!string.IsNullOrEmpty(_configuredKey) &&
            context.Request.Headers.TryGetValue(HeaderName, out var provided) &&
            provided == _configuredKey)
        {
            var identity = new ClaimsIdentity(
                authenticationType: "InternalApiKey",
                nameType: ClaimTypes.Name,
                roleType: ClaimTypes.Role);

            identity.AddClaim(new Claim(ClaimTypes.Name, "internal-service"));
            identity.AddClaim(new Claim(ClaimTypes.Role, "ADMIN"));

            context.User = new ClaimsPrincipal(identity);
        }

        return _next(context);
    }
}
