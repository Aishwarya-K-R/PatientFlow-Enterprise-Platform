using System.Text.Json;
using PatientFlow.Common.Kafka;
using PatientFlow.Contracts.Events;

namespace PatientFlow.AI.Services;

/// <summary>
/// Consumes patient events and updates Redis context for AI queries.
/// Uses manual offset commit to prevent message loss.
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
            logger,
            config["Kafka:PatientCreatedTopic"]!,
            "ai-service-group")
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

            // Check for duplicates using EventId
            using var scope = _serviceProvider.CreateScope();
            var redis = scope.ServiceProvider.GetRequiredService<RedisService>();

            // Simple deduplication: Check if we've seen this EventId
            var cacheKey = $"processed_event:{envelope.EventId}";
            var alreadyProcessed = await redis.GetPatientContextAsync(cacheKey);

            if (alreadyProcessed != null)
            {
                _logger.LogInformation("Event {EventId} already processed, skipping", envelope.EventId);
                return true; // Return true to commit offset (idempotent processing)
            }

            // Process based on event type
            switch (envelope.EventType)
            {
                case "PatientCreated":
                    var patientCreated = JsonSerializer.Deserialize<PatientCreatedEvent>(
                        envelope.Payload.ToString()!);
                    if (patientCreated != null)
                    {
                        await HandlePatientCreatedAsync(redis, patientCreated);
                    }
                    break;

                case "PatientUpdated":
                    var patientUpdated = JsonSerializer.Deserialize<PatientUpdatedEvent>(
                        envelope.Payload.ToString()!);
                    if (patientUpdated != null)
                    {
                        await HandlePatientUpdatedAsync(redis, patientUpdated);
                    }
                    break;

                case "PatientDeleted":
                    var patientDeleted = JsonSerializer.Deserialize<PatientDeletedEvent>(
                        envelope.Payload.ToString()!);
                    if (patientDeleted != null)
                    {
                        await HandlePatientDeletedAsync(redis, patientDeleted);
                    }
                    break;

                default:
                    _logger.LogWarning("Unknown event type: {EventType}", envelope.EventType);
                    return true; // Skip unknown events
            }

            // Mark event as processed (with 7 day expiration)
            await redis.SetPatientContextAsync(cacheKey, "processed", TimeSpan.FromDays(7));

            _logger.LogInformation("Successfully processed event {EventId}", envelope.EventId);
            return true; // Success - commit offset
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing event {EventId}: {Message}",
                envelope.EventId, ex.Message);
            return false; // Failure - don't commit, will retry
        }
    }

    private async Task HandlePatientCreatedAsync(RedisService redis, PatientCreatedEvent evt)
    {
        var context = $"Patient {evt.PatientId} ({evt.Name}) created with email {evt.Email}";
        await redis.SetPatientContextAsync($"patient:{evt.PatientId}", context, TimeSpan.FromHours(24));
        _logger.LogInformation("Updated context for patient {PatientId}", evt.PatientId);
    }

    private async Task HandlePatientUpdatedAsync(RedisService redis, PatientUpdatedEvent evt)
    {
        var context = $"Patient {evt.PatientId} ({evt.Name}) updated, email: {evt.Email}";
        await redis.SetPatientContextAsync($"patient:{evt.PatientId}", context, TimeSpan.FromHours(24));
        _logger.LogInformation("Updated context for patient {PatientId}", evt.PatientId);
    }

    private async Task HandlePatientDeletedAsync(RedisService redis, PatientDeletedEvent evt)
    {
        await redis.DeletePatientContextAsync($"patient:{evt.PatientId}");
        _logger.LogInformation("Removed context for deleted patient {PatientId}", evt.PatientId);
    }
}
