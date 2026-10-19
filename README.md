# AIP Audit Service

.NET 8 microservice that keeps the **audit trail** of mortgage Agreement in Principle (AIP) decisions. It reads `AipDecisionMade` events from **Kafka** and stores them in **MongoDB**, and has a small API to look up an application's trail.

```
aip-api ──publish AipDecisionMade──▶ Kafka topic aip.decisions ──▶ aip-audit-service ──▶ MongoDB aip_audit.audit_events
                                                                        │
                                              events it can't process ──┴──▶ aip.decisions.dlq
```

| Repo | Role |
| --- | --- |
| [aip-mortgage-api](https://github.com/ServinCharles/aip-mortgage-api) | Makes the decision, saves the application, publishes the event |
| **aip-audit-service** (this repo) | Consumes events, keeps the audit trail |
| [aip-deployment-pipeline](https://github.com/ServinCharles/aip-deployment-pipeline) | Deploys Kafka, MongoDB and this service to `<env>-data` / `<env>-api` |
| [Agentic-qa-demo](https://github.com/ServinCharles/Agentic-qa-demo) | `events` test suite |

## How it consumes

| Behaviour | How |
| --- | --- |
| **At-least-once** | The offset is committed only after an event is stored (or dead-lettered), so a crash never loses an event |
| **Idempotent** | MongoDB `_id` is the `eventId`, so an event delivered twice is stored once |
| **Ordered per application** | aip-api keys events by `applicationId`, so one application's events share a partition |
| **Dead-letter topic** | Events that break the contract (bad JSON, unknown type or schema version) go to `aip.decisions.dlq` with the reason in a `dlq-reason` header, and don't block the events behind them |
| **MongoDB down** | The event is not committed; the same event is retried every 5 seconds until it can be stored |
| **Restart** | Consumer group `aip-audit` carries on from its last committed offset; a brand-new group starts from the oldest event |

## API

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/health` | Kubernetes probe |
| GET | `/api/audit/{applicationId}` | The application's events, oldest first (`events: []` if none) |

```json
{
  "applicationId": "AIP-20261019-7F3C2A9B",
  "events": [
    {
      "eventId": "5b1f8c2e-3f4a-4c8e-9a51-2d7e6b0c9f13",
      "eventType": "AipDecisionMade",
      "schemaVersion": 1,
      "applicationId": "AIP-20261019-7F3C2A9B",
      "decision": "Accepted",
      "loanAmount": 150000,
      "maxBorrowing": 150000,
      "occurredAt": "2026-10-19T11:30:00Z",
      "receivedAt": "2026-10-19T11:30:00.412Z",
      "partition": 1,
      "offset": 42
    }
  ]
}
```

## Event contract

`AipDecisionMade`, schemaVersion 1, published by aip-api: `eventId`, `eventType`, `schemaVersion`, `occurredAt`, `applicationId`, `decision`, `loanAmount`, `maxBorrowing`. IDs and figures only, no personal data. A sample of the exact JSON is in `tests/AipAuditService.Tests/Contracts/`, and the contract test fails if this service stops accepting it.

## Configuration

| Variable | Default |
| --- | --- |
| `MONGODB_URI` | `mongodb://localhost:27017` |
| `MONGODB_DATABASE` | `aip_audit` |
| `KAFKA_BOOTSTRAP_SERVERS` | `localhost:9092` |
| `KAFKA_TOPIC` | `aip.decisions` |
| `KAFKA_DLQ_TOPIC` | `aip.decisions.dlq` |
| `KAFKA_GROUP_ID` | `aip-audit` |
| `CONSUMER_ENABLED` | `true` (`false` runs only the API) |

## Tests

| Level | What it proves |
| --- | --- |
| Unit: `DecisionEventParserTests` | The contract sample from aip-api is accepted; anything else is rejected with a reason |
| Unit: `AuditEventHandlerTests` | Stored once on duplicate delivery, dead-lettered when invalid, retried (not dead-lettered) when MongoDB is down |
| Endpoint: `AuditEndpointTests` | Trail oldest first, empty trail, 400 on a bad ID, 503 when MongoDB is down |
| CI event flow (real Kafka + MongoDB containers) | Audited end to end, duplicate stored once, broken event to the DLQ without blocking, catch-up after a restart |

```bash
dotnet test tests/AipAuditService.Tests
```

## Run locally

Port **5082** (8080 is Jenkins, 5080 aip-api, 5081 aip-chat-api), with Kafka and MongoDB running:

```bash
dotnet run --project src/AipAuditService --urls http://localhost:5082
```

## Jenkins build pipeline

Same structure as aip-mortgage-api: **Checkout → Build & Unit Test → SonarQube → Snyk → Docker Build → Image Scan → Push to ECR → Trigger Deploy**. Create a Pipeline job `aip-audit-service-build` from this repo (`*/main`, script path `Jenkinsfile`).
