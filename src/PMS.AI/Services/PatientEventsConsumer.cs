using System.Text.Json;
using PatientFlow.Common.Kafka;
using PatientFlow.Contracts.Events;

namespace PatientFlow.AI.Services;

/// <summary>
/// Consumes the patient-created topic, dedupes by EventId, and delegates the
/// actual Redis + pgvector work to PatientEventHandler so the logic is shared
/// with the other patient consumers (updated / deleted / retry).
///
/// Kept as its own class (rather than a single omnibus consumer) because
/// KafkaConsumerBase is intentionally single-topic, and each consumer needs
/// its own consumer group so Kafka tracks offsets independently.
/// </summary>
public class PatientEventsConsumer : KafkaConsumerBase<PatientCreatedEvent>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PatientEventsConsumer> _logger;

    public PatientEventsConsumer(
        IConfiguration config,
        IServiceProvider serviceProvider,
        ILogger<PatientEventsConsumer> logger)
        : base(
            config,
            serviceProvider,
            logger,
            config["Kafka:PatientCreatedTopic"]!,
            "ai-service-group",
            maxRetryAttempts: 3)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task<bool> ProcessMessageAsync(
        EventEnvelope envelope,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Processing event {EventType} with ID {EventId}",
                envelope.EventType, envelope.EventId);

            using var scope = _serviceProvider.CreateScope();
            var redis = scope.ServiceProvider.GetRequiredService<RedisService>();
            var handler = scope.ServiceProvider.GetRequiredService<PatientEventHandler>();

            // Idempotency: return true (commit) if we've already handled this EventId.
            var cacheKey = $"processed_event:{envelope.EventId}";
            if (await redis.GetPatientContextAsync(cacheKey) != null)
            {
                _logger.LogInformation("Event {EventId} already processed, skipping", envelope.EventId);
                return true;
            }

            if (envelope.EventType != EventTypes.PatientCreated)
            {
                // PatientUpdated / PatientDeleted flow through their own dedicated
                // consumers on their own topics - seeing them here would mean a
                // publisher misconfiguration. Skip cleanly instead of retrying.
                _logger.LogWarning("Unexpected event type {EventType} on patient-created topic; skipping",
                    envelope.EventType);
                return true;
            }

            var evt = JsonSerializer.Deserialize<PatientCreatedEvent>(envelope.Payload.ToString()!);
            if (evt == null)
            {
                _logger.LogWarning("Failed to deserialize PatientCreatedEvent for event {EventId}",
                    envelope.EventId);
                return true;
            }

            await handler.HandleChangedAsync(evt.PatientId, "created", cancellationToken);

            await redis.SetPatientContextAsync(cacheKey, "processed", TimeSpan.FromDays(7));
            _logger.LogInformation("Successfully processed event {EventId}", envelope.EventId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing event {EventId}: {Message}",
                envelope.EventId, ex.Message);
            return false;
        }
    }
}
