using System.Collections.Concurrent;

namespace AipAuditService.Tests;

/// <summary>Behaves like MongoAuditStore: eventId is the unique key.</summary>
public sealed class InMemoryAuditStore : IAuditStore
{
    public ConcurrentDictionary<string, AuditEvent> Events { get; } = new();

    public Task<bool> AddAsync(AuditEvent auditEvent, CancellationToken cancellationToken) =>
        Task.FromResult(Events.TryAdd(auditEvent.EventId, auditEvent));

    public Task<IReadOnlyList<AuditEvent>> ForApplicationAsync(string applicationId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AuditEvent>>(Events.Values
            .Where(e => e.ApplicationId == applicationId)
            .OrderBy(e => e.OccurredAt)
            .ThenBy(e => e.Offset)
            .ToList());
}

public sealed class FailingAuditStore : IAuditStore
{
    public Task<bool> AddAsync(AuditEvent auditEvent, CancellationToken cancellationToken) =>
        throw new TimeoutException("MongoDB unreachable");

    public Task<IReadOnlyList<AuditEvent>> ForApplicationAsync(string applicationId, CancellationToken cancellationToken) =>
        throw new TimeoutException("MongoDB unreachable");
}

public sealed class RecordingDeadLetters : IDeadLetterPublisher
{
    public ConcurrentQueue<(ConsumedMessage Original, string Reason)> Messages { get; } = new();

    public Task PublishAsync(ConsumedMessage original, string reason, CancellationToken cancellationToken)
    {
        Messages.Enqueue((original, reason));
        return Task.CompletedTask;
    }
}
