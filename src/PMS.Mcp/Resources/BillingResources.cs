using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using PatientFlow.Mcp.Data;

namespace PatientFlow.Mcp.Resources;

[McpServerResourceType]
public sealed class BillingResources
{
    private readonly IDbContextFactory<McpBillingDbContext> _billingFactory;

    public BillingResources(IDbContextFactory<McpBillingDbContext> billingFactory)
    {
        _billingFactory = billingFactory;
    }

    [McpServerResource(
        UriTemplate = "patientflow://billing/summary",
        Name = "billing_summary",
        MimeType = "application/json")]
    [System.ComponentModel.Description(
        "Aggregate billing account stats: total accounts and a breakdown by status " +
        "(e.g. ACTIVE / INACTIVE counts).")]
    public async Task<string> GetBillingSummaryAsync(CancellationToken ct = default)
    {
        // Direct DbContext hit here (rather than through McpReadRepository)
        // because "group by status" is a one-off shape that doesn't belong on
        // the general read facade. Same read-only guarantees still apply —
        // context enforces NoTracking + throws on SaveChanges.
        await using var db = await _billingFactory.CreateDbContextAsync(ct);
        var groups = await db.BillingAccounts
            .GroupBy(b => b.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var payload = new
        {
            total = groups.Sum(g => g.Count),
            byStatus = groups.ToDictionary(g => g.Status, g => g.Count),
            asOf = DateTime.UtcNow
        };

        return JsonSerializer.Serialize(payload);
    }
}
