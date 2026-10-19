using System.Text.Json;
using System.Text.RegularExpressions;

namespace AipAuditService;

/// <summary>
/// One audited AipDecisionMade event. Stored in MongoDB (collection audit_events) with _id = eventId,
/// so the same event delivered twice is stored once. Records where it came from (partition and offset).
/// </summary>
public sealed class AuditEvent
{
    public string EventId { get; set; } = "";
    public string EventType { get; set; } = "";
    public int SchemaVersion { get; set; }
    public string ApplicationId { get; set; } = "";
    public string Decision { get; set; } = "";
    public double LoanAmount { get; set; }
    public long MaxBorrowing { get; set; }
    public DateTime OccurredAt { get; set; }
    public DateTime ReceivedAt { get; set; }
    public int Partition { get; set; }
    public long Offset { get; set; }
}

public sealed record AuditTrail(string ApplicationId, IReadOnlyList<AuditEvent> Events);

public sealed record ApiError(string Error, IReadOnlyList<string>? Details = null);

public static partial class ApplicationIds
{
    public const string Format = "AIP-yyyyMMdd-XXXXXXXX";

    public static bool IsValid(string? value) => value is not null && Pattern().IsMatch(value);

    [GeneratedRegex("^AIP-[0-9]{8}-[0-9A-F]{8}$")]
    private static partial Regex Pattern();
}

/// <summary>
/// Checks an event against the AipDecisionMade contract (schemaVersion 1) published by aip-api.
/// Anything that doesn't match is rejected with a reason, and goes to the dead-letter topic.
/// </summary>
public static class DecisionEventParser
{
    public const string EventType = "AipDecisionMade";
    public const int SupportedSchemaVersion = 1;
    private static readonly string[] Decisions = ["Accepted", "Referred", "Declined"];

    public static (AuditEvent? Event, string? Error) Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (null, "Empty message");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return (null, "Not valid JSON");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, "Not a JSON object");
            }

            var eventId = Text(root, "eventId");
            if (string.IsNullOrWhiteSpace(eventId) || eventId.Length > 100)
            {
                return (null, "Missing or invalid eventId");
            }

            var eventType = Text(root, "eventType");
            if (eventType != EventType)
            {
                return (null, $"Unsupported eventType '{eventType}'");
            }

            if (!root.TryGetProperty("schemaVersion", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var schemaVersion)
                || schemaVersion != SupportedSchemaVersion)
            {
                return (null, $"Unsupported schemaVersion (expected {SupportedSchemaVersion})");
            }

            var applicationId = Text(root, "applicationId");
            if (!ApplicationIds.IsValid(applicationId))
            {
                return (null, "Missing or invalid applicationId");
            }

            var decision = Text(root, "decision");
            if (decision is null || !Decisions.Contains(decision))
            {
                return (null, "Missing or invalid decision");
            }

            if (!root.TryGetProperty("occurredAt", out var occurred)
                || occurred.ValueKind != JsonValueKind.String
                || !occurred.TryGetDateTime(out var occurredAt))
            {
                return (null, "Missing or invalid occurredAt");
            }

            return (new AuditEvent
            {
                EventId = eventId,
                EventType = eventType,
                SchemaVersion = schemaVersion,
                ApplicationId = applicationId!,
                Decision = decision,
                LoanAmount = root.TryGetProperty("loanAmount", out var loan) && loan.ValueKind == JsonValueKind.Number ? loan.GetDouble() : 0,
                MaxBorrowing = root.TryGetProperty("maxBorrowing", out var max) && max.ValueKind == JsonValueKind.Number && max.TryGetInt64(out var maxValue) ? maxValue : 0,
                OccurredAt = occurredAt.ToUniversalTime()
            }, null);
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
