using System.Text.Json;
using PatientFlow.Common.Kafka;
using PatientFlow.Contracts.Events;

namespace PatientFlow.AI.Services;

/// <summary>
/// Consumes retry topic messages with exponential backoff delay.
/// Re-processes failed messages after a delay before sending to main consumer.
/// </summary>
public class PatientEventsRetryConsumer : KafkaConsumerBase<PatientCreatedEvent>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PatientEventsRetryConsumer> _logger;

    public PatientEventsRetryConsumer(
        IConfiguration config,
        IServiceProvider serviceProvider,
        ILogger<PatientEventsRetryConsumer> logger)
        : base(
            config,
            serviceProvider,
            logger,
            $"{config["Kafka:PatientCreatedTopic"]}-retry",
            "ai-service-retry-group",
            maxRetryAttempts: 3) // Same max as main consumer
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
            // Get retry count from metadata
            var retryCount = envelope.Metadata?.ContainsKey("RetryCount") == true
                ? int.Parse(envelope.Metadata["RetryCount"])
                : 1;

            // Exponential backoff: 2^retryCount seconds (2s, 4s, 8s)
            var delaySeconds = Math.Pow(2, retryCount);
            _logger.LogInformation("Retry consumer: Waiting {Delay}s before retry attempt {Attempt} for event {EventId}",
                delaySeconds, retryCount, envelope.EventId);

            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);

            // Now process with same logic as main consumer
            using var scope = _serviceProvider.CreateScope();
            var redis = scope.ServiceProvider.GetRequiredService<RedisService>();

            // Check for duplicates
            var cacheKey = $"processed_event:{envelope.EventId}";
            var alreadyProcessed = await redis.GetPatientContextAsync(cacheKey);

            if (alreadyProcessed != null)
            {
                _logger.LogInformation("Retry consumer: Event {EventId} already processed during retry delay",
                    envelope.EventId);
                return true;
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
                    _logger.LogWarning("Retry consumer: Unknown event type: {EventType}", envelope.EventType);
                    return true;
            }

            // Mark as processed
            await redis.SetPatientContextAsync(cacheKey, "processed", TimeSpan.FromDays(7));

            _logger.LogInformation("Retry consumer: Successfully processed event {EventId} on retry attempt {Attempt}",
                envelope.EventId, retryCount);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retry consumer: Error processing event {EventId}: {Message}",
                envelope.EventId, ex.Message);
            return false; // Will go to DLQ after max retries in base class
        }
    }

    private async Task HandlePatientCreatedAsync(RedisService redis, PatientCreatedEvent evt)
    {
        var context = $"Patient {evt.PatientId} ({evt.Name}) created with email {evt.Email}";
        await redis.SetPatientContextAsync($"patient:{evt.PatientId}", context, TimeSpan.FromHours(24));
        _logger.LogInformation("Retry consumer: Updated context for patient {PatientId}", evt.PatientId);
    }

    private async Task HandlePatientUpdatedAsync(RedisService redis, PatientUpdatedEvent evt)
    {
        var context = $"Patient {evt.PatientId} ({evt.Name}) updated, email: {evt.Email}";
        await redis.SetPatientContextAsync($"patient:{evt.PatientId}", context, TimeSpan.FromHours(24));
        _logger.LogInformation("Retry consumer: Updated context for patient {PatientId}", evt.PatientId);
    }

    private async Task HandlePatientDeletedAsync(RedisService redis, PatientDeletedEvent evt)
    {
        await redis.DeletePatientContextAsync($"patient:{evt.PatientId}");
        _logger.LogInformation("Retry consumer: Removed context for deleted patient {PatientId}", evt.PatientId);
    }
}
