using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AipAuditService.Tests;

public class AuditEventHandlerTests
{
    private const string Topic = "aip.decisions";
    private const string ApplicationId = "AIP-20261019-7F3C2A9B";

    private readonly InMemoryAuditStore _store = new();
    private readonly RecordingDeadLetters _deadLetters = new();
    private readonly AuditEventHandler _handler;

    public AuditEventHandlerTests()
    {
        _handler = new AuditEventHandler(_store, _deadLetters, NullLogger<AuditEventHandler>.Instance);
    }

    private static string Event(string eventId, string decision = "Accepted") =>
        $$"""{"eventId":"{{eventId}}","eventType":"AipDecisionMade","schemaVersion":1,"occurredAt":"2026-10-19T11:30:00Z","applicationId":"{{ApplicationId}}","decision":"{{decision}}","loanAmount":150000,"maxBorrowing":150000}""";

    private static ConsumedMessage Message(string? value, long offset = 0, int partition = 1) =>
        new(Topic, partition, offset, ApplicationId, value);

    [Fact]
    public async Task ValidEvent_IsStoredWithWhereItCameFrom()
    {
        var result = await _handler.HandleAsync(Message(Event("e1"), offset: 42, partition: 2), CancellationToken.None);

        Assert.Equal(HandleResult.Stored, result);
        var stored = Assert.Single(_store.Events.Values);
        Assert.Equal("e1", stored.EventId);
        Assert.Equal(ApplicationId, stored.ApplicationId);
        Assert.Equal(2, stored.Partition);
        Assert.Equal(42, stored.Offset);
        Assert.True(stored.ReceivedAt > DateTime.UtcNow.AddMinutes(-1));
        Assert.Empty(_deadLetters.Messages);
    }

    [Fact]
    public async Task SameEventDeliveredTwice_IsStoredOnce()
    {
        // At-least-once delivery: Kafka can redeliver an event, e.g. after a consumer restart
        await _handler.HandleAsync(Message(Event("e1"), offset: 10), CancellationToken.None);

        var second = await _handler.HandleAsync(Message(Event("e1"), offset: 10), CancellationToken.None);

        Assert.Equal(HandleResult.Duplicate, second);
        Assert.Single(_store.Events);
    }

    [Fact]
    public async Task SameEventIdAtAnotherOffset_IsStillADuplicate()
    {
        // e.g. the producer retried and the event was written twice
        await _handler.HandleAsync(Message(Event("e1"), offset: 10), CancellationToken.None);

        var second = await _handler.HandleAsync(Message(Event("e1"), offset: 11), CancellationToken.None);

        Assert.Equal(HandleResult.Duplicate, second);
        Assert.Single(_store.Events);
    }

    [Fact]
    public async Task DifferentEventsForOneApplication_AreAllStored()
    {
        await _handler.HandleAsync(Message(Event("e1"), offset: 1), CancellationToken.None);
        await _handler.HandleAsync(Message(Event("e2", "Referred"), offset: 2), CancellationToken.None);

        var trail = await _store.ForApplicationAsync(ApplicationId, CancellationToken.None);

        Assert.Equal(2, trail.Count);
    }

    [Fact]
    public async Task InvalidEvent_GoesToTheDeadLetterTopicUnchanged()
    {
        var result = await _handler.HandleAsync(Message("{not json", offset: 7), CancellationToken.None);

        Assert.Equal(HandleResult.DeadLettered, result);
        Assert.Empty(_store.Events);
        var (original, reason) = Assert.Single(_deadLetters.Messages);
        Assert.Equal("{not json", original.Value);
        Assert.Equal(ApplicationId, original.Key);
        Assert.Equal(7, original.Offset);
        Assert.Equal("Not valid JSON", reason);
    }

    [Fact]
    public async Task MongoDbDown_Throws_SoTheEventIsRetriedNotLost()
    {
        var handler = new AuditEventHandler(new FailingAuditStore(), _deadLetters, NullLogger<AuditEventHandler>.Instance);

        await Assert.ThrowsAsync<TimeoutException>(() => handler.HandleAsync(Message(Event("e1")), CancellationToken.None));
        Assert.Empty(_deadLetters.Messages); // a storage problem is not the event's fault, so no dead-lettering
    }
}
