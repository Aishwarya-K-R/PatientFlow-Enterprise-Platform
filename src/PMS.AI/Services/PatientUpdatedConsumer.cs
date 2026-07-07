using System.Text.Json;
using PatientFlow.Common.Kafka;
using PatientFlow.Contracts.Events;

namespace PatientFlow.AI.Services;

/// <summary>
/// Consumes the patient-updated topic and delegates to PatientEventHandler
/// so Redis context and pgvector embedding stay in sync on every edit.
///
/// Own consumer group ("ai-service-updates-group") so Kafka tracks offsets
/// for this topic independently from patient-created / patient-deleted.
/// </summary>
public class PatientUpdatedConsumer : KafkaConsumerBase<PatientUpdatedEvent>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PatientUpdatedConsumer> _logger;

    public PatientUpdatedConsumer(
        IConfiguration config,
        IServiceProvider serviceProvider,
        ILogger<PatientUpdatedConsumer> logger)
        : base(
            config,
            serviceProvider,
            logger,
            config["Kafka:PatientUpdatedTopic"]!,
            "ai-service-updates-group",
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

            // Idempotency: same "processed_event:{EventId}" key namespace as the
            // other consumers so re-delivery across any patient topic is a no-op.
            var cacheKey = $"processed_event:{envelope.EventId}";
            if (await redis.GetPatientContextAsync(cacheKey) != null)
            {
                _logger.LogInformation("Event {EventId} already processed, skipping", envelope.EventId);
                return true;
            }

            if (envelope.EventType != EventTypes.PatientUpdated)
            {
                _logger.LogWarning("Unexpected event type {EventType} on patient-updated topic; skipping",
                    envelope.EventType);
                return true;
            }

            var evt = JsonSerializer.Deserialize<PatientUpdatedEvent>(envelope.Payload.ToString()!);
            if (evt == null)
            {
                _logger.LogWarning("Failed to deserialize PatientUpdatedEvent for event {EventId}",
                    envelope.EventId);
                return true;
            }

            await handler.HandleChangedAsync(evt.PatientId, "updated", cancellationToken);

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
