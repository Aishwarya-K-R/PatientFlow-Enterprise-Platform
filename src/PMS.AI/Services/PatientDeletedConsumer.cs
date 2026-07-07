using System.Text.Json;
using PatientFlow.Common.Kafka;
using PatientFlow.Contracts.Events;

namespace PatientFlow.AI.Services;

/// <summary>
/// Consumes the patient-deleted topic and clears the Redis context.
/// The pgvector row is cleaned up by the cascade FK on PatientEmbeddings
/// when the parent Patient row is deleted upstream in Patient service.
///
/// Own consumer group ("ai-service-deletes-group") so it can be scaled or
/// paused independently from the other patient consumers.
/// </summary>
public class PatientDeletedConsumer : KafkaConsumerBase<PatientDeletedEvent>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PatientDeletedConsumer> _logger;

    public PatientDeletedConsumer(
        IConfiguration config,
        IServiceProvider serviceProvider,
        ILogger<PatientDeletedConsumer> logger)
        : base(
            config,
            serviceProvider,
            logger,
            config["Kafka:PatientDeletedTopic"]!,
            "ai-service-deletes-group",
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

            var cacheKey = $"processed_event:{envelope.EventId}";
            if (await redis.GetPatientContextAsync(cacheKey) != null)
            {
                _logger.LogInformation("Event {EventId} already processed, skipping", envelope.EventId);
                return true;
            }

            if (envelope.EventType != EventTypes.PatientDeleted)
            {
                _logger.LogWarning("Unexpected event type {EventType} on patient-deleted topic; skipping",
                    envelope.EventType);
                return true;
            }

            var evt = JsonSerializer.Deserialize<PatientDeletedEvent>(envelope.Payload.ToString()!);
            if (evt == null)
            {
                _logger.LogWarning("Failed to deserialize PatientDeletedEvent for event {EventId}",
                    envelope.EventId);
                return true;
            }

            await handler.HandleDeletedAsync(evt.PatientId, cancellationToken);

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
