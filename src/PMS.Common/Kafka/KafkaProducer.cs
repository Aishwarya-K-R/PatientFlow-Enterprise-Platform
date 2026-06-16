using Confluent.Kafka;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PatientFlow.Common.Metrics;

namespace PatientFlow.Common.Kafka;

public class KafkaProducer : IDisposable
{
    private readonly IProducer<Null, string> _producer;
    private readonly ILogger<KafkaProducer> _logger;
    private bool _disposed;

    public KafkaProducer(IConfiguration config, ILogger<KafkaProducer> logger)
    {
        _logger = logger;

        var producerConfig = new ProducerConfig
        {
            BootstrapServers = config["Kafka:BootstrapServers"],

            // Reliability: Wait for all replicas to acknowledge
            // Prevents message loss if leader crashes
            Acks = Acks.All,

            // Idempotence: Kafka deduplicates messages at broker level
            // Prevents duplicate messages from producer retries
            EnableIdempotence = true,

            // Retries: Automatically retry failed sends
            MessageSendMaxRetries = 10,
            RetryBackoffMs = 100,

            // Timeout: Maximum time to wait for send
            RequestTimeoutMs = 30000,

            // Batching: Wait up to 5ms to batch messages for efficiency
            LingerMs = 5,

            // Compression: Reduce network bandwidth
            CompressionType = CompressionType.Snappy,

            // Max in-flight requests: Limit for ordering guarantees
            MaxInFlight = 5
        };

        _producer = new ProducerBuilder<Null, string>(producerConfig)
            .SetErrorHandler((_, error) =>
            {
                _logger.LogError("Kafka producer error: {Reason}", error.Reason);
            })
            .SetLogHandler((_, logMessage) =>
            {
                _logger.LogDebug("Kafka producer log: {Message}", logMessage.Message);
            })
            .Build();

        _logger.LogInformation("Kafka producer initialized with reliability settings");
    }

    public async Task PublishAsync(string topic, object message)
    {
        var json = JsonSerializer.Serialize(message);

        try
        {
            var result = await _producer.ProduceAsync(topic, new Message<Null, string>
            {
                Value = json
            });

            AppMetrics.KafkaMessagesPublished.WithLabels(topic).Inc();

            _logger.LogDebug("Message published to {Topic} at offset {Offset}", 
                topic, result.Offset.Value);
        }
        catch (ProduceException<Null, string> ex)
        {
            AppMetrics.KafkaMessagesFailed.WithLabels(topic).Inc();
            _logger.LogError(ex, "Failed to publish message to {Topic}: {Reason}", 
                topic, ex.Error.Reason);
            throw;
        }
    }

    /// <summary>
    /// Publish a pre-serialized JSON string directly. Used by the Outbox
    /// publisher to avoid deserializing-then-reserializing a payload that
    /// is already valid JSON in the database.
    /// </summary>
    public async Task PublishRawAsync(string topic, string jsonPayload)
    {
        try
        {
            var result = await _producer.ProduceAsync(topic, new Message<Null, string>
            {
                Value = jsonPayload
            });

            AppMetrics.KafkaMessagesPublished.WithLabels(topic).Inc();

            _logger.LogDebug("Raw message published to {Topic} at offset {Offset}", 
                topic, result.Offset.Value);
        }
        catch (ProduceException<Null, string> ex)
        {
            AppMetrics.KafkaMessagesFailed.WithLabels(topic).Inc();
            _logger.LogError(ex, "Failed to publish raw message to {Topic}: {Reason}", 
                topic, ex.Error.Reason);
            throw;
        }
    }

    /// <summary>
    /// Flush all pending messages before shutdown.
    /// Critical for ensuring messages aren't lost during app termination.
    /// </summary>
    public void Flush(TimeSpan timeout)
    {
        try
        {
            _logger.LogInformation("Flushing Kafka producer (timeout: {Timeout}s)...", 
                timeout.TotalSeconds);

            var remaining = _producer.Flush(timeout);

            if (remaining > 0)
            {
                _logger.LogWarning("{Count} messages still pending after flush timeout", 
                    remaining);
            }
            else
            {
                _logger.LogInformation("All messages flushed successfully");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Kafka producer flush");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        try
        {
            // Flush pending messages with 10 second timeout
            Flush(TimeSpan.FromSeconds(10));

            _producer?.Dispose();
            _logger.LogInformation("Kafka producer disposed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disposing Kafka producer");
        }
        finally
        {
            _disposed = true;
        }
    }
}
