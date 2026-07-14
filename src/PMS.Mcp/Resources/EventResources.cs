using System.Text.Json;
using ModelContextProtocol.Server;
using PatientFlow.Mcp.Data;

namespace PatientFlow.Mcp.Resources;

[McpServerResourceType]
public sealed class EventResources
{
    private readonly McpReadRepository _repo;

    public EventResources(McpReadRepository repo) => _repo = repo;

    [McpServerResource(
        UriTemplate = "patientflow://events/recent",
        Name = "recent_events",
        MimeType = "application/json")]
    [System.ComponentModel.Description(
        "Last 20 domain events (patient registrations, updates, deletions). " +
        "Same PHI-safe shape as the list_recent_events tool: topic, timestamp, and " +
        "short payload preview only — raw JSON payloads are not exposed.")]
    public async Task<string> GetRecentEventsAsync(CancellationToken ct = default)
    {
        var events = await _repo.GetRecentEventsAsync(take: 20, ct);

        // Same projection rule as EventTools: never expose the full Payload
        // JSON — only the truncated preview crosses the MCP boundary.
        var payload = events.Select(e => new
        {
            e.Topic,
            occurredAt = e.OccurredAt,
            e.IsPublished,
            payloadPreview = e.PayloadPreview
        });

        return JsonSerializer.Serialize(payload);
    }
}
