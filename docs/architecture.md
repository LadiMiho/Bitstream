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
| Audit | `AuditWriter` (Persistence, implementing `IAuditWriter`) — the only write path; the table takes no update or delete | TR-SEC-22 to TR-SEC-24 |
| Configuration | `appsettings.json` plus per-environment overrides; secrets from the secret store | TR-ARC-06, TR-SEC-28 |

`/health/live` deliberately consults no dependency: a CRM outage must not make IIS recycle a
portal that is still usable in read mode (TR-NFR-07).

## Access management (TRD §4)

The first fully implemented module — every other application service is still a stub. Session
cookie authentication, not a bearer token: `SessionAuthenticationHandler`
(`Bitstream.Api.Security`) resolves the cookie against `IIdentityService.ValidateSessionAsync`
on every request, because TR-SEC-07's "invalidated at logout and at lock" needs a server-side
record that can be revoked on demand — a signed, self-contained token cannot be, without a
second revocation mechanism that amounts to this one anyway.

| Concern | Where | Requirement |
| --- | --- | --- |
| Password hashing | `Argon2PasswordHasher` (Application — pure computation, no adapter needed) | TR-SEC-02 |
| Password policy | `PasswordPolicyValidator` + `CommonPasswordList` | TR-SEC-03 |
| Two-factor | `IdentityService` orchestrates; `TotpService` (RFC 6238) and the SMTP-backed `EmailOtp` channel | TR-SEC-04, TR-SEC-05 |
| Lockout | `IdentityService.AuthenticateAsync` — locked before a password check, locked at the configured failure threshold | TR-SEC-06, TR-SEC-12 |
| Sessions | `sec.UserSession` + `UserSessionStore`; idle and absolute timeout, whichever comes first | TR-SEC-07 |
| RBAC | `PermissionAuthorizationHandler` checks claims `SessionAuthenticationHandler` set, rebuilt from the database every request | TR-SEC-17, TR-SEC-20 |
| Ownership scoping | `AdministrationService.GetIspAsync` / `GetUserAsync` — decided from identity before the repository is touched | TR-SEC-18, TR-SEC-19 |
| Lock in place of delete | `SetIspStatusAsync` / `SetUserStatusAsync`; no delete endpoint exists anywhere | TR-SEC-11 |

Two design choices worth knowing before extending this module:

- **The default second-factor channel is `Totp`, not the TRD's own two named alternatives
  chosen at random.** TRD §11.4 open item 13 leaves the production channel undecided. Totp
  needs no delivery path (no SMTP, no SMS provider), so it is the one channel that works today
  regardless of how that item is answered; `EmailOtp` is fully implemented and switches on with
  one configuration value once `SmtpEmailGateway` is built, and `SmsOtp` throws
  `NotSupportedException`, consistent with how this codebase already leaves CRM and SAP
  unimplemented pending their own open items.
- **Ownership is not a permission.** `isp.read.all` lets an Administrator or Auditor read *any*
  ISP; an ISP user reading their *own* ISP needs no permission claim at all — the route requires
  only authentication, and `AdministrationService` decides the rest from `ICurrentUserContext`.
  This is what makes TR-SEC-19's not-found (rather than forbidden) response possible: the
  decision is identity-only, made before the database is even asked whether the record exists.

## Activation requests (TRD 5)

