using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AipAuditService.Tests;

/// <summary>The audit API with the Kafka consumer off and an in-memory store in place of MongoDB.</summary>
public class AuditEndpointTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string ApplicationId = "AIP-20261019-7F3C2A9B";

    private HttpClient Client(IAuditStore store) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("CONSUMER_ENABLED", "false");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(store);
                services.AddSingleton(AuditOptions.From(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()) with { ConsumerEnabled = false });
            });
        }).CreateClient();

    private static AuditEvent Audited(string eventId, string decision, int minute, long offset) => new()
    {
        EventId = eventId,
        EventType = "AipDecisionMade",
        SchemaVersion = 1,
        ApplicationId = ApplicationId,
        Decision = decision,
        LoanAmount = 150000,
        MaxBorrowing = 150000,
        OccurredAt = new DateTime(2026, 10, 19, 11, minute, 0, DateTimeKind.Utc),
        ReceivedAt = new DateTime(2026, 10, 19, 11, minute, 1, DateTimeKind.Utc),
        Partition = 1,
        Offset = offset
    };

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var response = await Client(new InMemoryAuditStore()).GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AuditTrail_ReturnsEventsOldestFirst()
    {
        var store = new InMemoryAuditStore();
        await store.AddAsync(Audited("e2", "Accepted", minute: 35, offset: 8), CancellationToken.None);
        await store.AddAsync(Audited("e1", "Referred", minute: 30, offset: 5), CancellationToken.None);

        var response = await Client(store).GetAsync($"/api/audit/{ApplicationId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ApplicationId, body.GetProperty("applicationId").GetString());
        var events = body.GetProperty("events").EnumerateArray().ToList();
        Assert.Equal(new[] { "e1", "e2" }, events.Select(e => e.GetProperty("eventId").GetString()!));
        Assert.Equal("AipDecisionMade", events[0].GetProperty("eventType").GetString());
        Assert.Equal(5, events[0].GetProperty("offset").GetInt64());
    }

    [Fact]
    public async Task UnknownApplication_ReturnsAnEmptyTrail()
    {
        var response = await Client(new InMemoryAuditStore()).GetAsync("/api/audit/AIP-20000101-00000000");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The deploy smoke test relies on this exact shape
        Assert.Contains("\"events\":[]", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task InvalidApplicationId_Returns400()
    {
        var response = await Client(new InMemoryAuditStore()).GetAsync("/api/audit/not-an-id");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MongoDbDown_Returns503()
    {
        var response = await Client(new FailingAuditStore()).GetAsync($"/api/audit/{ApplicationId}");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task UnknownRoute_Returns404()
    {
        var response = await Client(new InMemoryAuditStore()).GetAsync("/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
