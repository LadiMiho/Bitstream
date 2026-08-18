# Interface inventory — TRD §7.1

Every row of TRD §7.1 mapped to what represents it in the scaffold. The distinction that
matters: an interface the portal **exposes** is an HTTP endpoint and appears in
`/openapi/v1.json`; an interface the portal **calls** is a port plus an adapter, and its
contract belongs to the other system.

| TRD §7.1 | Direction | Represented by | OpenAPI | Blocked by |
| --- | --- | --- | --- | --- |
| INT-CRM-01 Create Customer | Portal → CRM | `ICrmGateway.CreateCustomerAsync` — **implemented** against a provisional payload shape, dispatched from the outbox by `OutboxDispatcher` | no — outbound | Real contract: Open item 1 |
| INT-CRM-02 Create Activation Ticket | Portal → CRM | `ICrmGateway.CreateActivationTicketAsync` — **implemented**, enqueued by the dispatcher once INT-CRM-01's Business Partner is known | no — outbound | Real contract: Open item 1 |
| INT-CRM-03 Sales Order Notification | CRM → Portal | `POST /api/v1/tickets/{identifier}/events`, `SALES_ORDER_OPENED` — **implemented** for activation requests (TR-ACT-18) | **yes** | Auth: open item 3 |
| INT-CRM-04 Create Complaint Ticket | Portal → CRM | `ICrmGateway.CreateComplaintTicketAsync` — **implemented** against a provisional payload shape, dispatched from the outbox on `ComplaintTicketService.CreateAsync` | no — outbound | Real contract: Open item 1 |
| INT-CRM-05 Ticket Lifecycle Events | CRM → Portal | `POST /api/v1/tickets/{identifier}/events` — activation-relevant types (`PROVISIONING_STARTED`, `TECHNICALLY_COMPLETED`) and complaint-ticket types (`STATUS_CHANGED`, `COMMENT_ADDED`, `CLOSED_WITH_CLEARING_CODE`, `AUTO_COMPLETED`, `REOPENED`) both **implemented**, routed by `InboundEventService` to whichever entity the identifier resolves to | **yes** | Auth: open item 3; full vocabulary: open item 4 |
| INT-CRM-06 Comment Replication | Bidirectional | Out: `ICrmGateway.ReplicateCommentAsync` — **implemented**, enqueued per-comment by `ComplaintTicketService.AddCommentAsync`. In: `COMMENT_ADDED` on the events endpoint — **implemented** | **yes** | Real contract: Open item 1 |
| INT-CRM-07 Closure / Clearing Code | CRM → Portal | `CLOSED_WITH_CLEARING_CODE` on the events endpoint — **implemented**, calls `TicketClosureService.ApplyClearingCodeAsync` | **yes** | Auth: open item 3 |
| INT-CRM-08 Closure Decision | Portal → CRM | `ICrmGateway.SubmitClosureDecisionAsync` — **implemented**, enqueued by `TicketClosureService` on an ISP confirm/reject or an auto-confirmation | no — outbound | Real contract: Open item 1 |
| INT-CRM-09 Service Change | Portal → CRM | `ICrmGateway.SubmitServiceChangeAsync`, outbox — **implemented**, enqueued by `ServiceChangeRequestService.SubmitAsync` | no — outbound | Real contract: Open item 1 |
| INT-BI-01 Active Lines Sync | BI → Portal (pull) | `IBiGateway.GetActiveLinesAsync` — port and `ActiveLineSyncService` **implemented** and tested against `FakeBiGateway`; scheduled (`ActiveLineSyncScheduler`) and manually triggered at `POST /api/v1/ops/bi/active-lines/sync`. `BiGateway`'s real HTTP call still throws | **yes** (the trigger) | Real HTTP call: BI table structure (§11.2) |
| INT-BI-02 Reporting Extract | Portal → BI | `IBiGateway.PublishReportingExtractAsync`, scheduled | no — outbound | BI table structure (§11.2) |
| INT-SAP-01 Financial Code | SAP ↔ Portal | `ISapGateway.GetFinancialCodeAsync`, disabled by configuration | no | **Open item 5 — direction itself undecided** |
| INT-MAIL-01 Email Dispatch | Portal → SMTP | `IEmailGateway.SendAsync` | no — outbound | Open items 6, 7 (recipients, template) |

## Why one inbound endpoint and not three

TRD §7.1 lists INT-CRM-03, -05 and -07 as three interfaces, but TR-INT-22 requires "a single
versioned inbound event API for all CRM-originated ticket lifecycle updates". They are three
*events*, not three endpoints, distinguished by `eventType`. Splitting them would give three
places to implement deduplication, ordering and dead-lettering, and TR-INT-25 requires ordering
to be enforced **per ticket** across all of them — which is only coherent if they share a path.

## Operational endpoints

These are not §7.1 rows, but §7.2 and §6.1 require an administrator to be able to operate the
interfaces above:

| Endpoint | Requirement |
| --- | --- |
| `GET /api/v1/ops/integration/dead-letter` | TR-INT-05 — dead-lettered messages are inspectable |
| `POST /api/v1/ops/integration/dead-letter/{messageId}/replay` | TR-INT-05 — replayable without loss or duplication |
| `POST /api/v1/ops/bi/active-lines/sync` | TR-PAS-03 — manual trigger by the administrator |
| `GET /api/v1/ops/bi/active-lines/sync/status` | TR-PAS-07 — last successful sync, monitored |
| `GET /api/v1/ops/reconciliation` | TR-INT-10, TR-ACT-19 — daily portal/CRM discrepancies |
| `POST /api/v1/tickets/events/replay` | TR-INT-31 — reprocess events for a ticket or window |

## Publishing the contract

`Microsoft.AspNetCore.OpenApi` generates the document from the endpoint definitions and serves
it at `/openapi/v1.json`. TR-INT-01 requires a versioned contract agreed and signed off by both
parties before development: the signed-off artefact is a published snapshot of that document,
taken from a build and attached to the integration agreement — not a hand-maintained file that
can drift from what the portal serves.

To capture a snapshot:

```bash
dotnet run --project src/Bitstream.Api &
curl -sk https://localhost:7291/openapi/v1.json -o docs/integration/bitstream-portal-v1.json
```

## Field mapping for Direction A

Not written as a signed-off document. TR-INT-21 requires the portal-to-CRM field mapping to be
documented and signed off by both teams before development, and the CRM-side contract has not
been supplied (open item 1). `CrmHttpGateway` (`src/Bitstream.Infrastructure.Integration/Crm`)
does carry a *provisional* shape — a JSON POST per operation, echoing the fields
`CreateCrmCustomerCommand`/`CreateActivationTicketCommand` already have, described in that
file's own doc comment — built so Direction A is exercisable end to end (against
`tools/CrmSimulator`) without waiting on the contract, and isolated so that adopting the real one
is a change to that file's request/response records and nothing else. It is not the signed-off
mapping TR-INT-21 asks for; that table still goes in
`docs/integration/crm-direction-a-mapping.md` when the real contract arrives.
