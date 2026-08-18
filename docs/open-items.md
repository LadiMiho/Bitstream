# Open items from TRD §11.4 — what they block

Read this before starting a module. Items are grouped by what they actually stop, not by
their number. Nothing below has been guessed at: where an answer is missing, the scaffold
carries a port, a nullable column or a configuration key and stops there.

## Blocking now — work cannot start

### Item 1 — CRM contract for ticket creation (Direction A)
*Endpoint, authentication, field mapping and error semantics. From: CRM team.*

**Still blocks:** INT-CRM-04, -06 (outbound half), -08, -09 — complaint ticket creation, comment
replication, closure decision and service change all still throw `NotSupportedException`; they
belong to modules (complaint tickets, service changes) that are not built yet, and there is
nothing to map their fields against regardless. TR-INT-21's signed-off field mapping — the actual
deliverable this item blocks — does not exist for any of the six operations, provisional or not.

**No longer blocks starting work on activation requests.** INT-CRM-01 (create customer) and
INT-CRM-02 (create activation ticket) are implemented in `CrmHttpGateway`, dispatched from the
outbox by `OutboxDispatcher`, against a *provisional* payload shape following TRD §7.4's field
list rather than a signed-off mapping. This is a deliberate bet: build against the best available
guess, isolated behind `ICrmGateway` so that adopting the real contract is a change to one file's
request/response records (and `AuthorizeAsync`, if the auth scheme differs), not a redesign. It
does **not** resolve TR-INT-21 — the mapping still needs both teams' sign-off before this goes
live — and TR-INT-19's business-rejection/technical-failure split is implemented against a guess
(4xx vs. everything else) that may not match CRM's actual error semantics.

**What exists:** `ICrmGateway.CreateCustomerAsync`/`CreateActivationTicketAsync` implemented;
`ICrmGateway`'s other four methods still throw. `IIntegrationOutbox` (enqueue, claim, mark
succeeded or failed, replay) and `OutboxDispatcher` (the background hosted service that drains
it, with exponential backoff and dead-lettering, TR-INT-04/05) are both built and used by every
target system, not CRM alone. `tools/CrmSimulator` stands in for CRM locally, honouring the same
`Idempotency-Key` header `CrmHttpGateway` sends (TR-INT-03/17), so the provisional shape is
exercisable end to end without a real CRM endpoint to call.

### Item 5 — Where the SAP financial code is populated
*From: Wholesale / Finance.*

**Blocks:** INT-SAP-01 entirely — including its *direction*. Whether the portal pulls the code
from SAP, SAP pushes it to the portal, or a Wholesale user enters it changes which component
owns the interface, whether the inbound event API needs another event type, and where in the
flow the value appears.

**Why it is not workable around.** This is not a parameter, it is a topology question. Picking
one and building it means a rewrite if the answer differs, and the requirement explicitly says
the technical design cannot be finalised until it is decided (TRD §7.5).

**What exists:** `ActivationRequest.FinancialCode`, nullable and indexed (TR-INT-11,
TR-INT-13); `ISapGateway` declared as a pull and disabled by configuration; and the guarantee
that its absence blocks nothing (TR-INT-14). All four SAP requirements hold whichever option is
chosen.

## Blocking the module they belong to

### Item 3 — Inbound API authentication and CRM source IP ranges
*Mutual TLS or signed token. From: CRM team / Security.*

**Blocks:** production use of `POST /api/v1/tickets/{identifier}/events`. The endpoint shape,
payload, dedup, ordering and response codes are all defined by the TRD and are built; the
authentication is not, because mutual TLS and a signed bearer token differ in certificate
provisioning, rotation procedure and IIS configuration — not in code shape.

**Consequence if left open:** the interface can be developed and stubbed, but cannot be
enabled in UAT with CRM, and TR-INT-23 (reject and log unauthenticated callers) cannot be
tested end to end.

### Item 4 — Complete CRM status and event type list, and which statuses are ISP-notifiable
*From: CRM team / Wholesale.*

**Blocks:** status projection (TR-PAS-13 to TR-PAS-17), notification suppression (TR-INT-28)
and the complaints dashboard filters (TR-PAS-31).

**Why it is visible in the schema.** `ComplaintTicket.Status` deliberately has **no** CHECK
constraint and is a string code, not an enum — TR-PAS-16 requires the notifiable set to be
configurable, and a constraint would have to be altered every time CRM adds a status. Unknown
values are rejected at the API with 422 and surfaced to the administrator (TR-INT-27) rather
than being silently mapped to something plausible. `ActivationRequest.Status` *is* constrained,
because TRD §5.3 defines that state machine completely.

### Item 8 — Three-level defect category catalogue and its CRM mapping
*From: Service Desk / CRM.*

**Blocks:** the complaint ticket form (TRD §6.2). The cascade needs the actual hierarchy;
TR-PAS-08 requires categories to map one-to-one on both sides, which cannot be verified against
a catalogue that has not been supplied.

**What exists:** `CategoryL1/L2/L3` columns; the catalogue is configuration or a CRM sync job
(TR-PAS-09), so no code change is needed when it arrives.

### Item 6 — FM and FM Contractor distribution lists · Item 7 — Sales order email sample
*From: FM · Wholesale.*

