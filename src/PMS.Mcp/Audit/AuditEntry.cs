namespace PatientFlow.Mcp.Audit;

/// <summary>
/// Structured audit record for a single MCP tool or resource invocation.
///
/// This is the HIPAA-style access trail: who asked, what they asked for,
/// how much data came back, when, and from where. Serilog turns each field
/// into a queryable property in Loki so operators can filter by agent,
/// tool, or time range without regex-parsing log messages.
///
/// PHI rule: <see cref="InputSummary"/> must be a redacted, human-readable
/// hint (e.g. "query='diabetic' limit=10"). Never dump raw request bodies
/// or full result payloads here — that would move PHI into the audit log
/// and defeat the redaction done at the tool layer.
/// </summary>
public sealed record AuditEntry(
    string AgentName,
    string ToolName,
    string InputSummary,
    int ResultCount,
    long DurationMs,
    DateTime Timestamp,
    string ClientIp);