The second fully implemented module. `ActivationRequestService` owns the TRD 5.3 state machine
end to end, Submitted through Completed, including the steps CRM drives — see
[CRM integration](#crm-integration-trd-73) below for how those actually reach it.

| Concern | Where | Requirement |
| --- | --- | --- |
| Public identifier | `SqlPublicIdentifierGenerator`, calling `ops.usp_NextPublicIdentifier` inside the caller's transaction | TR-DAT-01 to TR-DAT-02e |
| Submission validation | `ActivationRequestService.SubmitAsync` — package, location, classification, contract duration, comment length, against `CatalogueOptions` | TR-ACT-01, TR-ACT-04, TR-ACT-05 |
| Coordinate parsing | `CoordinateParser` — a bare pair or a map URL's `@lat,lng` / `q=`/`ll=` parameter, normalised and range-checked | TR-ACT-02, TR-ACT-03 |
| State machine | `ActivationRequestTransitions` (Domain) is the single source of truth; every status change in the service goes through it | TRD 5.3 |
| GIS verification | `RecordGisOutcomeAsync` — the no-line and line-exists branches, only permitted from `AwaitingGisVerification` | TR-ACT-12 to TR-ACT-19 |
| CRM sync outcome | `MarkCrmSyncSucceededAsync` / `MarkCrmSyncFailedAsync` — PendingCrmSync to AwaitingGisVerification or IntegrationFailed, called by `OutboxDispatcher` | TRD 5.3 |
| Sales order, provisioning, completion | `ApplySalesOrderAsync`, `StartProvisioningAsync`, `CompleteAsync` — called by `InboundEventService` from Direction B events | TR-ACT-18, TRD 5.3 |

**The state machine is proven exhaustively, not just at the paths any one caller drives.**
`ActivationRequestTransitionsTests` checks every ordered pair of the ten statuses against an
independently restated copy of the TRD 5.3 table — every permitted transition and every
rejection, including self-transitions and skipped steps — so the table in
`ActivationRequestTransitions` cannot silently drift from the design without a test failing.

## CRM integration (TRD 7.3)

**Direction A (portal → CRM).** `ActivationRequestService.SubmitAsync` enqueues INT-CRM-01
(create customer) on `IIntegrationOutbox` and stops — it never calls `ICrmGateway` directly
(TR-ARC-01, TR-ARC-03). `OutboxDispatcher` (Application, `Services/Integration`) is the
background hosted service that claims due messages and calls the gateway:

1. Claims INT-CRM-01, calls `ICrmGateway.CreateCustomerAsync`. On success it enqueues INT-CRM-02
   itself, now carrying the real Business Partner the call returned — not a placeholder, and not
   something `SubmitAsync` could have known up front.
2. Claims INT-CRM-02, calls `CreateActivationTicketAsync`. On success it calls
   `IActivationRequestService.MarkCrmSyncSucceededAsync`, moving `PendingCrmSync` to
   `AwaitingGisVerification`.
3. On failure: a business rejection (TR-INT-19) or a technical failure that has exhausted
   `OutboxDispatcherOptions.MaxAttempts` (TR-INT-04) dead-letters the message and calls
   `MarkCrmSyncFailedAsync`, moving the request to `IntegrationFailed`. A technical failure with
   attempts remaining schedules a backoff retry and leaves the request exactly where it was —
   still `PendingCrmSync`, since nothing about it is wrong yet.

`CrmHttpGateway` (`Infrastructure.Integration/Crm`) implements the two calls against a
*provisional* payload shape (TRD §7.4's field list is the real target; the real CRM contract is
still TRD 11.4 open item 1). Every request carries an `Idempotency-Key` header set to the
envelope's `IdempotencyKey` — the request's own public identifier — so a retried message is
recognisable as a repeat rather than a second create (TR-INT-03, TR-INT-17). Everything that
would change when the real contract arrives is isolated to that one file: the request/response
record shapes, and `AuthorizeAsync` if the auth scheme differs from a bearer token.

**Direction B (CRM → portal).** `POST /api/v1/tickets/{identifier}/events` (`CrmInboundEndpoints`)
persists the raw event through `IIntegrationOutbox.RecordInboundAsync` before anything else
(TR-INT-07, TR-INT-24) — a repeated `eventId` is recognised there and returns the original
outcome without calling into interpretation again (TR-INT-25). A new event is handed to
`InboundEventService.ApplyAsync`, which discards (but still acknowledges) an event no later than
the request's `LastAppliedEventAt` (TR-INT-25, TR-PAS-17), then routes by event type:
`SALES_ORDER_OPENED`, `PROVISIONING_STARTED` and `TECHNICALLY_COMPLETED` call the matching
`ActivationRequestService` method; every other recognised type is a complaint-ticket concept
(TRD 6) not yet built, and is rejected 422 rather than silently accepted (TR-INT-27) — the full
event vocabulary is TRD 11.4 open item 4 regardless.

**A response code answers a different question than the state machine does.** 404 means the
identifier does not resolve to any known request; 422 means the event's shape is fine but it
does not apply here; 409 means it is a TRD 5.3 concept but not a permitted transition from the
current status. `ActivationRequestConflictException`, thrown by the same
`ActivationRequestService` methods Direction A's dispatcher calls, is what a sales order event
arriving before the request is even `LineAvailable` turns into.

**CRM simulator.** `tools/CrmSimulator` is a standalone minimal API standing in for CRM's
customer- and ticket-creation endpoints, matching `CrmHttpGateway`'s provisional shape and
honouring the same `Idempotency-Key` header (a repeated key returns the same identifiers rather
than minting new ones). Point `Integration:Crm:BaseAddress` at it for local development; there is
no real CRM to point at yet. The automated tests do not depend on it being run — they substitute
`FakeCrmGateway`, an in-process double with the same idempotent-by-key behaviour, so the test
suite has no second process to manage.

## Deliberate gaps

Every application service besides identity, administration and activation requests is still a
stub — complaint tickets, service changes, reporting — and the BI and SAP adapters still throw.
Direction A and B of CRM are built for activation requests specifically; the complaint-ticket
events they already recognise but reject exist so nothing about the inbound endpoint has to
change shape when that module is built. See [`open-items.md`](open-items.md) for which TRD §11.4
answers are needed before which piece can be built.
