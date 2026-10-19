using Xunit;

namespace AipAuditService.Tests;

public class DecisionEventParserTests
{
    // The exact JSON aip-api publishes (contract sample, schemaVersion 1)
    private static readonly string Contract = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contracts", "aip-decision-made.v1.json"));

    [Fact]
    public void Parse_AcceptsTheContractSampleFromAipApi()
    {
        var (auditEvent, error) = DecisionEventParser.Parse(Contract);

        Assert.Null(error);
        Assert.NotNull(auditEvent);
        Assert.Equal("5b1f8c2e-3f4a-4c8e-9a51-2d7e6b0c9f13", auditEvent.EventId);
        Assert.Equal("AipDecisionMade", auditEvent.EventType);
        Assert.Equal(1, auditEvent.SchemaVersion);
        Assert.Equal("AIP-20261019-7F3C2A9B", auditEvent.ApplicationId);
        Assert.Equal("Accepted", auditEvent.Decision);
        Assert.Equal(150000, auditEvent.LoanAmount);
        Assert.Equal(150000, auditEvent.MaxBorrowing);
        Assert.Equal(new DateTime(2026, 10, 19, 11, 30, 0, DateTimeKind.Utc), auditEvent.OccurredAt);
    }

    [Theory]
    [InlineData(null, "Empty message")]
    [InlineData("", "Empty message")]
    [InlineData("{not json", "Not valid JSON")]
    [InlineData("[1,2]", "Not a JSON object")]
    [InlineData("""{"eventType":"AipDecisionMade","schemaVersion":1,"applicationId":"AIP-20261019-7F3C2A9B","decision":"Accepted","occurredAt":"2026-10-19T11:30:00Z"}""", "Missing or invalid eventId")]
    [InlineData("""{"eventId":"e1","eventType":"SomethingElse","schemaVersion":1,"applicationId":"AIP-20261019-7F3C2A9B","decision":"Accepted","occurredAt":"2026-10-19T11:30:00Z"}""", "Unsupported eventType 'SomethingElse'")]
    [InlineData("""{"eventId":"e1","eventType":"AipDecisionMade","schemaVersion":2,"applicationId":"AIP-20261019-7F3C2A9B","decision":"Accepted","occurredAt":"2026-10-19T11:30:00Z"}""", "Unsupported schemaVersion (expected 1)")]
    [InlineData("""{"eventId":"e1","eventType":"AipDecisionMade","schemaVersion":1,"applicationId":"12345","decision":"Accepted","occurredAt":"2026-10-19T11:30:00Z"}""", "Missing or invalid applicationId")]
    [InlineData("""{"eventId":"e1","eventType":"AipDecisionMade","schemaVersion":1,"applicationId":"AIP-20261019-7F3C2A9B","decision":"Maybe","occurredAt":"2026-10-19T11:30:00Z"}""", "Missing or invalid decision")]
    [InlineData("""{"eventId":"e1","eventType":"AipDecisionMade","schemaVersion":1,"applicationId":"AIP-20261019-7F3C2A9B","decision":"Accepted","occurredAt":"yesterday"}""", "Missing or invalid occurredAt")]
    public void Parse_RejectsAnythingOutsideTheContract(string? json, string expectedError)
    {
        var (auditEvent, error) = DecisionEventParser.Parse(json);

        Assert.Null(auditEvent);
        Assert.Equal(expectedError, error);
    }

    [Fact]
    public void Parse_IgnoresFieldsAddedLater()
    {
        // A later aip-api may add fields to schemaVersion 1; the consumer must keep working
        var json = Contract.Replace("\"decision\":", "\"channel\":\"web\",\"decision\":");

        var (auditEvent, error) = DecisionEventParser.Parse(json);

        Assert.Null(error);
        Assert.NotNull(auditEvent);
    }
}
