using Confluent.Kafka.Admin;
using Confluent.Kafka;

namespace PatientFlow.Gateway.Kafka;

public class KafkaTopicCreator(IConfiguration config, ILogger<KafkaTopicCreator> logger)
{
    private readonly IConfiguration _config = config;
    private readonly ILogger<KafkaTopicCreator> _logger = logger;

    public async Task CreateTopicsAsync()
    {
        var bootstrapServers = _config["Kafka:BootstrapServers"];

        var config = new AdminClientConfig
        {
            BootstrapServers = bootstrapServers
        };

        using var adminClient = new AdminClientBuilder(config).Build();

        try
        {
            await adminClient.CreateTopicsAsync(new[]
            {
                new TopicSpecification
                {
                    Name = _config["Kafka:PatientCreatedTopic"],
                    NumPartitions = 1,
                    ReplicationFactor = 1
                },
                new TopicSpecification
                {
                    Name = _config["Kafka:PatientUpdatedTopic"],
                    NumPartitions = 1,
                    ReplicationFactor = 1
                },
                new TopicSpecification
                {
                    Name = _config["Kafka:PatientDeletedTopic"],
                    NumPartitions = 1,
                    ReplicationFactor = 1
                },
                new TopicSpecification
                {
                    Name = _config["Kafka:BillingCreatedTopic"],
                    NumPartitions = 1,
                    ReplicationFactor = 1
                },
            });

            _logger.LogInformation("Kafka topics created successfully");
        }
        catch (CreateTopicsException ex)
        {
            if (ex.Results.All(r => r.Error.Code == ErrorCode.TopicAlreadyExists))
            {
                _logger.LogInformation("Kafka topics already exist");
            }
            else
            {
                _logger.LogError(ex, "Failed to create Kafka topics");
                throw;
            }
        }
    }
}
