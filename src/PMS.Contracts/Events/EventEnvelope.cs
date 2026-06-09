namespace PatientFlow.Contracts.Events;

/// <summary>
/// Standard envelope for all events published to Kafka.
/// Provides metadata for idempotency, versioning, and audit trail.
/// </summary>
public class EventEnvelope
{
    /// <summary>
    /// Unique identifier for this event instance.
    /// Used for deduplication - consumers should track processed EventIds.
    /// </summary>
    public string EventId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Type of event (e.g., "PatientCreated", "BillingCreated").
    /// Used for consumer routing and deserialization.
    /// </summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// Schema version for the payload (e.g., "v1", "v2").
    /// Allows schema evolution and backward compatibility.
    /// </summary>
    public string Version { get; set; } = "v1";

    /// <summary>
    /// When the event occurred (UTC).
    /// Used for ordering, audit trail, and event replay.
    /// </summary>
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The actual event data (domain-specific payload).
    /// Serialized as JSON object.
    /// </summary>
    public object Payload { get; set; } = new { };

    /// <summary>
    /// Optional correlation ID for tracing related events across services.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Service that produced this event.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Additional metadata (e.g., RetryCount, LastRetryAt).
    /// Used for retry logic and debugging.
    /// </summary>
    public Dictionary<string, string>? Metadata { get; set; }

    /// <summary>
    /// Factory: build a new envelope with auto-generated EventId, current UTC timestamp,
    /// and default Version. Use this instead of inline object initializers to keep the
    /// envelope construction consistent across services.
    /// </summary>
    public static EventEnvelope Create(string eventType, string source, object payload, string? correlationId = null)
    {
        return new EventEnvelope
        {
            EventId = Guid.NewGuid().ToString(),
            EventType = eventType,
            Source = source,
            Payload = payload,
            OccurredAt = DateTime.UtcNow,
            Version = "v1",
            CorrelationId = correlationId
        };
    }
}

/// <summary>
/// Canonical event type names. Use these constants instead of magic strings.
/// </summary>
public static class EventTypes
{
    public const string PatientCreated = "PatientCreated";
    public const string PatientUpdated = "PatientUpdated";
    public const string PatientDeleted = "PatientDeleted";
    public const string BillingCreated = "BillingCreated";
}

/// <summary>
/// Canonical event source names (which service emitted the event).
/// </summary>
public static class EventSources
{
    public const string PatientService = "PatientService";
    public const string BillingService = "BillingService";
}
