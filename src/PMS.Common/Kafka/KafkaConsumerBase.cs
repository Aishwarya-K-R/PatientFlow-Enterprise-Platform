using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using PatientFlow.Contracts.Events;

namespace PatientFlow.Common.Kafka;

/// <summary>
/// Base class for reliable Kafka consumers with manual offset commit.
/// Prevents message loss by committing offsets only after successful processing.
/// </summary>
public abstract class KafkaConsumerBase<TPayload> : BackgroundService
{
    private readonly IConsumer<Null, string> _consumer;
    private readonly ILogger _logger;
    private readonly string _topic;

    protected KafkaConsumerBase(
        IConfiguration config,
        ILogger logger,
        string topic,
        string groupId)
    {
        _logger = logger;
        _topic = topic;

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
                        _logger.LogWarning("Failed to deserialize message envelope, skipping");
                        _consumer.Commit(consumeResult);
                        continue;
                    }

                    // Process message (implemented by derived class)
                    var processed = await ProcessMessageAsync(envelope, stoppingToken);

                    if (processed)
                    {
                        // SUCCESS: Commit offset (message will not be re-delivered)
                        _consumer.Commit(consumeResult);
                        _logger.LogDebug("Committed offset {Offset} for {Topic}[{Partition}]",
                            consumeResult.Offset.Value,
                            consumeResult.Topic,
                            consumeResult.Partition.Value);
                    }
                    else
                    {
                        // FAILURE: Don't commit (message will be re-delivered)
                        _logger.LogWarning("Processing failed for message at offset {Offset}, will retry",
                            consumeResult.Offset.Value);
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
            _logger.LogInformation("Kafka consumer stopped");
        }
    }

    /// <summary>
    /// Process the received message. Return true if processing succeeded, false to retry.
    /// </summary>
    protected abstract Task<bool> ProcessMessageAsync(EventEnvelope envelope, CancellationToken cancellationToken);
}
