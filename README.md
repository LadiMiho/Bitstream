# ISP Platform — Bitstream Portal

Project scaffold for the ISP Platform (Bitstream Portal), built to
*Technical Requirements Document v1.0, August 2026*.

**Foundation, Access Management (TRD §4), Activation Requests (TRD §5), Post-Activation
Support (TRD §6) and CRM integration (TRD §7.3) are built, and the solution builds and its
tests pass in CI.** Structure, contracts, schema, configuration, logging, health, CI and
deployment are in place; authentication, 2FA, sessions, RBAC and ISP/user administration are
built; activation request submission, the GIS verification admin screen and the TRD §5.3 state
machine are built, and that state machine is driven end to end by a real (if provisional) CRM
integration — an outbox dispatcher calling CRM for customer and ticket creation, and the
inbound event API applying sales order, provisioning and completion events back; complaint
tickets, the closure handshake, the working-day auto-confirmation engine and service status
changes are built on top of it. An MVC (controllers + views) UI covers sign-in with 2FA, ISP and
user administration, and the activation request screens.

**Not built yet:** reporting (TRD §9), the SAP adapter (blocked on TRD §11.4 open item 5),
`BiGateway`'s real HTTP calls (blocked on the §11.2 BI table structure), and an audit-log
read/export API — the audit log is written but nothing reads it back. Their endpoints, where
they exist, still answer `501 Not Implemented`.

