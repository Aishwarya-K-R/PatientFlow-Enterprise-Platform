using System.Text.Json;
using Confluent.Kafka;
using PatientFlow.Billing.Services;

namespace PatientFlow.Billing.Kafka;

public class BillingKafkaConsumer(
    IConfiguration config, 
    IServiceProvider serviceProvider,
    ILogger<BillingKafkaConsumer> logger) : BackgroundService
{
    private readonly IConfiguration _config = config;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly ILogger<BillingKafkaConsumer> _logger = logger;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = _config["Kafka:BootstrapServers"],
            GroupId = _config["Kafka:GroupId"],
            AutoOffsetReset = AutoOffsetReset.Earliest
        };

        return Task.Run(async () =>
        {
            using var consumer = new ConsumerBuilder<string, string>(consumerConfig).Build();
            consumer.Subscribe(_config["Kafka:PatientCreatedTopic"]);

            _logger.LogInformation("Kafka consumer started, subscribed to {Topic}", 
                _config["Kafka:PatientCreatedTopic"]);

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    var result = consumer.Consume(stoppingToken);
                    _logger.LogInformation("Received Patient Event: {Message}", result.Message.Value);

                    var patientEvent = JsonSerializer.Deserialize<PatientCreatedEvent>(result.Message.Value);

                    if (patientEvent != null)
                    {
                        using var scope = _serviceProvider.CreateScope();
                        var billingService = scope.ServiceProvider.GetRequiredService<BillingAccountService>();
                        await billingService.CreateAccountAsync(patientEvent.PatientId);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Kafka consumer stopping");
                consumer.Close();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Kafka consumer");
            }
        }, stoppingToken);
    }
}

public class PatientCreatedEvent
{
    public int PatientId { get; set; }
}
