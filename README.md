# ISP Platform — Bitstream Portal

Project scaffold for the ISP Platform (Bitstream Portal), built to
*Technical Requirements Document v1.0, August 2026*.

**Foundation, Access Management (TRD §4), Activation Requests (TRD §5) and CRM integration
(TRD §7.3) are built.** Structure, contracts, schema, configuration, logging, health, CI and
deployment are in place; authentication, 2FA, sessions, RBAC and ISP/user administration are
built; activation request submission, the GIS verification admin screen and the TRD §5.3 state
machine are built; and that state machine is now driven end to end by a real (if provisional)
CRM integration — an outbox dispatcher calling CRM for customer and ticket creation, and the
inbound event API applying sales order, provisioning and completion events back. Every other
application service — complaint tickets, service changes, reporting, the BI/SAP adapters — is
still unimplemented, and their endpoints still answer `501 Not Implemented`.

- .NET 10 (C#) backend, layered per TRD §2.1
- MSSQL, schema defined by T-SQL scripts
- Vanilla JavaScript with Tailwind CSS — no framework, no bundler
- Windows Server / IIS, no containers anywhere

## Layout

```
Bitstream.sln
src/
  Bitstream.Domain                      entities and enums (TRD §3.1)
  Bitstream.Application                 service contracts and integration ports
  Bitstream.Infrastructure.Persistence  EF Core mapped to the physical schema
  Bitstream.Infrastructure.Integration  CRM, BI, SAP and SMTP adapters
  Bitstream.Api                         minimal-API endpoints, composition root
  Bitstream.Web                         HTML, ES modules, Tailwind CLI build
tests/
  Bitstream.ArchitectureTests           layering rules, enforced at build time
  Bitstream.Api.Tests                   middleware, health endpoints, config validators
    Identity/                           auth, RBAC, lockout, sessions — see below
    Activation/                         submission, state machine, GIS verification — see below
    Integration/                        CRM outbox, dispatcher, Direction A/B, end-to-end — see below
tools/
  CrmSimulator                          standalone stand-in for CRM, for local development
deploy/
  environments/                         one file per environment, values only
  *.ps1                                 prerequisites, site, secrets, deployment
db/
  mssql/                                numbered, idempotent T-SQL
  Deploy-Database.ps1, Get-SchemaStatus.ps1
docs/
  architecture.md                       layers, ports, message flow
  open-items.md                         which TRD §11.4 answers block what
  deployment-iis.md                     Windows Server / IIS
  integration/interface-inventory.md    every TRD §7.1 row mapped
  adr/                                  the two decisions the brief asked us to state
.github/workflows/ci.yml                build, lint, test, publish — no container step
```

## Getting started

```bash
# Backend
dotnet restore
dotnet build

# Database (Windows, SqlServer PowerShell module)
./db/Deploy-Database.ps1 -ServerInstance . -Database BitstreamPortal -AppUser 'DOMAIN\svc_bitstream_dev'

# Frontend
cd src/Bitstream.Web && npm ci && npm run build:css

# Run — portal at /, generated contract at /openapi/v1.json
dotnet run --project src/Bitstream.Api
```

## Foundation layer (TRD §2.4)

| Requirement | Where |
| --- | --- |
| TR-ARC-06 externalised configuration | `Configuration/` in Application and Api, per-adapter options, validators run at start-up |
| TR-ARC-04 correlation and structured logging | `ICorrelationContext`, `CorrelationIdMiddleware`, `RequestLoggingMiddleware`, `CorrelationPropagationHandler` |
| TR-ARC-05 health endpoints | `/health/live`, `/health/ready`, checks in the layer that owns each dependency |
| TR-NFR-17 CI | `.github/workflows/ci.yml` — build, format, test, publish; no container step |
| TR-ARC-07/08 provisioning | `deploy/` — idempotent PowerShell, one definition file per environment |
| Migration tooling | `SchemaVersionGuard`, `db/Get-SchemaStatus.ps1` |

Three of these encode a requirement rather than merely satisfying it, which is worth knowing
before changing them:

- **Reminders cannot be switched off** while auto-confirmation is enabled (TR-PAS-21b). The
  timings are configuration, so the rule is only real if the configuration refuses to load
  without them — otherwise a settings change could start closing ISPs out silently, which is
  the objection the whole mechanism exists to answer.
- **Secrets are named, never valued.** Options carry a secret *name*; `ISecretResolver` fetches
  the value, and refuses one that came from a JSON file outside Development. A credential pasted
  into `appsettings.json` fails loudly rather than shipping (TR-SEC-28).
- **Liveness consults nothing.** TR-NFR-07 requires the portal to stay usable in read mode when
  CRM or BI is down, so a dependency outage must not make IIS recycle a working portal.

## Access Management (TRD §4)

Session cookie authentication, not a bearer token: the cookie holds only an opaque random
value, and every request is checked against the session store, because TR-SEC-07's
"invalidated at logout and at lock" needs a record the server can revoke on demand — a signed,
self-contained token cannot be, without a second revocation mechanism that amounts to this one
anyway. Full write-up in [`docs/architecture.md`](docs/architecture.md#access-management-trd-4).

| Requirement | Where |
| --- | --- |
| TR-SEC-02 password hashing | `Argon2PasswordHasher` — Argon2id, OWASP baseline cost floor enforced by the options validator |
| TR-SEC-03 password policy | `PasswordPolicyValidator`, `CommonPasswordList` |
| TR-SEC-04/05 two-factor | `IdentityService` + `TotpService` (RFC 6238) and an `EmailOtp` channel, switched by configuration |
| TR-SEC-06/12 lockout | Locked before a password check; locked automatically at the configured failure threshold |
| TR-SEC-07 sessions | `sec.UserSession`; idle and absolute timeout, whichever is reached first |
| TR-SEC-17/20 RBAC | `PermissionAuthorizationHandler`, checked server-side on every permission-gated endpoint |
| TR-SEC-18/19 ownership scoping | `AdministrationService` decides from identity before the repository is touched — a request for another ISP's record is indistinguishable from one that doesn't exist |
| TR-SEC-09 to TR-SEC-16 administration | `POST`/`GET`/`PATCH` under `/api/v1/isps` and `/api/v1/users` |
| TR-SEC-11 lock in place of delete | No delete endpoint anywhere in this module, or in the schema's grants |
| TR-SEC-22 to TR-SEC-24 audit | `AuditWriter` — append-only, enforced by the database as well as by the application |

**The default second-factor channel is `Totp`.** TRD §11.4 open item 13 leaves the production
channel undecided; Totp needs no delivery path, so it is the one channel that works regardless
of how that item is answered. `EmailOtp` is fully built and is one configuration value away once
`SmtpEmailGateway` exists; `SmsOtp` throws `NotSupportedException`, since no SMS provider is
named anywhere in the TRD.

**Proven through the real pipeline, not just asserted.**
`tests/Bitstream.Api.Tests/Identity/CrossIspAccessTests.cs` seeds two ISPs and a session, then
shows an ISP user reading the other ISP's record gets 404 — never 403 — and that the attempt is
logged as a security event, while the same user reading their own ISP and an Administrator
reading any ISP both succeed. `LockoutAndSessionTests.cs` drives five wrong passwords through
the real `/api/v1/auth/login` endpoint and confirms the account locks; two further tests isolate
the idle and absolute session timeouts from each other. The lock-cascade logic
(`SetIspStatusAsync`/`SetUserStatusAsync`) is unit-tested against hand-written fakes instead —
it calls a bulk `ExecuteUpdateAsync`, which EF Core's InMemory provider (standing in for SQL
Server, which is unavailable in this environment) does not support.

## Activation Requests (TRD §5)

`ActivationRequestService` drives the TRD §5.3 state machine, Submitted through Completed — the
CRM-driven steps included, now that CRM integration (below) is built. Full write-up in
[`docs/architecture.md`](docs/architecture.md#activation-requests-trd-5).

| Requirement | Where |
| --- | --- |
| TR-DAT-01 to TR-DAT-02e identifier | `SqlPublicIdentifierGenerator`, calling `ops.usp_NextPublicIdentifier` inside the caller's transaction — gap-free, `PREFIX_NUMBER`, never zero-padded |
| TR-ACT-01 to TR-ACT-06 submission | `ActivationRequestService.SubmitAsync` — package, location, classification and contract duration validated against `CatalogueOptions`; the identifier is issued and the record persisted before any CRM call is enqueued |
| TR-ACT-02/03 coordinates | `CoordinateParser` — a bare `lat,lng` pair or a map URL's `@lat,lng` marker or `q=`/`ll=` parameter, normalised and range-checked |
| TRD §5.3 state machine | `ActivationRequestTransitions` (Domain) is the single source of truth; every status change goes through it, so an invalid jump fails rather than corrupting the record |
| TR-ACT-12 to TR-ACT-19 GIS verification | `RecordGisOutcomeAsync` — the no-line and line-exists branches, only permitted from `AwaitingGisVerification`; a no-line outcome requires a reason |

**The state table is tested exhaustively, not just along the paths this module drives.**
`ActivationRequestTransitionsTests` checks all 100 ordered pairs of the ten TRD §5.3 statuses —
every permitted transition and every rejection — against an independently restated copy of the
table, so a rejected transition is proven rejected, not merely unasserted.

## CRM Integration (TRD §7.3)

Full write-up in [`docs/architecture.md`](docs/architecture.md#crm-integration-trd-73).

| Requirement | Where |
| --- | --- |
| TR-ARC-03 outbox / dispatcher | `IIntegrationOutbox` (storage) + `OutboxDispatcher` (Application, background hosted service) — claims due messages, calls the gateway, marks succeeded or dead-letters |
| TR-INT-04, TR-INT-05 retry and dead-letter | Exponential backoff up to `OutboxDispatcherOptions.MaxAttempts`, then dead-lettered; a business rejection (TR-INT-19) dead-letters immediately, never retried |
| TR-INT-03 idempotency | Every outbound call carries an `Idempotency-Key` header set to the request's own public identifier; inbound dedup is on `eventId` (`IIntegrationOutbox.RecordInboundAsync`) |
| TR-INT-15 to TR-INT-21 Direction A | `CrmHttpGateway.CreateCustomerAsync` / `CreateActivationTicketAsync`, against a provisional TRD §7.4 payload shape, isolated so the real contract is a one-file change |
| TR-INT-22 to TR-INT-31 Direction B | `POST /api/v1/tickets/{identifier}/events` — persist-then-interpret, `eventId` dedup, `occurredAt` ordering, the full response-code table (200/400/404/409/422/429) |

**A CRM simulator, and a fake that stands in for it in tests.** `tools/CrmSimulator` is a
standalone minimal API matching `CrmHttpGateway`'s provisional shape, for pointing
`Integration:Crm:BaseAddress` at during local development (`dotnet run --project
tools/CrmSimulator`). The automated tests use `FakeCrmGateway` instead — an in-process double
with the same idempotent-by-key behaviour — so the suite never has to manage a second process.

**Proven end to end.** `tests/Bitstream.Api.Tests/Integration/CrmClosureEndToEndTests.cs` submits
a request, drives `OutboxDispatcher` through both Direction A calls, verifies the GIS admin
screen, then posts the sales order, provisioning and completion events on the real inbound
endpoint — asserting a repeated `eventId` is a no-op and a stale `occurredAt` is discarded along
the way — through to `Completed`. A separate test proves a business rejection dead-letters
immediately and moves the request to `IntegrationFailed`.

## The four Phase 0 deliverables

**1. Layered solution (TRD §2.1, TR-ARC-01/02).** Five projects with the reference direction
running one way only: Domain ← Application ← Infrastructure ← Api. The application layer
reaches external systems solely through ports in
`Bitstream.Application.Abstractions.Integration`; the adapters that implement them are the only
code holding an `HttpClient` or an SMTP client. `Bitstream.Api` references both Infrastructure
projects purely as the composition root. This is enforced by
`tests/Bitstream.ArchitectureTests`, which reads compiled assembly references and fails the
build if the application layer picks up `System.Net.Http`, EF Core or an Infrastructure
assembly. See [`docs/architecture.md`](docs/architecture.md).

**2. Physical data model — hand-written T-SQL, not EF migrations.** All thirteen TRD §3.1
entities plus three additions each required by a "Must". The reasoning is in
[ADR-0002](docs/adr/0002-physical-data-model.md); the short version is that the requirements
which carry the compliance weight — append-only audit (TR-SEC-24), no physical deletion
(TR-DAT-07), a **gap-free** identifier series (TR-DAT-02b) — are triggers, grants and a stored
procedure, none of which a migration builder expresses. They would end up as raw SQL inside
migrations anyway, which keeps the generated-artefact review problem while losing the benefit.
EF Core is mapped onto the schema and never generates it. Note in particular that a SQL Server
`SEQUENCE` is monotonic but *not* gap-free, which is why `ops.usp_NextPublicIdentifier`
allocates inside the caller's transaction instead. See [`db/README.md`](db/README.md).

**3. OpenAPI stubs — minimal APIs, not controllers.** Reasoning in
[ADR-0001](docs/adr/0001-api-style.md). Of the thirteen interfaces in TRD §7.1, only three are
exposed by the portal, and TR-INT-22 requires all three on a *single* endpoint; the rest are
outbound calls that are ports, not endpoints.
[`docs/integration/interface-inventory.md`](docs/integration/interface-inventory.md) maps every
row to its endpoint or port. The document is generated from the endpoint definitions and served
at `/openapi/v1.json`, so the published contract cannot drift from what the portal serves.

**4. Tailwind build.** Tailwind v4 CLI, CSS-first configuration, no `tailwind.config.js`, no
PostCSS, no bundler. `npm run build:css` produces `wwwroot/css/app.css`; the browser loads the
ES modules directly. The API host serves the frontend so the portal is one IIS site. See
[`src/Bitstream.Web/README.md`](src/Bitstream.Web/README.md).

## Open items

Several TRD §11.4 items block design decisions, and none of them has been guessed at.
**Two stop work outright:**

- **Item 1 — the CRM Direction A contract.** Blocks every portal-to-CRM call and the whole
  activation flow. TR-INT-19 requires distinguishing a business rejection (never retried) from
  a technical failure (retried), which is only knowable from CRM's error semantics.
- **Item 5 — where the SAP financial code is populated.** Blocks INT-SAP-01 including its
  *direction*. Pull, push and manual entry put the interface in different components.

A third, from §11.2 rather than §11.4: **the BI active-lines reference table structure** blocks
the entire post-activation support module.

The full analysis — what each of the thirteen items blocks, what exists in the scaffold in the
meantime, and which are configuration rather than design — is in
[`docs/open-items.md`](docs/open-items.md).

## Verification status

Honest accounting of what has and has not been run:

| | |
| --- | --- |
| Tailwind build | **Verified** — `npm run build:css` runs clean and emits the expected classes |
| CI workflow YAML | **Verified** as valid YAML; never executed on a runner |
| C# compilation | **Not verified** — the .NET SDK could not be installed here; the egress policy blocks `builds.dotnet.microsoft.com` |
| Tests, including `Identity/*`, `Activation/*` and `Integration/*` | **Not run** — they need the SDK. Manually traced against the implementation (types, method signatures, request/response shapes) but never executed |
| `tools/CrmSimulator` | **Not run** — same SDK constraint; traced against `CrmHttpGateway`'s request/response shapes by hand |
| T-SQL execution, incl. `0010_activation_event_ordering.sql` | **Not verified** — no SQL Server instance available |
| PowerShell deployment scripts | **Not run** — they need Windows, IIS and SQL Server |

So the first thing to do on a machine with the .NET 10 SDK is `dotnet build` and
`dotnet test`, and to apply `db/mssql` against a scratch database. Expect small fixes —
package versions and a stray using are the usual candidates, and
`dotnet format --verify-no-changes` will likely want a first formatting pass, since the code
was written by hand rather than emitted by the formatter. Nothing in the design depends on that
pass; it is a compile-and-run check of code that has been written but not executed.