- .NET 10 (C#) backend, layered per TRD §2.1
- MSSQL, schema defined by T-SQL scripts
- MVC (controllers + views) UI, Tailwind CSS via the standalone CLI — no Node.js, no npm, no client-side router
- Windows Server / IIS, no containers anywhere

## Layout

```
Bitstream.sln
src/
  Bitstream.Domain                      entities and enums (TRD §3.1)
  Bitstream.Application                 service contracts and integration ports
  Bitstream.Infrastructure.Persistence  EF Core mapped to the physical schema
  Bitstream.Infrastructure.Integration  CRM, BI, SAP and SMTP adapters
  Bitstream.Hosting                     middleware, options and health endpoints shared by both hosts
  Bitstream.Web                         the portal site people use — MVC controllers/views plus the JSON actions they call
    Controllers/, Views/                 one controller (+ views folder) per module
    ClientAssets/app.css                Tailwind source (compiled to wwwroot/css/app.css)
  Bitstream.Api                         the integration site CRM calls — inbound events, outbound background jobs
tests/
  Bitstream.ArchitectureTests           layering rules, enforced at build time
  Bitstream.Api.Tests                   both hosts: middleware, health endpoints, config validators
    Identity/                           auth, RBAC, lockout, sessions — see below
    Activation/                         submission, state machine, GIS verification — see below
    Integration/                        CRM outbox, dispatcher, Direction A/B, end-to-end — see below
tools/
  CrmSimulator                          standalone stand-in for CRM, for local development
  tailwindcss/                          standalone Tailwind CLI binary, downloaded on demand — not committed
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

# Frontend stylesheet (downloads the standalone Tailwind CLI on first run — no Node/npm)
dotnet build src/Bitstream.Web -p:BuildFrontend=true

# Run the portal — screens at /. In Development this applies db/mssql and seeds an
# administrator on start-up (see "Running it locally" below).
dotnet run --project src/Bitstream.Web

# Run the integration host — inbound CRM events, and the background jobs that call CRM
# outbound. Generated contract at /openapi/v1.json.
dotnet run --project src/Bitstream.Api
```

## Two hosts

The solution builds **two** web applications against one database, split by audience:

| | `Bitstream.Web` | `Bitstream.Api` |
| --- | --- | --- |
| Who calls it | People — ISP users, administrators | CRM |
| Contains | MVC controllers/views, session sign-in, and the JSON actions those views fetch | The inbound CRM event API |
| Background jobs | none | outbox dispatcher, active-line sync, auto-confirmation sweep |

`AddBitstreamBackgroundJobs()` is called by the API host **only**, and exactly one deployed
host may call it: two outbox dispatchers would each claim and send the same message. The
consequence worth knowing is that the API host is not optional — deploy the portal alone and
submissions are accepted, queued, and never sent. `docs/architecture.md` has the reasoning.

## Running it locally

`dotnet run --project src/Bitstream.Web` is enough for a fresh clone. In Development, and only
when `Database:DevelopmentAutoMigrate` is true (it is, in `appsettings.Development.json`), the
host applies every `db/mssql/*.sql` script in order, stamps `ops.SchemaVersion` exactly as
`db/Deploy-Database.ps1` does, and seeds an Administrator from `Development:AdminEmail` /
`Development:AdminPassword`.

Both guards are checked — the environment must be Development *and* the flag must be true — and
no configuration value can make it run outside Development. On UAT and production
`db/Deploy-Database.ps1` remains the only supported path (TR-ARC-08).

The seeded administrator has two-factor authentication enabled, so the sign-in screen will ask
for a code. Because nothing has confirmed the secret yet, the first sign-in shows a QR code
right on `/Login` — scan it with an authenticator app and enter the code it shows, which both
confirms enrollment and signs you in; every sign-in after that just asks for the code. The host
also logs the `otpauth://` provisioning URI at start-up (warning level) as a fallback for a
browser that cannot render the QR image.

The database itself must exist first — the scripts create schemas and tables, not the database:

```sql
IF DB_ID('BitstreamPortal') IS NULL CREATE DATABASE BitstreamPortal;
```

One script, `0008_permissions.sql`, grants and denies rights to the application's service
account rather than shaping the schema. It is skipped on start-up unless
`Database:DevelopmentAppUser` names a login, because a developer connecting as an administrator
is already mapped to `dbo` and `CREATE USER ... FOR LOGIN` fails for them. Its `DENY` rules are
therefore not in force locally — verify those on UAT through `db/Deploy-Database.ps1`, which is
the path that always applies it.

To run the database step by hand instead, set `Database:DevelopmentAutoMigrate` to false and
use:

```powershell
./db/Deploy-Database.ps1 -ServerInstance . -Database BitstreamPortal -AppUser 'DOMAIN\svc_bitstream_dev'
```

## Foundation layer (TRD §2.4)

| Requirement | Where |
| --- | --- |
| TR-ARC-06 externalised configuration | `Configuration/` in Application and Hosting, per-adapter options, validators run at start-up |
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
| TR-SEC-09 to TR-SEC-16 administration | `POST`/`GET`/`PATCH` on `IspsController` (`/AccessManagement/Isps`) and `UsersController` (`/AccessManagement/Users`) |
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
the real `AuthController.Login` action and confirms the account locks; two further tests isolate
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

## Post-Activation Support (TRD §6)

Full write-up in [`docs/architecture.md`](docs/architecture.md#post-activation-support-trd-6).

| Requirement | Where |
| --- | --- |
| TR-PAS-01 to TR-PAS-07 BI active-lines sync | `ActiveLineSyncService`, scheduled by `ActiveLineSyncScheduler` (default hourly) and triggerable manually via `OperationsController.TriggerActiveLineSync` (`POST /Operations/bi/active-lines/sync`); upserts on `(IspId, ContractId)` so a repeated page is idempotent, and `ops.SyncState` tracks the change marker and consecutive-failure count for `TR-PAS-07`'s freshness reporting |
| TR-PAS-08 to TR-PAS-12 complaint tickets | `ComplaintTicketService.CreateAsync` — three-level category validated against `CatalogueOptions.ComplaintCategories`, identifier issued, INT-CRM-04 enqueued on the outbox |
| TR-PAS-13 to TR-PAS-17 status suppression | `InboundEventService.ApplyToComplaintTicketAsync` — a status update is applied always, but the ISP is notified only when the incoming status is `Technically Completed` or is in the configured `IspNotifiableStatuses` list; an internal forward (`ForwardingGroup` set) never notifies |
| TRD §6.4 closure handshake | `TicketClosureService.ApplyClearingCodeAsync` (CRM-driven, sets the ticket `Pending ISP Confirmation` and the working-day-out `ConfirmationDueAt`) and `RecordIspDecisionAsync` (Confirm closes; No reopens) |
| TR-PAS-21 to TR-PAS-21h auto-confirmation | `TicketClosureService.RunAutoConfirmationSweepAsync`, run by `AutoConfirmationSweepScheduler`: reminders at working day 2 and 4 (`Reminder2SentAt`/`Reminder4SentAt`), auto-confirm at working day 5 recorded as `ClosureDecision.AutoConfirmed` — distinct from `ClosureDecision.Confirmed` — and a 10-calendar-day challenge window (`RaiseFollowUpAsync`) off the closed date. Working-day arithmetic is `WorkingDayCalculator`, driven by `WorkingCalendarOptions` |
| §6.6 comments | `ComplaintTicketService.AddCommentAsync`, replicated to CRM via INT-CRM-06 |
| §6.7 complaints dashboard | `ComplaintTicketService.SearchAsync` behind `TicketsController.Search` (`GET /PostActivation/Tickets/Search`), ISP-scoped unless the caller holds `TicketReadAll` |
| §6.8 service status management | `ServiceChangeRequestService` — Upgrade/Downgrade eligibility computed from `CatalogueOptions.Packages` tiers (as-is line package vs. eligible to-be packages), Termination validated against a future date with no to-be package; replicated via INT-CRM-09 |

**The auto-confirmation timing is proven by advancing a fake clock, not by inspection.**
`tests/Bitstream.Api.Tests/PostActivation/TicketClosureServiceTests.cs` anchors on a Monday,
drives `WorkingDayCalculator` for real (not faked) against a Mon–Fri calendar with no holidays,
and asserts each reminder fires exactly once at its working-day threshold, auto-confirmation
lands exactly on working day 5 (skipping the intervening weekend) with a `ClosureDecision`
distinct from an ISP confirmation, and a persisted ISP decision pre-empts the sweep even after
the due date has passed. `ActiveLineSyncServiceTests.cs` separately proves the BI sync's
upsert-not-duplicate idempotency (TR-PAS-04) and its unknown-Business-Partner and failure
handling.

**Known scope cuts, both documented in code:** `BiGateway`'s real HTTP implementation stays a
stub — the BI reference-table structure is a genuinely unresolved external dependency (TRD
§11.2), and `ActiveLineSyncService` is fully exercised through `FakeBiGateway` regardless.
`TicketComment.CrmSyncStatus` is not updated back to `Sent` when the outbox dispatch of
INT-CRM-06 succeeds; a real status-callback loop is future work.

## The four Phase 0 deliverables

**1. Layered solution (TRD §2.1, TR-ARC-01/02).** Seven projects with the reference direction
running one way only: Domain ← Application ← Infrastructure ← Hosting ← the two hosts. The
application layer
reaches external systems solely through ports in
`Bitstream.Application.Abstractions.Integration`; the adapters that implement them are the only
code holding an `HttpClient` or an SMTP client. `Bitstream.Web` and `Bitstream.Api` reference
both Infrastructure projects purely as composition roots; nothing below them references
`Bitstream.Hosting`, so it cannot become a back door into ASP.NET Core. This is enforced by
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

**4. Tailwind build.** Tailwind v4, CSS-first configuration, no `tailwind.config.js`, no
PostCSS, no bundler, and no Node.js or npm anywhere in the project: the standalone Tailwind CLI
— a single self-contained native binary — is downloaded on demand and run as a plain MSBuild
`Exec` step (`Bitstream.Web.csproj`, gated behind `-p:BuildFrontend=true` so an ordinary backend
build never touches the network). It compiles `ClientAssets/app.css` to `wwwroot/css/app.css`.
The UI itself is MVC controllers and views under `src/Bitstream.Web/Controllers`/`Views` — one
controller per module, a shared `_Layout.cshtml` for the header/nav/content-area chrome, and the
auth-guard implemented as `RequireSessionAttribute`/`RequirePermissionAttribute`
(`Security/MvcAuthorization.cs`), applied per controller/action rather than a client-side
redirect. There is no client-side router: navigation is ordinary page requests, and
JavaScript, where used at all, is for behaviour (fetch calls, form feedback) only. The screens
and the endpoints they fetch are served by the same host, so the session cookie works with no
CORS configuration.

## Open items

Several TRD §11.4 items block design decisions, and none of them has been guessed at.
**Two stop work outright:**

- **Item 1 — the CRM Direction A contract.** Blocks every portal-to-CRM call and the whole
  activation flow. TR-INT-19 requires distinguishing a business rejection (never retried) from
  a technical failure (retried), which is only knowable from CRM's error semantics.
- **Item 5 — where the SAP financial code is populated.** Blocks INT-SAP-01 including its
  *direction*. Pull, push and manual entry put the interface in different components.

A third, from §11.2 rather than §11.4: **the BI active-lines reference table structure** blocks
`BiGateway`'s real HTTP implementation. The rest of post-activation support (TRD §6) does not
depend on it and is built — see below.

The full analysis — what each of the thirteen items blocks, what exists in the scaffold in the
meantime, and which are configuration rather than design — is in
[`docs/open-items.md`](docs/open-items.md).

## Verification status

Honest accounting of what has and has not been run:

| | |
| --- | --- |
| C# compilation | **Verified** — `dotnet build Bitstream.sln -c Release` succeeds on the CI runner (Windows Server 2025, .NET SDK 10.0.400) with 0 warnings and 0 errors, warnings-as-errors and `EnforceCodeStyleInBuild` both on |
| Lint / formatting | **Verified** — `dotnet format --verify-no-changes --severity warn` is clean |
| Tests — `Identity/*`, `Activation/*`, `Integration/*`, `PostActivation/*` | **Verified** — 281 pass, 0 fail |
| `Bitstream.ArchitectureTests` (the TR-ARC-01 layering rules) | **Verified** — 9 pass, 0 fail |
| CI workflow | **Verified** — executes green end to end on a runner, through to publishing the artifact |
| Tailwind build | **Verified** — the standalone CLI runs clean against `ClientAssets/app.css` and emits the expected classes, including from the `.cshtml` sources |
| Gap-free identifier series (TR-DAT-02b) | **Not verified** — it lives in `ops.usp_NextPublicIdentifier`, so it needs a real SQL Server. Every test path either fakes `IPublicIdentifierGenerator` or never reaches it, so no automated test proves the *gap-free* property |
| T-SQL execution, incl. `0011_post_activation_support.sql` | **Not verified** — no SQL Server instance available to CI or to the authoring environment |
| `tools/CrmSimulator` | **Compiles**, but never run against `CrmHttpGateway` — the automated tests substitute `FakeCrmGateway` instead |
| PowerShell deployment scripts | **Not run** — they need Windows, IIS and SQL Server |
| The Razor UI in a browser | **Not verified** — the pages compile and their scripts were exercised against a mocked API in a headless browser, but no run against the real backend |

The remaining gaps all share one cause: **there is no SQL Server anywhere in the loop.** That is
what leaves the schema scripts, the deployment scripts and the gap-free identifier series
unproven, and it is the next thing worth fixing — a SQL Server service container in the CI job
would close all three at once.
