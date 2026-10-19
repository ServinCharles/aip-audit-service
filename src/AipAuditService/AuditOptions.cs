namespace AipAuditService;

/// <summary>Settings from environment variables. Kubernetes sets the MongoDB and Kafka addresses per environment.</summary>
public sealed record AuditOptions(
    string MongoUri,
    string MongoDatabase,
    string KafkaBootstrapServers,
    string KafkaTopic,
    string KafkaDeadLetterTopic,
    string KafkaGroupId,
    bool ConsumerEnabled)
{
    public static AuditOptions From(IConfiguration configuration) => new(
        configuration["MONGODB_URI"] ?? "mongodb://localhost:27017",
        configuration["MONGODB_DATABASE"] ?? "aip_audit",
        configuration["KAFKA_BOOTSTRAP_SERVERS"] ?? "localhost:9092",
        configuration["KAFKA_TOPIC"] ?? "aip.decisions",
        configuration["KAFKA_DLQ_TOPIC"] ?? "aip.decisions.dlq",
        configuration["KAFKA_GROUP_ID"] ?? "aip-audit",
        // false runs only the audit API (used by tests); the consumer is on by default
        configuration.GetValue("CONSUMER_ENABLED", true));
}