**Blocks:** the sales order notification (TRD §8.1) being *correct*, not the notification
machinery. Recipients are configured distribution groups (TR-NTF-02) — the keys exist in
`appsettings.json` with empty lists — and the content comes from an external template
(TR-NTF-01). Both are data, so the notification service can be built and tested against a
placeholder template; it just must not go live against one.

### Item 13 — 2FA delivery channel for production
*From: Security.*

**Blocks:** which channel is live in production, not the login flow itself any more — the
Access Management module (TRD §4) is now built against all three, and `Security:TwoFactor:Channel`
switches between them without a code change (TR-ARC-06). TOTP, SMS OTP and email OTP differ in
enrolment, recovery and in what has to be stored — `User.TotpSecret` exists for the TOTP case
and stays null otherwise. TR-SEC-05 also forbids production falling back to a weaker channel
than the configured one, which needs a channel to be configured first.

**What exists:** `Totp` (RFC 6238, `TotpService`) is fully implemented and is the scaffold
default, precisely because it needs no delivery channel and so is not itself blocked by this
item. `EmailOtp` is fully implemented and switches on with one configuration value, but is only
actually usable once `SmtpEmailGateway` is built (open item 1's sibling gap — SMTP has no
adapter contract of its own to be blocked on, it is simply not written yet). `SmsOtp` throws
`NotSupportedException`: no SMS provider is named anywhere in the TRD, so there is nothing to
implement against yet, consistent with how CRM and SAP are left unimplemented pending their own
open items. Whichever channel is confirmed, provisioning a user for it is already wired
(`AdministrationService.CreateUserAsync` generates and encrypts a TOTP secret at creation time
when the configured channel is `Totp`).

## Not blocking design — configuration or content

### Item 2 — The identifier prefix, production and non-production
The TRD settles this itself: TRD §3.2 states the prefix is a configuration value and "does not
affect the design". `ops.PublicIdentifierSeries` is seeded with placeholders (`ISP`, `TKT`,
`SCR`) and the value is set per environment. **It does block go-live**, because TR-DAT-02a
requires the prefix to be identical across portal, CRM, BI and SAP for a given environment, and
TR-DAT-02e requires a distinct non-production prefix.

### Item 9 — Approval of the auto-confirmation mechanism
The mechanism in TRD §6.5 is fully specified, and TR-PAS-21a/b require the period and the
reminder points to be configurable anyway. Built against the proposed values — 5 working days,
reminders at day 2 and day 4, 10-day challenge window — which live in
`appsettings.json:TicketClosure`. If Wholesale changes the numbers, that is a configuration
change. If they reject the mechanism outright, TR-PAS-21 loses its answer and the question
reopens.

### Item 10 — Retention periods for personal data, audit logs and integration messages
Does not block the schema: TR-DAT-10 already sets a 24-month floor and requires archival rather
than purging. It does block **writing the archival job**, so no archive tables and no purge job
are created. The application account is denied DELETE everywhere; the archival job will run
under a DBA account, and that should stay true.

### Item 11 — Required interface languages
Does not block the frontend build. It blocks choosing a localisation approach, so no
localisation framework has been added and no resource files exist yet (TR-NFR-13, a *Should*).
Labels are currently inline in the markup — they need extracting before the answer arrives, not
after.

### Item 12 — Whether attachments are in scope for Release 1
Does not block anything already built. There is no attachment column, no upload endpoint and no
malware-scanning integration, because TRD §6.2 lists attachments as optional and this item asks
whether they are in Release 1 at all. `web.config` carries a 30 MB request limit as a
placeholder. If the answer is yes, this adds an entity, an endpoint and a scanning dependency —
worth knowing before the sprint that includes complaint ticket creation.

## Dependencies from §11.2 that behave like open items

**The BI active-lines reference table structure** (BI team) is listed as a dependency rather
than an open item, but it blocks INT-BI-01 exactly the way item 1 blocks CRM: without the
structure and access method there is nothing to map. The whole post-activation support module
depends on it — no line selection means no complaint ticket. `IBiGateway` and
`portal.ActiveLine` are shaped for either a REST endpoint or a read-only view.

## Summary

| Item | Blocks | Can work start? |
| --- | --- | --- |
| 1 CRM Direction A contract | Complaint/service-change CRM calls; the real, signed-off mapping | Activation's two calls: built provisional. Rest: **No** |
| 5 SAP financial code population point | INT-SAP-01, its direction | **No** |
| 3 Inbound API authentication | Production use of the event API | Endpoint yes, enablement no |
| 4 CRM status / event type list | Status projection, notifications, dashboard | Partly |
| 8 Category catalogue | Complaint ticket form | Partly |
| 6, 7 Recipients and email sample | Correct notifications | Yes, not go-live |
| 13 2FA channel | Which channel is live in production | Built (Totp default), yes |
| 2 Identifier prefix | Go-live only | Yes |
| 9 Auto-confirmation approval | Nothing; configurable | Yes |
| 10 Retention periods | Archival job | Yes |
| 11 Interface languages | Localisation approach | Yes |
| 12 Attachments in Release 1 | Attachment feature | Yes |
| §11.2 BI reference table | Post-activation support module | **No** |
