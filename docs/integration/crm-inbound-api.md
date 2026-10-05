# CRM → Portal: status update API

This is the interface CRM calls to tell the Bitstream Portal that an activation request (or a
complaint ticket) has moved to its next step. It is served by the **Bitstream.Api** host; the
generated OpenAPI document is at `/openapi/v1.json` on that host.

## The call

```
POST https://{api-host}/api/v1/tickets/{identifier}/events
Content-Type: application/json
X-Api-Key: {key agreed with the portal team}
X-Correlation-Id: {optional; echoed in the portal's logs}
```

`{identifier}` is **either** the portal's request ID (for example `TRING_001`) **or** CRM's own
ticket number (the `EV_TICKET_NO` returned by `BITSTREAM_TICKET_CREATE`, for example
`8009521719`). The portal tries its own ID first, then the CRM ticket number.

Body — the same shape for every step:

```json
{
  "eventId": "crm-evt-000123",
  "eventType": "SALES_ORDER_OPENED",
  "identifier": "TRING_001",
  "crmTicketId": "8009521719",
  "occurredAt": "2026-10-05T09:15:00Z",
  "payload": { "salesOrderId": "SO-4500123" }
}
```

| Field | Required | Meaning |
|---|---|---|
| `eventId` | yes | Unique per event. Resending the same `eventId` is safe: the portal answers 200 with `"duplicate": true` and changes nothing. |
| `eventType` | yes | The step — see the table below. |
| `identifier` | no | If sent, must equal `{identifier}` in the URL. |
| `crmTicketId` | no | CRM's ticket number, for reference. |
| `occurredAt` | yes | UTC time the step happened in CRM. Must be later than the previous step's `occurredAt` for the same request; an older or equal one is accepted (200) but ignored. |
| `payload` | yes | Step-specific fields (may be `{}`). |

## Activation request steps

The portal itself does the first steps: the ISP submits the request, the portal creates the
Business Partner (`CRM_BP_CREATE`) and the ticket (`BITSTREAM_TICKET_CREATE`), and the request
waits in **AwaitingGisVerification**. From there CRM reports each step:

| # | Step | `eventType` | Required status before | Status after | `payload` |
|---|---|---|---|---|---|
| 1a | Line check: line available | `LINE_AVAILABLE` | AwaitingGisVerification | LineAvailable | `{}` |
| 1b | Line check: no line | `NO_LINE` | AwaitingGisVerification | RejectedNoLine | `reason` (required) |
| 2 | Sales order opened | `SALES_ORDER_OPENED` | LineAvailable | SalesOrderOpened | `salesOrderId` (required), `businessPartner` (optional) |
| 3 | Provisioning started | `PROVISIONING_STARTED` | SalesOrderOpened | InProvisioning | `{}` |
| 4 | Line live | `TECHNICALLY_COMPLETED` | InProvisioning | Completed | `{}` |

Steps must arrive in this order. An administrator can still record the line check in the portal
as a fallback; whichever arrives first applies, and the other is then refused with 409.

### Examples

```json
POST /api/v1/tickets/8009521719/events
{ "eventId": "crm-evt-1001", "eventType": "LINE_AVAILABLE",
  "occurredAt": "2026-10-05T08:00:00Z", "payload": {} }
```

```json
POST /api/v1/tickets/8009521719/events
{ "eventId": "crm-evt-1002", "eventType": "NO_LINE",
  "occurredAt": "2026-10-05T08:00:00Z", "payload": { "reason": "No fibre in this street" } }
```

```json
POST /api/v1/tickets/8009521719/events
{ "eventId": "crm-evt-1003", "eventType": "SALES_ORDER_OPENED",
  "occurredAt": "2026-10-05T09:15:00Z",
  "payload": { "salesOrderId": "SO-4500123", "businessPartner": "1102017112" } }
```

```json
POST /api/v1/tickets/8009521719/events
{ "eventId": "crm-evt-1004", "eventType": "PROVISIONING_STARTED",
  "occurredAt": "2026-10-06T07:30:00Z", "payload": {} }
```

```json
POST /api/v1/tickets/8009521719/events
{ "eventId": "crm-evt-1005", "eventType": "TECHNICALLY_COMPLETED",
  "occurredAt": "2026-10-08T14:00:00Z", "payload": {} }
```

## Complaint ticket events

The same endpoint, addressed by the complaint ticket's ID or CRM ticket number:
`STATUS_CHANGED` (`payload.status` required; `forwardingGroup` marks an internal forward the ISP
is not told about), `COMMENT_ADDED` (`payload.comment` required, `agent` optional),
`CLOSED_WITH_CLEARING_CODE` (`payload.clearingCode` required, `clearingText` optional),
`AUTO_COMPLETED`, `REOPENED`.

## Responses

| Code | Meaning | What CRM should do |
|---|---|---|
| 200 | Accepted (`{eventId, identifier, duplicate, receivedAt}`). Also for a duplicate `eventId` or an out-of-date `occurredAt`. | Nothing. |
| 400 | Malformed body, missing `eventId`/`eventType`, or body `identifier` ≠ URL. | Fix the call. |
| 401 | Missing or wrong `X-Api-Key`. | Check the key. |
| 404 | No request or ticket with that identifier. | Check the identifier. |
| 409 | The step isn't allowed from the request's current status (wrong order, or already done). | Don't retry blindly; check the request's status. |
| 422 | Event type doesn't apply to this request, or a required payload field is missing. | Fix the call. |
| 429 | Too many calls (limit: 200 per second). | Retry after a short wait. |
| 5xx / timeout | Portal-side failure. | Retry with the **same** `eventId`. |

Every accepted call is stored before it is applied, so the portal can replay it later
(`POST /api/v1/tickets/events/replay`, administrative use).
