using System.Diagnostics;

namespace PatientFlow.Mcp.Audit;

/// <summary>
/// Request-level audit middleware for the MCP transport endpoints.
///
/// The per-tool call sites emit a rich <see cref="AuditEntry"/> that includes
/// tool name, input summary, and result count. This middleware is the
/// belt-and-braces layer: it records that a request happened at all,
/// with agent, path, status, timing, and client IP — so even if a tool
/// handler crashes before it can write its own audit line, we still know
/// somebody was authenticated and tried to call the server.
///
/// Deliberately scoped to MCP-protocol paths (root POST + /sse) so that
/// /health and /metrics scrapes don't spam the audit log.
/// </summary>
public sealed class McpAuditMiddleware
{
    private readonly RequestDelegate _next;
    private readonly AuditLogger _audit;

    public McpAuditMiddleware(RequestDelegate next, AuditLogger audit)
    {
        _next = next;
        _audit = audit;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!ShouldAudit(context))
        {
            await _next(context);
            return;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            await _next(context);
        }
        finally
        {
            sw.Stop();
            _audit.Write(new AuditEntry(
                AgentName: context.User?.Identity?.Name ?? "anonymous",
                ToolName: $"HTTP {context.Request.Method} {context.Request.Path}",
                InputSummary: $"status={context.Response.StatusCode}",
                ResultCount: 0,
                DurationMs: sw.ElapsedMilliseconds,
                Timestamp: DateTime.UtcNow,
                ClientIp: context.Connection.RemoteIpAddress?.ToString() ?? "unknown"));
        }
    }

    private static bool ShouldAudit(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        // Skip infra endpoints — they're scraped every few seconds by
        // Prometheus and health probes; auditing them would drown the trail.
        if (path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)) return false;
        if (path.StartsWith("/metrics", StringComparison.OrdinalIgnoreCase)) return false;
        if (path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}
