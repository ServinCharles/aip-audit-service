using AipAuditService;

var builder = WebApplication.CreateBuilder(args);

var options = AuditOptions.From(builder.Configuration);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<IAuditStore, MongoAuditStore>();
builder.Services.AddSingleton<IDeadLetterPublisher, KafkaDeadLetterPublisher>();
builder.Services.AddSingleton<AuditEventHandler>();
builder.Services.AddHostedService<KafkaConsumerWorker>();

var app = builder.Build();

// Liveness/readiness probe: the web host is up (MongoDB and Kafka problems are logged and retried, not fatal)
app.MapGet("/health", (AuditOptions current) =>
    Results.Ok(new { status = "ok", service = "aip-audit-service", consumer = current.ConsumerEnabled }));

// Audit trail for one application, oldest event first. An application with no events returns an empty list.
app.MapGet("/api/audit/{applicationId}", async (
    string applicationId,
    IAuditStore store,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    if (!ApplicationIds.IsValid(applicationId))
    {
        return Results.BadRequest(new ApiError("Invalid application ID", [$"Expected {ApplicationIds.Format}"]));
    }

    try
    {
        var events = await store.ForApplicationAsync(applicationId, cancellationToken);
        return Results.Ok(new AuditTrail(applicationId, events));
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        logger.LogWarning(ex, "Could not read the audit trail for {ApplicationId}", applicationId);
        return Results.Json(new ApiError("Audit store unavailable"), statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapFallback(() => Results.NotFound(new ApiError("Not Found")));

app.Run();

// Exposes Program to WebApplicationFactory in the test project
public partial class Program { }
