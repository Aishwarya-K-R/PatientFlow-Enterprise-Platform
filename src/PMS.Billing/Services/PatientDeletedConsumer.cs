using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PatientFlow.Billing.Data;
using PatientFlow.Common.Kafka;
using PatientFlow.Contracts.Events;

namespace PatientFlow.Billing.Services;

/// <summary>
/// Consumes the patient-deleted topic and removes the corresponding
/// BillingAccount row so the Billing DB stays consistent with the
/// Patient service (prevents orphaned billing accounts).
///
/// Uses its own consumer group ("billing-service-deletes-group") so it
/// can be scaled or paused independently from other billing consumers.
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
            "billing-service-deletes-group",
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

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();

            var deleted = await db.BillingAccounts
                .Where(b => b.PatientId == evt.PatientId)
                .ExecuteDeleteAsync(cancellationToken);

            if (deleted > 0)
            {
                _logger.LogInformation(
                    "Deleted {Count} BillingAccount row(s) for PatientId {PatientId} (event {EventId})",
                    deleted, evt.PatientId, envelope.EventId);
            }
            else
            {
                _logger.LogInformation(
                    "No BillingAccount found for PatientId {PatientId} (event {EventId}); idempotent no-op",
                    evt.PatientId, envelope.EventId);
            }

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
