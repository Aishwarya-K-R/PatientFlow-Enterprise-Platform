using Confluent.Kafka;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace PatientFlow.Common.Kafka;

public class KafkaProducer
{
    private readonly IProducer<Null, string> _producer;

    public KafkaProducer(IConfiguration config)
    {
        var producerConfig = new ProducerConfig
        {
            BootstrapServers = config["Kafka:BootstrapServers"]
        };

        _producer = new ProducerBuilder<Null, string>(producerConfig).Build();
    }

    public async Task PublishAsync(string topic, object message)
    {
        var json = JsonSerializer.Serialize(message);

        await _producer.ProduceAsync(topic, new Message<Null, string>
        {
            Value = json
        });
    }

    /// <summary>
    /// Publish a pre-serialized JSON string directly. Used by the Outbox
    /// publisher to avoid deserializing-then-reserializing a payload that
    /// is already valid JSON in the database.
    /// </summary>
    public async Task PublishRawAsync(string topic, string jsonPayload)
    {
        await _producer.ProduceAsync(topic, new Message<Null, string>
        {
            Value = jsonPayload
        });
    }
}
