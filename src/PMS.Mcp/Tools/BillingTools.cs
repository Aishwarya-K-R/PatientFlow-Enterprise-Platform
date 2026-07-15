using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using PatientFlow.Mcp.Audit;
using PatientFlow.Mcp.Data;
using PatientFlow.Mcp.Models;

namespace PatientFlow.Mcp.Tools;

[McpServerToolType]
public sealed class BillingTools
{
    private readonly McpReadRepository _repo;
    private readonly IHttpContextAccessor _httpContext;
    private readonly AuditLogger _audit;
    private readonly ILogger<BillingTools> _logger;

    public BillingTools(
        McpReadRepository repo,
        IHttpContextAccessor httpContext,
        AuditLogger audit,
        ILogger<BillingTools> logger)
    {
        _repo = repo;
        _httpContext = httpContext;
        _audit = audit;
        _logger = logger;
    }

    [McpServerTool(Name = "get_billing")]
    [Description("Return the billing account (AccountId, Status) linked to a given patient. " +
                 "Use for questions like 'is patient 42 active in billing?'.")]
    public async Task<GetBillingResult> GetBillingAsync(
        [Description("Patient ID whose billing account should be returned.")] int patientId,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var billing = await _repo.GetBillingForPatientAsync(patientId, ct);
        sw.Stop();

        _audit.Write(new AuditEntry(
            AgentName: _httpContext.HttpContext?.User?.Identity?.Name ?? "unknown",
            ToolName: "get_billing",
            InputSummary: $"patient_id={patientId}",
            ResultCount: billing is null ? 0 : 1,
            DurationMs: sw.ElapsedMilliseconds,
            Timestamp: DateTime.UtcNow,
            ClientIp: _httpContext.HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "unknown"));

        return billing is null
            ? new GetBillingResult(null, $"No billing account found for patient {patientId}.")
            : new GetBillingResult(billing, null);
    }
}

public sealed record GetBillingResult(BillingSummary? Billing, string? Error);
