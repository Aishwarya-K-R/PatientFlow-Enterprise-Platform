namespace PatientFlow.Mcp.Models;

/// <summary>
/// Domain projection of an outbox row. The full <c>Payload</c> is kept
/// (tools decide how much of it to surface); <c>PayloadPreview</c> is a
/// truncated form convenient for list-style responses.
/// </summary>
public sealed record RecentEvent(
    long Id,
    string Topic,
    DateTime OccurredAt,
    bool IsPublished,
    string PayloadPreview,
    string Payload);
