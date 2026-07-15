using Serilog;
using Serilog.Context;

namespace PatientFlow.Mcp.Audit;

/// <summary>
/// Writes <see cref="AuditEntry"/> records through a dedicated Serilog
/// logger tagged with SourceContext "MCP.Audit".
///
/// Why a separate logger (vs. an <see cref="ILogger{T}"/> injection):
///  - Operators can filter audit entries from operational logs with a single
///    Loki query: {app="mcp-service"} | json | sourceContext="MCP.Audit".
///  - Tool code doesn't have to remember to include SourceContext on every
///    call site; the logger baked into this service always emits it.
///  - Later we can route audit sinks separately (e.g. long-retention S3
///    bucket for compliance) without touching call sites.
///
/// The message template uses named properties so Serilog captures each
/// field as a structured JSON property, not just interpolated text.
/// </summary>
public sealed class AuditLogger
{
    private readonly Serilog.ILogger _logger;

    public AuditLogger()
    {
        _logger = Log.ForContext("SourceContext", "MCP.Audit");
    }

    public void Write(AuditEntry entry)
    {
        // LogContext.PushProperty makes each field a first-class structured
        // property on the log event, so Loki/Grafana can filter on them
        // without parsing the message text.
        using (LogContext.PushProperty("agentName", entry.AgentName))
        using (LogContext.PushProperty("toolName", entry.ToolName))
        using (LogContext.PushProperty("inputSummary", entry.InputSummary))
        using (LogContext.PushProperty("resultCount", entry.ResultCount))
        using (LogContext.PushProperty("durationMs", entry.DurationMs))
        using (LogContext.PushProperty("clientIp", entry.ClientIp))
        {
            _logger.Information(
                "MCP audit: {ToolName} by {AgentName} -> {ResultCount} result(s) in {DurationMs}ms",
                entry.ToolName, entry.AgentName, entry.ResultCount, entry.DurationMs);
        }
    }
}
