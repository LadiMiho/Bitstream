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
Bitstream.Hosting                    middleware, options, health — shared by both hosts
        ^                    ^
Bitstream.Web                        Bitstream.Api
(the portal people use)              (the interface CRM uses)
```

| Project | References | Layer (TRD §2.1) | May contain |
| --- | --- | --- | --- |
| `Bitstream.Domain` | none | — | Entities, enums, invariants. No EF Core, no HTTP, no configuration. |
| `Bitstream.Application` | Domain | Application services | Service interfaces, integration **ports**, DTOs. No adapter, no `HttpClient`, no `DbContext`. |
| `Bitstream.Infrastructure.Persistence` | Application, Domain | Persistence | `BitstreamDbContext`, entity configurations, repositories. |
| `Bitstream.Infrastructure.Integration` | Application, Domain | Integration adapters | The only place with an `HttpClient`, an SMTP client or a vendor SDK. |
| `Bitstream.Hosting` | Application | Presentation (shared) | Correlation and request-logging middleware, secret resolver, options validator, rate-limit options and policy names, health endpoints, claim types. |
| `Bitstream.Web` | all of the above | Presentation | MVC controllers and views, session authentication, RBAC, and the JSON actions the screens call. Composition root for the portal. |
| `Bitstream.Api` | all of the above | Presentation | The CRM-facing inbound event API, and the background jobs that call CRM outbound. Composition root for the integration host. |

### Why two hosts

The split is by **audience**, not by subject: people use `Bitstream.Web`, machines use
`Bitstream.Api`. Everything else follows from that.

- The portal calls the application services **in process**. There is no HTTP hop between a
  screen and the domain, so there is no second authorisation surface to keep in step and no
  serialisation boundary that exists only because the code lives in two folders.
- The only interface another system consumes is the CRM one (TRD §7.1 exposes just
  INT-CRM-03, -05 and -07, all on the single inbound endpoint TR-INT-22 requires). That is
  the whole of `Bitstream.Api`, which is why the generated OpenAPI contract lives there and
  describes only that.
- They deploy as separate IIS sites with separate application pools, so only the API host
  needs to be reachable from CRM, and either can be restarted without the other.

**Exactly one host runs the background jobs.** `AddBitstreamApplication` registers the
services; `AddBitstreamBackgroundJobs` starts the outbox dispatcher, the BI active-lines sync
and the auto-confirmation sweep, and only `Bitstream.Api` calls it. This is a correctness
requirement, not tidiness: the jobs are not idempotent against a concurrent copy of
themselves, so two hosts running them would each claim and send the same outbox message and
each auto-confirm the same ticket. The consequence is worth stating plainly — **outbound CRM
traffic needs the API host deployed.** A portal-only deployment accepts submissions and
queues them, and they sit on the outbox until something drains it.

The API host has no signed-in user, so it registers `SystemCurrentUserContext`: every identity
property null and `HasPermission` always false. A system-initiated change is recorded in the
audit log as exactly that (TR-SEC-22) rather than attributed to whichever user was nearby, and
a background job can never pass an ownership check by pretending to be privileged.

`Bitstream.Web` and `Bitstream.Api` each reference both Infrastructure projects so that
`Program.cs` can call their registration extensions. That is the composition root and nothing
else: no endpoint or page uses an adapter or a `DbContext` type directly.

**This is enforced, not documented.** `tests/Bitstream.ArchitectureTests/LayeringTests.cs`
reads the compiled assembly references — which the compiler emits only for assemblies a
project actually uses — and fails the build if the application layer picks up
`System.Net.Http`, `Microsoft.EntityFrameworkCore` or either Infrastructure assembly, or if a
port is implemented outside the integration layer.

The split between the two hosts is enforced the same way, because a split that lives only in a
document is one refactoring away from gone:

- neither host may reference the other — anything genuinely common belongs in
  `Bitstream.Hosting`, which both already reference;
- `Bitstream.Hosting` may not reference an Infrastructure project, which would make it a second
  composition root and let the two hosts disagree about how the platform is wired;
- `Bitstream.Api` may not reference MVC views, since a screen served from the https-only site
  that only CRM's source ranges can reach is a screen nobody can use.

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
| Correlation ID | `Bitstream.Hosting.Middleware.CorrelationIdMiddleware` — accepts an inbound ID, echoes it, puts it in the log scope | TR-ARC-04 |
| Rate limiting | `RateLimitPolicies` (Hosting), registered per host and applied per endpoint group | TR-SEC-29, TR-INT-30 |
| Health | `/health/live` (process only) and `/health/ready` (dependencies) | TR-ARC-05 |
| Audit | `AuditWriter` (Persistence, implementing `IAuditWriter`) — the only write path; the table takes no update or delete | TR-SEC-22 to TR-SEC-24 |
| Configuration | `appsettings.json` plus per-environment overrides; secrets from the secret store | TR-ARC-06, TR-SEC-28 |

`/health/live` deliberately consults no dependency: a CRM outage must not make IIS recycle a
portal that is still usable in read mode (TR-NFR-07).

## Access management (TRD §4)

The first fully implemented module — every other application service is still a stub. Session
cookie authentication, not a bearer token: `SessionAuthenticationHandler`
(`Bitstream.Web.Security`) resolves the cookie against `IIdentityService.ValidateSessionAsync`
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

### Access management screens (GUI-3) and the API gaps they surfaced

`Views/Auth/Login.cshtml` (the two-factor sign-in flow, rendered by `AuthController.LoginPage`) and
`Views/AccessManagement/Index.cshtml`, `Views/{Isps,Users}/Index.cshtml`, `Views/AccessManagement/AuditLog.cshtml`
call the endpoints above from client-side script (`wwwroot/js/pages/*.js`, via the shared fetch
wrapper `wwwroot/js/api-client.js`) — nothing server-side in these views re-implements
authentication, validation or the lock/unlock decision; `RequireSessionAttribute`/permission claims
only decide what to *show* (TR-SEC-17), never what the API actually allows. Building these screens
against the real endpoints originally surfaced gaps in what TRD §4's backend exposed; the search/
list and update gaps have since been closed (`UsersController`/`IspsController`'s `Search` and
`Update` actions, backed by `AdministrationService`) — the one still open:

- **No audit log read path at all.** `IAuditWriter` only writes; `audit.read` is seeded and
  granted to the Auditor and Administrator roles, but no service method or endpoint reads an
  audit entry back. `Views/AccessManagement/AuditLog.cshtml` stays an explanatory placeholder
  rather than querying the database directly or otherwise reimplementing that read path in the
  frontend.

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

### Activation request screens (GUI-4)

`Views/ActivationRequests/Index.cshtml` (rendered by `ActivationRequestsController`) is a
browsable, searchable, filterable grid — the same pattern as User/ISP Administration
(`wwwroot/js/pages/activation-admin.js`, mirroring `user-admin.js`/`isp-admin.js`) — with drawer
forms for submitting a new request and, from an eligible row, recording the GIS verification
outcome. `IActivationRequestRepository.SearchAsync`/`ActivationRequestService.SearchAsync` back
the grid with the same ownership scoping as everywhere else (`activation.read.all` sees every
ISP's requests; anyone else sees only their own). Submission validation, coordinate parsing,
state-machine enforcement and the GIS outcome decision all stay entirely server-side; the views
only render what the API returns.

**Confirmed rather than assumed: a request is visible with a `PendingCrmSync`-style status
before CRM integration is "live".** `ActivationRequestService.SubmitAsync` enqueues INT-CRM-01
on the outbox and transitions `Submitted` → `PendingCrmSync` *synchronously, in the same call*,
before returning — the CRM call itself is made later by the out-of-process `OutboxDispatcher`.
So the `201 Created` response, and every subsequent `GET`, already shows `PendingCrmSync`
regardless of whether `Integration:Crm:BaseAddress` is configured or the dispatcher has run at
all. The grid and its view drawer surface every status verbatim, integration-pending ones
included (TR-ACT-11) — `wwwroot/js/status-presentation.js` only maps a label and a colour, it
does not decide which status a request is in.

Activation Requests has the same searchable/filterable/paged grid as Access Management
(`ActivationRequestsController.Search`, `IActivationRequestRepository.SearchAsync`), so the GIS
verification screen has a queue of requests currently `AwaitingGisVerification` to work from
rather than requiring the public ID in hand. The submission form's package, classification and
contract duration fields are dropdowns sourced from `IActivationCatalogueRepository`
(`db/mssql/0017_activation_catalogues.sql`, tables `portal.Package`/`ActivationClassification`/
`ContractDuration`) rather than `appsettings.json:Catalogues` — TR-ACT-01/TR-ACT-04's
"extensible without a release" is satisfied by editing the tables directly, with no redeploy or
process restart needed.

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
`ActivationRequestService` method after trying an activation-request lookup first; if the
identifier instead resolves to a complaint ticket, `STATUS_CHANGED`, `COMMENT_ADDED`,
`CLOSED_WITH_CLEARING_CODE`, `AUTO_COMPLETED` and `REOPENED` route to
`InboundEventService.ApplyToComplaintTicketAsync` (TRD 6, below). Any other event type is rejected
422 rather than silently accepted (TR-INT-27) — the full event vocabulary is TRD 11.4 open item 4
regardless.

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

## Post-activation support (TRD 6)

**BI active-lines sync (TR-PAS-01 to TR-PAS-07).** `ActiveLineSyncService.SynchroniseAsync` pages
through `IBiGateway.GetActiveLinesAsync`, resuming from the change marker stored in
`ops.SyncState` unless a full reload is requested. Each record resolves its ISP by CRM Business
Partner reference (`IIspRepository.FindByCrmBpReferenceAsync`); an unresolvable BP is skipped and
logged rather than failing the run, since one bad row should not block the rest of the page.
Upsert is keyed on `(IspId, ContractId)`, so a repeated page updates the one row instead of
duplicating it — the idempotency TR-PAS-04 requires. `ActiveLineSyncScheduler` runs this on a
configurable interval (`ActiveLineSyncOptions.SyncInterval`, default hourly); an administrator can
also trigger it on demand via `OperationsController.TriggerActiveLineSync`
(`POST /Operations/bi/active-lines/sync`), and read freshness back via `GET .../sync/status`,
which is `ops.SyncState.LastSuccessfulSyncAt` and the consecutive-failure
count TR-PAS-07 asks be monitored. `ActiveLineSyncOptions` (Application) and `BiOptions`
(Infrastructure.Integration) deliberately bind to the *same* `Integration:Bi` configuration
section rather than one referencing the other's type — the Application layer cannot reference
Infrastructure (TR-ARC-01), so it declares its own narrow view of the fields it needs.

**Complaint tickets (TR-PAS-08 to TR-PAS-12).** `ComplaintTicketService.CreateAsync` validates
that the line belongs to the caller's ISP, validates the three-level category against
`CatalogueOptions.ComplaintCategories` (skipped, not rejected, when the catalogue is empty — the
real list is TRD 11.4 open item 8, and refusing every ticket until it arrives would be worse than
not validating), issues the identifier and enqueues INT-CRM-04 for CRM replication. Comments
(§6.6) follow the same shape: `AddCommentAsync` persists the comment and enqueues INT-CRM-06 with
a composite idempotency key (`{ticketPublicId}#comment-{commentId}`) so replication is per-comment
idempotent, not just per-ticket. `SearchAsync` (§6.7 dashboard) forces ISP scoping unless the
caller holds `ticket.read.all`; `GetByPublicIdAsync` follows the TR-SEC-19 not-found-not-forbidden
pattern already used for activation requests.

**Status suppression (TR-PAS-13 to TR-PAS-17).** `InboundEventService.ApplyToComplaintTicketAsync`
applies every incoming `STATUS_CHANGED` event to the ticket's `Status` unconditionally, but only
queues an ISP notification when the new status is `Technically Completed` (hard-coded — TRD 6.3
requires it always notify) or appears in the configured `IspNotifiableStatuses` list, and never
when the event carries a `ForwardingGroup` (an internal forward). `IspNotifiableStatuses` defaults
to empty, which is the safe default until TRD 11.4 open item 4 supplies the real CRM status
vocabulary: nothing is notifiable by guess.

**The closure handshake (TRD 6.4).** A `CLOSED_WITH_CLEARING_CODE` inbound event calls
`TicketClosureService.ApplyClearingCodeAsync`, which stores the clearing code and text, moves the
ticket to `Pending ISP Confirmation`, and computes `ConfirmationDueAt` once, at this moment, as
five working days out (`IWorkingDayCalculator.AddWorkingDays`) — not recomputed on every sweep
pass. `RecordIspDecisionAsync` accepts only `Confirmed` or `Rejected` from the ISP: Confirmed
closes the ticket and replicates the decision (INT-CRM-08); Rejected reopens it and clears the due
date, since there is nothing left to auto-confirm.

**Auto-confirmation (TR-PAS-21 to TR-PAS-21h).** `AutoConfirmationSweepScheduler` runs
`TicketClosureService.RunAutoConfirmationSweepAsync` on a configurable interval. For every ticket
awaiting confirmation it sends the day-2 and day-4 reminders exactly once each
(`Reminder2SentAt`/`Reminder4SentAt` — two dedicated columns, not a general collection, trading
configurability past two reminder points for schema simplicity), then re-fetches the ticket
immediately before deciding whether to auto-confirm, so a decision the ISP recorded between the
sweep's query and this point is never overwritten — **a persisted ISP decision always wins**.
Auto-confirmation at working day 5 sets `ClosureDecision.AutoConfirmed`, a value distinct from
`ClosureDecision.Confirmed`, satisfying TR-PAS-21c/e by construction rather than by convention.
`RaiseFollowUpAsync` implements the 10-*calendar*-day challenge window off the ticket's `ClosedAt`
(TR-PAS-21f is explicit that this window is calendar days, unlike every other duration in this
module) by creating a new ticket with `ParentTicketId` set.
`tests/Bitstream.Api.Tests/PostActivation/TicketClosureServiceTests.cs` proves this timing by
advancing a fake clock against a real `WorkingDayCalculator`, anchored on a Monday so the weekend
crossing is exercised rather than assumed.

**Service status management (TRD 6.8).** `ServiceChangeRequestService.GetEligibleTargetPackagesAsync`
computes Upgrade as active packages with a higher `Tier` than the line's current package (ascending)
and Downgrade as lower-tier packages (descending) — the as-is/to-be logic is data (`CatalogueOptions.
Packages[].Tier`), not a hard-coded table. `SubmitAsync` requires a to-be package and no termination
date for Upgrade/Downgrade, and a future termination date with no to-be package for Termination;
replicated via INT-CRM-09.

## Deliberate gaps

`BiGateway`'s real HTTP implementation is still a stub — the BI reference-table structure (TRD
§11.2) is a genuinely unresolved external dependency, and `ActiveLineSyncService` is fully
exercised through `FakeBiGateway` regardless, so nothing about the sync service itself is
unproven. `TicketComment.CrmSyncStatus` stays `Pending` after INT-CRM-06 dispatches successfully;
correlating a dispatched outbox message back to the specific comment row it replicated would need
the idempotency key parsed for the comment id it encodes, and a full status-callback loop is
future work. `GetReconciliationReport` (TR-INT-10) is still a 501 stub — out of scope for this
turn. Reporting is still unbuilt entirely. See [`open-items.md`](open-items.md) for which TRD
§11.4 answers are needed before which remaining piece can be built.
