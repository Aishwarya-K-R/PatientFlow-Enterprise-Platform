using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using PatientFlow.Contracts.Events;

namespace PatientFlow.Common.Kafka;

/// <summary>
/// Base class for reliable Kafka consumers with manual offset commit, retry topics, and DLQ.
/// Prevents message loss by committing offsets only after successful processing.
/// Failed messages are retried with exponential backoff, then sent to DLQ after max attempts.
/// </summary>
public abstract class KafkaConsumerBase<TPayload> : BackgroundService
{
    private readonly IConsumer<Null, string> _consumer;
    private readonly IProducer<Null, string> _producer;
    private readonly ILogger _logger;
    private readonly string _topic;
    private readonly string _retryTopic;
    private readonly string _dlqTopic;
    private readonly int _maxRetryAttempts;

    protected KafkaConsumerBase(
        IConfiguration config,
        IServiceProvider serviceProvider,
        ILogger logger,
        string topic,
        string groupId,
        int maxRetryAttempts = 3)
    {
        _logger = logger;
        _topic = topic;

        // If subscribed to a retry topic, derive retry/DLQ names from the BASE topic
        // so failed retries loop back to the same retry queue (instead of creating
        // patient-created-retry-retry, -retry-retry-retry, ...).
        var baseTopic = topic.EndsWith("-retry") ? topic[..^"-retry".Length] : topic;
        _retryTopic = $"{baseTopic}-retry";
        _dlqTopic = $"{baseTopic}-dlq";
        _maxRetryAttempts = maxRetryAttempts;

        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = config["Kafka:BootstrapServers"],
            GroupId = groupId,

            // Manual offset commit: Only commit after successful processing
            // Prevents message loss if consumer crashes during processing
            EnableAutoCommit = false,

            // Start from earliest unprocessed message
            AutoOffsetReset = AutoOffsetReset.Earliest,

            // Isolation level: Read only committed messages
            IsolationLevel = IsolationLevel.ReadCommitted,

            // Session timeout: How long before consumer considered dead
            SessionTimeoutMs = 45000,

            // Max poll interval: Max time between polls
            MaxPollIntervalMs = 300000,

            // Enable partition EOF
            EnablePartitionEof = false
        };

        var producerConfig = new ProducerConfig
        {
            BootstrapServers = config["Kafka:BootstrapServers"],
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageSendMaxRetries = 10,
            CompressionType = CompressionType.Snappy
        };

        _consumer = new ConsumerBuilder<Null, string>(consumerConfig)
            .SetErrorHandler((_, error) =>
            {
                _logger.LogError("Kafka consumer error: {Reason}", error.Reason);
            })
            .SetPartitionsAssignedHandler((c, partitions) =>
            {
                _logger.LogInformation("Partitions assigned: {Partitions}",
                    string.Join(", ", partitions.Select(p => $"{p.Topic}[{p.Partition.Value}]")));
            })
            .SetPartitionsRevokedHandler((c, partitions) =>
            {
                _logger.LogInformation("Partitions revoked: {Partitions}",
                    string.Join(", ", partitions.Select(p => $"{p.Topic}[{p.Partition.Value}]")));
            })
            .Build();

        _producer = new ProducerBuilder<Null, string>(producerConfig).Build();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _consumer.Subscribe(_topic);
        _logger.LogInformation("Kafka consumer started, subscribed to {Topic}", _topic);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var consumeResult = _consumer.Consume(stoppingToken);

                    if (consumeResult?.Message == null)
                        continue;

                    _logger.LogDebug("Received message from {Topic}[{Partition}] at offset {Offset}",
                        consumeResult.Topic,
                        consumeResult.Partition.Value,
                        consumeResult.Offset.Value);

