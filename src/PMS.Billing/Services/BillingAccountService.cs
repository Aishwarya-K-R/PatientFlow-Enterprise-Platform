using System.Text.Json;
using PatientFlow.Billing.Data;
using PatientFlow.Billing.Models;
using PatientFlow.Common.Kafka;

namespace PatientFlow.Billing.Services;

public class BillingAccountService(
    BillingDbContext context, 
    KafkaProducer kafkaProducer, 
    IConfiguration config,
    ILogger<BillingAccountService> logger)
{
    private readonly BillingDbContext _context = context;
    private readonly KafkaProducer _kafkaProducer = kafkaProducer;
    private readonly IConfiguration _config = config;
    private readonly ILogger<BillingAccountService> _logger = logger;

    public async Task<BillingAccount> CreateAccountAsync(int patientId)
    {
        _logger.LogInformation("Creating billing account for PatientId {PatientId}", patientId);

        var billing = new BillingAccount
        {
            PatientId = patientId,
            AccountId = Guid.NewGuid().ToString(),
            Status = "ACTIVE"
        };

        // Create outbox message for Kafka event
        var outboxMessage = new OutboxMessage
        {
            Topic = _config["Kafka:BillingCreatedTopic"]!,
            Payload = JsonSerializer.Serialize(new { PatientId = patientId, AccountId = billing.AccountId }),
            CreatedAt = DateTime.UtcNow
        };

        _context.BillingAccounts.Add(billing);
        _context.OutboxMessages.Add(outboxMessage);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Billing account {AccountId} created for PatientId {PatientId}", 
            billing.AccountId, patientId);

        return billing;
    }
}
