using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using PatientFlow.Mcp.Audit;
using PatientFlow.Mcp.Data;
using PatientFlow.Mcp.Models;

namespace PatientFlow.Mcp.Tools;

/// <summary>
/// MCP tools that expose patient reads.
///
/// A tool class is a plain DI-resolved type marked with
/// <see cref="McpServerToolTypeAttribute"/>; each public method decorated with
/// <see cref="McpServerToolAttribute"/> becomes a tool the LLM can invoke.
/// The [Description] attributes are what the model actually sees when
/// deciding whether to call the tool — treat them as prompt engineering,
/// not comments.
///
/// Tools stay thin: no queries or projections here, everything routes through
/// <see cref="McpReadRepository"/> so read-side logic lives in one place.
/// </summary>
[McpServerToolType]
public sealed class PatientTools
{
    private readonly McpReadRepository _repo;
    private readonly IHttpContextAccessor _httpContext;
    private readonly AuditLogger _audit;
    private readonly ILogger<PatientTools> _logger;

    public PatientTools(
        McpReadRepository repo,
        IHttpContextAccessor httpContext,
        AuditLogger audit,
        ILogger<PatientTools> logger)
    {
        _repo = repo;
        _httpContext = httpContext;
        _audit = audit;
        _logger = logger;
    }

    [McpServerTool(Name = "search_patients")]
    [Description("Search patients by name or email fragment. Returns a list of patient summaries " +
                 "(Id, Name, Email, RegisteredDate, Age). Use for questions like " +
                 "'find patients named smith' or 'who registered recently'.")]
    public async Task<IReadOnlyList<PatientSummary>> SearchPatientsAsync(
        [Description("Free-text query matched (case-insensitive) against patient name and email.")]
        string query,
        [Description("Maximum results to return. Default 10, capped at 50.")]
        int? limit = null,
        CancellationToken ct = default)
    {
        var take = Math.Clamp(limit ?? 10, 1, 50);
        var sw = Stopwatch.StartNew();
        var results = await _repo.SearchPatientsAsync(query, take, ct);
        sw.Stop();

        _audit.Write(new AuditEntry(
            AgentName: AgentName(),
            ToolName: "search_patients",
            InputSummary: $"query='{query}' limit={take}",
            ResultCount: results.Count,
            DurationMs: sw.ElapsedMilliseconds,
            Timestamp: DateTime.UtcNow,
            ClientIp: ClientIp()));

        return results;
    }

    [McpServerTool(Name = "get_patient")]
    [Description("Fetch a single patient by numeric ID together with their billing account. " +
                 "Returns null-valued fields on the response object if the record isn't found.")]
    public async Task<GetPatientResult> GetPatientAsync(
        [Description("Patient ID.")] int id,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var patient = await _repo.GetPatientAsync(id, ct);
        var billing = await _repo.GetBillingForPatientAsync(id, ct);
        sw.Stop();

        _audit.Write(new AuditEntry(
            AgentName: AgentName(),
            ToolName: "get_patient",
            InputSummary: $"id={id}",
            ResultCount: patient is null ? 0 : 1,
            DurationMs: sw.ElapsedMilliseconds,
            Timestamp: DateTime.UtcNow,
            ClientIp: ClientIp()));

        if (patient is null)
        {
            return new GetPatientResult(null, null, $"No patient found with id {id}.");
        }

        return new GetPatientResult(patient, billing, null);
    }

    private string AgentName() =>
        _httpContext.HttpContext?.User?.Identity?.Name ?? "unknown";

    private string ClientIp() =>
        _httpContext.HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

/// <summary>
/// Structured response for <c>get_patient</c>. Using a record with an optional
/// <c>Error</c> string means "not found" is a first-class result the model can
/// reason about, rather than an exception surfaced as an MCP protocol error.
/// </summary>
public sealed record GetPatientResult(
    PatientSummary? Patient,
    BillingSummary? Billing,
    string? Error);
