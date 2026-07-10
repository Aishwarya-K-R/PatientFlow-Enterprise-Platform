using System.Text.Json;
using PatientFlow.Common.Kafka;
using PatientFlow.Contracts.Events;

namespace PatientFlow.AI.Services;

/// <summary>
/// Consumes the billing-created topic and appends billing metadata to the
/// patient's Redis context so LLM prompts can answer questions like
/// "does patient X have an active billing account?".
///
/// The vector store is intentionally NOT touched here - billing changes
/// don't alter the clinical narrative (medical history) that the RAG
/// pipeline searches over, so re-embedding would be pure noise.
///
/// Own consumer group ("ai-service-billing-group").
/// </summary>
public class BillingCreatedConsumer : KafkaConsumerBase<BillingCreatedEvent>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<BillingCreatedConsumer> _logger;

    public BillingCreatedConsumer(
        IConfiguration config,
        IServiceProvider serviceProvider,
        ILogger<BillingCreatedConsumer> logger)
        : base(
            config,
            serviceProvider,
            logger,
            config["Kafka:BillingCreatedTopic"]!,
            "ai-service-billing-group",
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

            var cacheKey = $"processed_event:{envelope.EventId}";
            if (await redis.GetPatientContextAsync(cacheKey) != null)
            {
                _logger.LogInformation("Event {EventId} already processed, skipping", envelope.EventId);
                return true;
            }

            if (envelope.EventType != EventTypes.BillingCreated)
            {
                _logger.LogWarning("Unexpected event type {EventType} on billing-created topic; skipping",
                    envelope.EventType);
                return true;
            }

            var evt = JsonSerializer.Deserialize<BillingCreatedEvent>(envelope.Payload.ToString()!);
            if (evt == null)
            {
                _logger.LogWarning("Failed to deserialize BillingCreatedEvent for event {EventId}",
                    envelope.EventId);
                return true;
            }

            // Store the billing signal under its own key so it composes cleanly
            // with the primary patient-context-v2:{id} key produced by patient
            // events. The LLM prompt builder can look up both and merge them.
            var billingKey = $"patient-billing:{evt.PatientId}";
            var billingContext = $"Active billing account {evt.AccountId} created at {envelope.OccurredAt:o}";
            await redis.SetPatientContextAsync(billingKey, billingContext, TimeSpan.FromDays(30));

            _logger.LogInformation("Recorded billing account {AccountId} for patient {PatientId}",
                evt.AccountId, evt.PatientId);

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
