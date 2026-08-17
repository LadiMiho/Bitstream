# Interface inventory — TRD §7.1

Every row of TRD §7.1 mapped to what represents it in the scaffold. The distinction that
matters: an interface the portal **exposes** is an HTTP endpoint and appears in
`/openapi/v1.json`; an interface the portal **calls** is a port plus an adapter, and its
contract belongs to the other system.

| TRD §7.1 | Direction | Represented by | OpenAPI | Blocked by |
| --- | --- | --- | --- | --- |
| INT-CRM-01 Create Customer | Portal → CRM | `ICrmGateway.CreateCustomerAsync`, outbox | no — outbound | Open item 1 |
| INT-CRM-02 Create Activation Ticket | Portal → CRM | `ICrmGateway.CreateActivationTicketAsync`, outbox | no — outbound | Open item 1 |
| INT-CRM-03 Sales Order Notification | CRM → Portal | `POST /api/v1/tickets/{identifier}/events`, event type carrying `SalesOrderEventPayload` | **yes** | Open items 3, 4 |
| INT-CRM-04 Create Complaint Ticket | Portal → CRM | `ICrmGateway.CreateComplaintTicketAsync`, outbox | no — outbound | Open item 1 |
| INT-CRM-05 Ticket Lifecycle Events | CRM → Portal | `POST /api/v1/tickets/{identifier}/events` | **yes** | Open items 3, 4 |
| INT-CRM-06 Comment Replication | Bidirectional | Out: `ICrmGateway.ReplicateCommentAsync`. In: `COMMENT_ADDED` on the events endpoint | **yes** (inbound half) | Open item 1 (outbound) |
| INT-CRM-07 Closure / Clearing Code | CRM → Portal | `CLOSED_WITH_CLEARING_CODE` on the events endpoint | **yes** | Open items 3, 4 |
| INT-CRM-08 Closure Decision | Portal → CRM | `ICrmGateway.SubmitClosureDecisionAsync` | no — outbound | Open item 1 |
| INT-CRM-09 Service Change | Portal → CRM | `ICrmGateway.SubmitServiceChangeAsync`, outbox | no — outbound | Open item 1 |
| INT-BI-01 Active Lines Sync | BI → Portal (pull) | `IBiGateway.GetActiveLinesAsync`, scheduled; manual trigger at `POST /api/v1/ops/bi/active-lines/sync` | **yes** (the trigger) | BI table structure (§11.2) |
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

Not written. TR-INT-21 requires the portal-to-CRM field mapping to be documented in a mapping
table and signed off by both teams before development, and the CRM-side contract has not been
supplied (open item 1). Writing a mapping against a guessed contract would produce a document
that looks authoritative and is not. The table goes in
`docs/integration/crm-direction-a-mapping.md` when the contract arrives.