                    // Deserialize envelope
                    var envelope = JsonSerializer.Deserialize<EventEnvelope>(consumeResult.Message.Value);
                    if (envelope == null)
                    {
                        _logger.LogWarning("Failed to deserialize message envelope, sending to DLQ");
                        await SendToDLQAsync(consumeResult.Message.Value, "Failed to deserialize envelope");
                        _consumer.Commit(consumeResult);
                        continue;
                    }

                    // Get retry count from envelope metadata (default to 0)
                    var retryCount = envelope.Metadata?.ContainsKey("RetryCount") == true
                        ? int.Parse(envelope.Metadata["RetryCount"])
                        : 0;

                    // Process message (implemented by derived class)
                    var processed = await ProcessMessageAsync(envelope, stoppingToken);

                    if (processed)
                    {
                        // SUCCESS: Commit offset (message will not be re-delivered)
                        _consumer.Commit(consumeResult);
                        _logger.LogInformation("Successfully processed event {EventId} from {Topic}",
                            envelope.EventId, consumeResult.Topic);
                    }
                    else
                    {
                        // FAILURE: Handle retry or DLQ
                        if (retryCount >= _maxRetryAttempts)
                        {
                            // Max retries exceeded - send to DLQ
                            _logger.LogError("Max retry attempts ({MaxRetries}) exceeded for event {EventId}, sending to DLQ",
                                _maxRetryAttempts, envelope.EventId);
                            await SendToDLQAsync(consumeResult.Message.Value, $"Max retries exceeded: {retryCount}");
                            _consumer.Commit(consumeResult); // Commit to avoid infinite loop
                        }
                        else
                        {
                            // Send to retry topic with incremented count
                            _logger.LogWarning("Processing failed for event {EventId} (attempt {Attempt}/{Max}), sending to retry topic",
                                envelope.EventId, retryCount + 1, _maxRetryAttempts);
                            await SendToRetryTopicAsync(envelope, retryCount + 1);
                            _consumer.Commit(consumeResult); // Commit original message
                        }
                    }
                }
                catch (ConsumeException ex)
                {
                    _logger.LogError(ex, "Error consuming message: {Reason}", ex.Error.Reason);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Consumer operation cancelled");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error in consumer loop");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
        }
        finally
        {
            _consumer.Close();
            _consumer.Dispose();
            _producer.Dispose();
            _logger.LogInformation("Kafka consumer stopped");
        }
    }

    private async Task SendToRetryTopicAsync(EventEnvelope envelope, int retryCount)
    {
        try
        {
            // Add retry metadata
            envelope.Metadata ??= new Dictionary<string, string>();
            envelope.Metadata["RetryCount"] = retryCount.ToString();
            envelope.Metadata["LastRetryAt"] = DateTime.UtcNow.ToString("O");
            envelope.Metadata["OriginalTopic"] = _topic;

            var messageJson = JsonSerializer.Serialize(envelope);
            var message = new Message<Null, string> { Value = messageJson };

            await _producer.ProduceAsync(_retryTopic, message);
            _logger.LogInformation("Sent event {EventId} to retry topic (attempt {Attempt})",
                envelope.EventId, retryCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send message to retry topic for event {EventId}",
                envelope.EventId);
        }
    }

    private async Task SendToDLQAsync(string originalMessage, string reason)
    {
        try
        {
            var dlqMessage = new
            {
                OriginalMessage = originalMessage,
                Reason = reason,
                Topic = _topic,
                FailedAt = DateTime.UtcNow,
                ConsumerGroup = _consumer.MemberId
            };

            var messageJson = JsonSerializer.Serialize(dlqMessage);
            var message = new Message<Null, string> { Value = messageJson };

            await _producer.ProduceAsync(_dlqTopic, message);
            _logger.LogWarning("Sent message to DLQ: {Reason}", reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send message to DLQ");
        }
    }

    /// <summary>
    /// Process the received message. Return true if processing succeeded, false to retry.
    /// </summary>
    protected abstract Task<bool> ProcessMessageAsync(EventEnvelope envelope, CancellationToken cancellationToken);
}
