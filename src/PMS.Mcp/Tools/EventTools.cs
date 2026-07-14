using System.ComponentModel;
using ModelContextProtocol.Server;
using PatientFlow.Mcp.Data;
using PatientFlow.Mcp.Models;

namespace PatientFlow.Mcp.Tools;

/// <summary>
/// Domain-event tools. Reads the patient service's outbox table — every
/// business event (PatientRegistered / Updated / Deleted) was written there
/// transactionally, so it's the authoritative activity log.
/// </summary>
[McpServerToolType]
public sealed class EventTools
{
    private readonly McpReadRepository _repo;
    private readonly IHttpContextAccessor _httpContext;
    private readonly ILogger<EventTools> _logger;

    public EventTools(
        McpReadRepository repo,
        IHttpContextAccessor httpContext,
        ILogger<EventTools> logger)
    {
        _repo = repo;
        _httpContext = httpContext;
        _logger = logger;
    }

    [McpServerTool(Name = "list_recent_events")]
    [Description("Return the most recent domain events emitted by the platform " +
                 "(patient registrations, updates, deletions). Use for activity questions " +
                 "like 'how many patients registered this week' or 'what changed recently'. " +
                 "Returns a topic, timestamp, and short human-readable payload summary — " +
                 "not the raw JSON payload.")]
    public async Task<IReadOnlyList<RecentEventSummary>> ListRecentEventsAsync(
        [Description("Maximum events to return. Default 20, capped at 100.")]
        int? limit = null,
        CancellationToken ct = default)
    {
        var take = Math.Clamp(limit ?? 20, 1, 100);
        var events = await _repo.GetRecentEventsAsync(take, ct);

        _logger.LogInformation(
            "MCP tool list_recent_events called by {Agent}: limit={Limit} results={Count}",
            _httpContext.HttpContext?.User?.Identity?.Name ?? "unknown", take, events.Count);

        // Reproject to the tool-facing shape: never surface raw payload JSON to
        // the LLM (it may contain PHI). PayloadPreview from the repository is
        // already truncated; the summary layer restates that contract for the
        // client and drops the full Payload field entirely.
        return events
            .Select(e => new RecentEventSummary(
                e.Topic,
                e.OccurredAt,
                e.IsPublished,
                e.PayloadPreview))
            .ToList();
    }
}

public sealed record RecentEventSummary(
    string Topic,
    DateTime OccurredAt,
    bool IsPublished,
    string PayloadSummary);
