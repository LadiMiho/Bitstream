# Bitstream.Api on IIS: keep it running

Bitstream.Api is the only host that sends messages to CRM. Its background worker (the outbox
dispatcher) picks up queued messages every 5 seconds: creating the Business Partner
(`CRM_BP_CREATE`), the activation ticket (`BITSTREAM_TICKET_CREATE`), and so on. The portal
(Bitstream.Web) only queues them.

By default, IIS starts an ASP.NET Core site only when it receives its first request, and stops it
after 20 idle minutes. CRM rarely calls the API, so the worker is usually not running. Activation
requests then stay in **Pending CRM Sync**, and the View drawer timeline shows "Not picked up
yet".

## One-time server settings

1. **Windows feature:** Server Manager → Add Roles and Features → Web Server (IIS) → Web Server
   → Application Development → **Application Initialization**.
2. **App pool of the Bitstream.Api site:** Advanced Settings:
   - **Start Mode** = `AlwaysRunning`
   - **Idle Time-out (minutes)** = `0`
3. **Bitstream.Api site:** Advanced Settings → **Preload Enabled** = `True`.

`web.config` already contains `<applicationInitialization>`, which calls `/health/live` at start-up
and after every recycle.

## Check it works

- Open `http://<api-host>/health/live` once, or recycle the app pool.
- `logs/bitstream-api-YYYYMMDD.log` in the site folder shows
  `Outbox dispatcher started: polling every 00:00:05 …`.
- Requests waiting in Pending CRM Sync move on within seconds.
- If a CRM call fails, the log shows
  `INT-CRM-01 CREATE_CUSTOMER for TRING_001 failed (attempt n of 5); will retry: <reason>`, and
  the request's View timeline shows the same error. Requests stay in Pending CRM Sync while
  retries are in progress, and become Integration Failed after the last attempt.
- `logs/crm-soap-YYYYMMDD.log` (when `Integration:Crm:Soap:LogMessages` is on) has the SOAP
  request and CRM's response.
