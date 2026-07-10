using System.Text.Json;
using PatientFlow.Common.Kafka;
using PatientFlow.Contracts.Events;

namespace PatientFlow.AI.Services;

/// <summary>
/// Consumes the patient-created-retry topic with an exponential backoff before
/// re-processing. Delegates to PatientEventHandler for the actual work so the
/// retry path never diverges from the main path (previously it wrote a much
/// simpler "context = name + email" string that would clobber the real
/// pseudonymised context).
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
            var retryCount = envelope.Metadata?.ContainsKey("RetryCount") == true
                ? int.Parse(envelope.Metadata["RetryCount"])
                : 1;

            // Exponential backoff: 2s, 4s, 8s ...
            var delaySeconds = Math.Pow(2, retryCount);
            _logger.LogInformation("Retry consumer: Waiting {Delay}s before retry attempt {Attempt} for event {EventId}",
                delaySeconds, retryCount, envelope.EventId);
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);

            using var scope = _serviceProvider.CreateScope();
            var redis = scope.ServiceProvider.GetRequiredService<RedisService>();
            var handler = scope.ServiceProvider.GetRequiredService<PatientEventHandler>();

            var cacheKey = $"processed_event:{envelope.EventId}";
            if (await redis.GetPatientContextAsync(cacheKey) != null)
            {
                _logger.LogInformation("Retry consumer: Event {EventId} already processed during retry delay",
                    envelope.EventId);
                return true;
            }

            // The retry topic only carries PatientCreated events (produced by the
            // main patient-created consumer when it fails). Any other type here
            // is a routing bug - skip cleanly rather than looping.
            if (envelope.EventType != EventTypes.PatientCreated)
            {
                _logger.LogWarning("Retry consumer: Unexpected event type {EventType}; skipping",
                    envelope.EventType);
                return true;
            }

            var evt = JsonSerializer.Deserialize<PatientCreatedEvent>(envelope.Payload.ToString()!);
            if (evt == null)
            {
                _logger.LogWarning("Retry consumer: Failed to deserialize PatientCreatedEvent for event {EventId}",
                    envelope.EventId);
                return true;
            }

            await handler.HandleChangedAsync(evt.PatientId, "created (retry)", cancellationToken);

            await redis.SetPatientContextAsync(cacheKey, "processed", TimeSpan.FromDays(7));
            _logger.LogInformation("Retry consumer: Successfully processed event {EventId} on retry attempt {Attempt}",
                envelope.EventId, retryCount);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retry consumer: Error processing event {EventId}: {Message}",
                envelope.EventId, ex.Message);
            return false;
        }
    }
}
