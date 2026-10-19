using Confluent.Kafka;

namespace AipAuditService;

/// <summary>
/// Reads aip.decisions as consumer group aip-audit (at-least-once):
///  - the offset is committed only after an event is stored or dead-lettered, so a crash never loses an event
///  - a new consumer group starts from the oldest event, and a restarted one carries on from its last commit
///  - if MongoDB is down, the same event is retried every 5 seconds until it can be stored
/// </summary>
public sealed class KafkaConsumerWorker(AuditOptions options, AuditEventHandler handler, ILogger<KafkaConsumerWorker> logger)
    : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.ConsumerEnabled)
        {
            logger.LogInformation("Kafka consumer is switched off (CONSUMER_ENABLED=false)");
            return Task.CompletedTask;
        }

        // Consume() blocks, so the loop gets its own thread and the web host can start
        return Task.Run(() => RunAsync(stoppingToken), stoppingToken);
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = options.KafkaBootstrapServers,
            GroupId = options.KafkaGroupId,
            ClientId = "aip-audit-service",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };

        using var consumer = new ConsumerBuilder<string?, string?>(config)
            .SetErrorHandler((_, error) => logger.LogWarning("Kafka: {Reason}", error.Reason))
            .SetPartitionsAssignedHandler((_, partitions) => logger.LogInformation("Reading {Topic} partitions {Partitions}",
                options.KafkaTopic, string.Join(",", partitions.Select(p => p.Partition.Value))))
            .Build();

        consumer.Subscribe(options.KafkaTopic);
        logger.LogInformation("Consumer group {GroupId} subscribed to {Topic}", options.KafkaGroupId, options.KafkaTopic);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string?, string?>? result;
                try
                {
                    result = consumer.Consume(stoppingToken);
                }
                catch (ConsumeException ex)
                {
                    // e.g. the topic doesn't exist yet: wait and try again
                    logger.LogWarning("Consume failed: {Reason}", ex.Error.Reason);
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }

                if (result?.Message is null)
                {
                    continue;
                }

                var message = new ConsumedMessage(result.Topic, result.Partition.Value, result.Offset.Value,
                    result.Message.Key, result.Message.Value);
                try
                {
                    await handler.HandleAsync(message, stoppingToken);
                    consumer.Commit(result);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Not stored and not committed: read the same event again shortly, so nothing is lost
                    logger.LogWarning(ex, "Could not process {Topic}/{Partition}@{Offset}; retrying in 5s",
                        message.Topic, message.Partition, message.Offset);
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    try
                    {
                        consumer.Seek(result.TopicPartitionOffset);
                    }
                    catch (KafkaException seekError)
                    {
                        // The partition moved to another consumer during a rebalance; it resumes from the last commit
                        logger.LogInformation("Seek skipped: {Reason}", seekError.Error.Reason);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            consumer.Close(); // leaves the group cleanly so its partitions are reassigned straight away
        }
    }
}
