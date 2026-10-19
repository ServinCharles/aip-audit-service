using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;

namespace AipAuditService;

public interface IAuditStore
{
    /// <summary>Stores the event. Returns false if an event with the same eventId is already stored (a duplicate delivery).</summary>
    Task<bool> AddAsync(AuditEvent auditEvent, CancellationToken cancellationToken);

    Task<IReadOnlyList<AuditEvent>> ForApplicationAsync(string applicationId, CancellationToken cancellationToken);
}

/// <summary>MongoDB collection audit_events, _id = eventId (the unique key that makes storing idempotent).</summary>
public sealed class MongoAuditStore : IAuditStore
{
    private readonly IMongoCollection<AuditEvent> _collection;
    private volatile bool _indexesReady;

    static MongoAuditStore()
    {
        ConventionRegistry.Register(
            "aip-audit-events",
            new ConventionPack { new CamelCaseElementNameConvention(), new IgnoreExtraElementsConvention(true) },
            type => type == typeof(AuditEvent));
        BsonClassMap.TryRegisterClassMap<AuditEvent>(map =>
        {
            map.AutoMap();
            map.MapIdMember(e => e.EventId);
        });
    }

    public MongoAuditStore(AuditOptions options)
    {
        var settings = MongoClientSettings.FromConnectionString(options.MongoUri);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);
        settings.ConnectTimeout = TimeSpan.FromSeconds(2);

        _collection = new MongoClient(settings)
            .GetDatabase(options.MongoDatabase)
            .GetCollection<AuditEvent>("audit_events");
    }

    public async Task<bool> AddAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        await EnsureIndexesAsync(cancellationToken);
        try
        {
            await _collection.InsertOneAsync(auditEvent, cancellationToken: cancellationToken);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<AuditEvent>> ForApplicationAsync(string applicationId, CancellationToken cancellationToken) =>
        await _collection.Find(e => e.ApplicationId == applicationId)
            .SortBy(e => e.OccurredAt)
            .ThenBy(e => e.Offset)
            .ToListAsync(cancellationToken);

    private async Task EnsureIndexesAsync(CancellationToken cancellationToken)
    {
        if (_indexesReady)
        {
            return;
        }

        // Look-ups are by application, so index it
        await _collection.Indexes.CreateOneAsync(
            new CreateIndexModel<AuditEvent>(
                Builders<AuditEvent>.IndexKeys.Ascending(e => e.ApplicationId),
                new CreateIndexOptions { Name = "applicationId" }),
            cancellationToken: cancellationToken);
        _indexesReady = true;
    }
}
