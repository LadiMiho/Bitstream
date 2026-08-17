# Architecture

Implements TRD §2.1: presentation, application services, integration adapters and persistence
are distinct layers, and business logic never calls an external system directly (TR-ARC-01).

## Projects and the reference rule

```
Bitstream.Domain                     entities, enumerations, the activation state machine
        ^
Bitstream.Application                service contracts, integration ports, DTOs
        ^                    ^
Bitstream.Infrastructure.Persistence  Bitstream.Infrastructure.Integration
        ^                    ^
Bitstream.Api                        endpoints, middleware, composition root
```

| Project | References | Layer (TRD §2.1) | May contain |
| --- | --- | --- | --- |
| `Bitstream.Domain` | none | — | Entities, enums, invariants. No EF Core, no HTTP, no configuration. |
| `Bitstream.Application` | Domain | Application services | Service interfaces, integration **ports**, DTOs. No adapter, no `HttpClient`, no `DbContext`. |
| `Bitstream.Infrastructure.Persistence` | Application, Domain | Persistence | `BitstreamDbContext`, entity configurations, repositories. |
| `Bitstream.Infrastructure.Integration` | Application, Domain | Integration adapters | The only place with an `HttpClient`, an SMTP client or a vendor SDK. |
| `Bitstream.Api` | all four | Presentation | Endpoints, middleware, DI wiring. Composition root only. |

`Bitstream.Api` references both Infrastructure projects so that `Program.cs` can call their
registration extensions. That is the composition root and nothing else: no endpoint uses an
adapter or a `DbContext` type directly.

**This is enforced, not documented.** `tests/Bitstream.ArchitectureTests/LayeringTests.cs`
reads the compiled assembly references — which the compiler emits only for assemblies a
project actually uses — and fails the build if the application layer picks up
`System.Net.Http`, `Microsoft.EntityFrameworkCore` or either Infrastructure assembly, or if a
port is implemented outside the integration layer.

## Ports and adapters (TR-ARC-02)

Every external system is reached through an interface in
`Bitstream.Application.Abstractions.Integration`:

| Port | TRD §7.1 rows | Adapter |
| --- | --- | --- |
| `ICrmGateway` | INT-CRM-01, -02, -04, -06, -08, -09 | `Crm/CrmHttpGateway` |
| `IBiGateway` | INT-BI-01, INT-BI-02 | `Bi/BiGateway` |
| `ISapGateway` | INT-SAP-01 | `Sap/SapGateway` |
| `IEmailGateway` | INT-MAIL-01 | `Mail/SmtpEmailGateway` |
| `IIntegrationOutbox` | all of the above | persistence-backed |

The ports speak the portal's vocabulary, not the vendor's, so a target system can be replaced
without touching business logic. `IntegrationResult<T>` carries the one distinction the
adapters alone can make: **business rejection** (never retried, TR-INT-19) versus **technical
failure or timeout** (retried with backoff, TR-INT-20).

## Message flow

**Outbound (TR-ARC-03).** An application service writes the business record and enqueues an
`IntegrationMessage` in the same transaction. A background dispatcher — not the request
thread — claims due messages and calls the gateway. So a CRM outage delays a request but never
loses it, and the ISP still gets an identifier immediately (TR-ACT-07).

**Inbound (TR-INT-24, TR-INT-07).** `POST /api/v1/tickets/{identifier}/events` persists the raw
payload, deduplicates on `eventId`, acknowledges, and only then interprets the event
asynchronously — so CRM is never held open on portal-side work (TR-INT-30) and a mapping defect
can be corrected and the events replayed.

Both directions use one table, `ops.IntegrationMessage`, distinguished by `Direction`: one
store, one dead-letter mechanism, one replay path.

## Cross-cutting

| Concern | Where | Requirement |
| --- | --- | --- |
| Correlation ID | `Middleware/CorrelationIdMiddleware` — accepts an inbound ID, echoes it, puts it in the log scope | TR-ARC-04 |
| Rate limiting | Named policies in `Program.cs`, applied per endpoint group | TR-SEC-29, TR-INT-30 |
| Health | `/health/live` (process only) and `/health/ready` (dependencies) | TR-ARC-05 |
| Audit | `IAuditWriter` — the only write path; the table takes no update or delete | TR-SEC-22 to TR-SEC-24 |
| Configuration | `appsettings.json` plus per-environment overrides; secrets from the secret store | TR-ARC-06, TR-SEC-28 |

`/health/live` deliberately consults no dependency: a CRM outage must not make IIS recycle a
portal that is still usable in read mode (TR-NFR-07).

## Deliberate gaps at scaffold stage

No application service is implemented, and the adapters throw. That is not an oversight — see
[`open-items.md`](open-items.md) for which TRD §11.4 answers are needed before which piece can
be built.
