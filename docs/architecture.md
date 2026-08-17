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

## Deliberate gaps

Every application service besides identity and administration is still a stub, and the CRM,
BI and SAP adapters still throw. That is not an oversight — see [`open-items.md`](open-items.md)
for which TRD §11.4 answers are needed before which piece can be built.
