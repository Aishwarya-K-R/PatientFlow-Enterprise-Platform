using System.ComponentModel;
using ModelContextProtocol.Server;
using PatientFlow.Mcp.Data;
using PatientFlow.Mcp.Models;

namespace PatientFlow.Mcp.Tools;

[McpServerToolType]
public sealed class BillingTools
{
    private readonly McpReadRepository _repo;
    private readonly IHttpContextAccessor _httpContext;
    private readonly ILogger<BillingTools> _logger;

    public BillingTools(
        McpReadRepository repo,
        IHttpContextAccessor httpContext,
        ILogger<BillingTools> logger)
    {
        _repo = repo;
        _httpContext = httpContext;
        _logger = logger;
    }

    [McpServerTool(Name = "get_billing")]
    [Description("Return the billing account (AccountId, Status) linked to a given patient. " +
                 "Use for questions like 'is patient 42 active in billing?'.")]
    public async Task<GetBillingResult> GetBillingAsync(
        [Description("Patient ID whose billing account should be returned.")] int patientId,
        CancellationToken ct = default)
    {
        var billing = await _repo.GetBillingForPatientAsync(patientId, ct);

        _logger.LogInformation(
            "MCP tool get_billing called by {Agent}: patient_id={PatientId} found={Found}",
            _httpContext.HttpContext?.User?.Identity?.Name ?? "unknown",
            patientId, billing is not null);

        return billing is null
            ? new GetBillingResult(null, $"No billing account found for patient {patientId}.")
            : new GetBillingResult(billing, null);
    }
}

public sealed record GetBillingResult(BillingSummary? Billing, string? Error);
