using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PatientFlow.Patient.Data;
using PatientFlow.Common.Kafka;

namespace PatientFlow.Patient.Services;

public class OutboxPublisherService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<OutboxPublisherService> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(10);

    public OutboxPublisherService(
        IServiceProvider serviceProvider,
        ILogger<OutboxPublisherService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OutboxPublisher service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOutboxMessagesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing outbox messages");
            }

            await Task.Delay(_interval, stoppingToken);
        }

        _logger.LogInformation("OutboxPublisher service stopped");
    }

    private async Task ProcessOutboxMessagesAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PatientDbContext>();
        var kafkaProducer = scope.ServiceProvider.GetRequiredService<KafkaProducer>();

        var unpublishedMessages = await context.OutboxMessages
            .Where(m => !m.IsPublished && m.RetryCount < 5)
            .OrderBy(m => m.CreatedAt)
            .Take(100)
            .ToListAsync(stoppingToken);

        if (unpublishedMessages.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Processing {Count} outbox messages", unpublishedMessages.Count);

        foreach (var message in unpublishedMessages)
        {
            try
            {
                var payload = JsonSerializer.Deserialize<object>(message.Payload);
                await kafkaProducer.PublishAsync(message.Topic, payload!);

                message.IsPublished = true;
                message.PublishedAt = DateTime.UtcNow;
                message.ErrorMessage = null;

                _logger.LogInformation("Published outbox message {Id} to topic {Topic}", 
                    message.Id, message.Topic);
            }
            catch (Exception ex)
            {
                message.RetryCount++;
                message.ErrorMessage = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;

                _logger.LogWarning(ex, "Failed to publish outbox message {Id} (retry {RetryCount})", 
                    message.Id, message.RetryCount);
            }
        }

        await context.SaveChangesAsync(stoppingToken);
    }
}
