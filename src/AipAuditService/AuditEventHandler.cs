using System.Text;
using Confluent.Kafka;

namespace AipAuditService;

/// <summary>A message as read from Kafka, kept separate from the Kafka client so the logic is easy to test.</summary>
public sealed record ConsumedMessage(string Topic, int Partition, long Offset, string? Key, string? Value);

public enum HandleResult
{
    Stored,
    Duplicate,
    DeadLettered
}

/// <summary>
/// What happens to each event: valid ones are stored once (duplicates ignored), invalid ones go to the
/// dead-letter topic so they don't block the events behind them. If MongoDB is down it throws,
/// and the consumer retries the same event later without committing its offset.
/// </summary>
public sealed class AuditEventHandler(IAuditStore store, IDeadLetterPublisher deadLetters, ILogger<AuditEventHandler> logger)
{
    public async Task<HandleResult> HandleAsync(ConsumedMessage message, CancellationToken cancellationToken)
    {
        var (auditEvent, error) = DecisionEventParser.Parse(message.Value);
        if (auditEvent is null)
        {
            await deadLetters.PublishAsync(message, error!, cancellationToken);
            logger.LogWarning("Event at {Topic}/{Partition}@{Offset} sent to the dead-letter topic: {Reason}",
                message.Topic, message.Partition, message.Offset, error);
            return HandleResult.DeadLettered;
        }

        auditEvent.ReceivedAt = DateTime.UtcNow;
        auditEvent.Partition = message.Partition;
        auditEvent.Offset = message.Offset;

        if (!await store.AddAsync(auditEvent, cancellationToken))
        {
            logger.LogInformation("Duplicate event {EventId} for {ApplicationId} ignored", auditEvent.EventId, auditEvent.ApplicationId);
            return HandleResult.Duplicate;
        }

        logger.LogInformation("Audited {EventType} {EventId} for {ApplicationId} ({Decision})",
            auditEvent.EventType, auditEvent.EventId, auditEvent.ApplicationId, auditEvent.Decision);
        return HandleResult.Stored;
    }
}

public interface IDeadLetterPublisher
{
    Task PublishAsync(ConsumedMessage original, string reason, CancellationToken cancellationToken);
}

/// <summary>Copies a rejected event to aip.decisions.dlq unchanged, with the reason and its origin in headers.</summary>
public sealed class KafkaDeadLetterPublisher : IDeadLetterPublisher, IDisposable
{
    private readonly ProducerConfig _config;
    private readonly string _topic;
    private readonly object _buildLock = new();
    private volatile IProducer<string?, string?>? _producer;

    public KafkaDeadLetterPublisher(AuditOptions options)
    {
        _topic = options.KafkaDeadLetterTopic;
        _config = new ProducerConfig
        {
            BootstrapServers = options.KafkaBootstrapServers,
            ClientId = "aip-audit-service-dlq",
            Acks = Acks.All,
            MessageTimeoutMs = 10_000
        };
    }

    private IProducer<string?, string?> Producer
    {
        get
        {
            if (_producer is not null)
            {
                return _producer;
            }

            lock (_buildLock)
            {
                return _producer ??= new ProducerBuilder<string?, string?>(_config).Build();
            }
        }
    }

    public async Task PublishAsync(ConsumedMessage original, string reason, CancellationToken cancellationToken)
    {
        var headers = new Headers
        {
            { "dlq-reason", Encoding.UTF8.GetBytes(reason) },
            { "source-topic", Encoding.UTF8.GetBytes(original.Topic) },
            { "source-partition", Encoding.UTF8.GetBytes(original.Partition.ToString()) },
            { "source-offset", Encoding.UTF8.GetBytes(original.Offset.ToString()) }
        };

        // Awaited, so the original offset is only committed once the copy is safely in the dead-letter topic
        await Producer.ProduceAsync(_topic,
            new Message<string?, string?> { Key = original.Key, Value = original.Value, Headers = headers },
            cancellationToken);
    }

    public void Dispose()
    {
        _producer?.Flush(TimeSpan.FromSeconds(5));
        _producer?.Dispose();
    }
}
