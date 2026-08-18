# Deployment — Windows Server and IIS

The platform runs directly on Windows Server under IIS. There are no container files anywhere
in this repository: no Dockerfile, no compose file, no orchestrator manifest, and no container
references in any project.

TR-ARC-07 requires at least three environments — Development, UAT and Production — with
isolated data and credentials. TR-ARC-08 requires provisioning to be scripted; manual
configuration of production is not permitted.

## Prerequisites on the web server

| Component | Notes |
| --- | --- |
| Windows Server 2019 or later | IIS role with the ASP.NET Core Module v2 |
| .NET 10 Hosting Bundle | Installs ANCM v2 and the shared framework; restart IIS afterwards |
| TLS certificate | TLS 1.2 or higher, HTTP refused for API endpoints (TR-SEC-26) |
| Application pool | No Managed Code, running as the service account granted rights by `db/mssql/0008_permissions.sql` |

Node.js is not part of this stack anywhere, on the web server or the build agent: the UI is
Razor Pages, and its Tailwind stylesheet is compiled by the standalone Tailwind CLI — a single
self-contained native binary, downloaded on demand by an MSBuild step — not by npm. The build
agent needs only outbound access to GitHub to fetch that binary the first time, when publishing
with `-p:BuildFrontend=true`.

## Build and publish

```powershell
# On the build agent (compiles the stylesheet; needs network access the first time, to fetch
# the standalone Tailwind CLI into tools\tailwindcss\)
dotnet publish src\Bitstream.Api -c Release -p:BuildFrontend=true -o .\publish

# Backend only, using a stylesheet already built
dotnet publish src\Bitstream.Api -c Release -o .\publish
```

The publish output contains the API and the frontend under `wwwroot\`, so the portal deploys as
a single IIS site: same origin, no CORS configuration, and the session cookie behaves.

## Database

```powershell
.\db\Deploy-Database.ps1 -ServerInstance SQLUAT01 -Database BitstreamPortal -AppUser 'CORP\svc_bitstream_uat'
```

Scripts are idempotent, so this is also how an environment is brought up to date. The
application checks `ops.SchemaVersion` against `BitstreamDbContext.ExpectedSchemaVersion` at
start-up and refuses to run against a schema it was not built for.

**Order matters on an upgrade:** apply the database scripts first, then deploy the application.
The schema is designed to be backward compatible for one version so that this order never
requires downtime (TR-NFR-19).

## Configuration and secrets

`appsettings.json` holds non-secret defaults only. Per-environment values come from IIS
configuration or environment variables, and every credential comes from the secret store
(TR-SEC-28) — no connection string, integration credential or certificate password belongs in a
file in this repository.

Set at minimum, per environment:

| Setting | Notes |
| --- | --- |
| `ConnectionStrings:BitstreamDb` | Prefer Integrated Security with the app pool identity |
| `Identifiers:*Prefix` | TR-DAT-02a/e — distinct prefix outside production. Open item 2 |
| `Integration:Crm:*` | Open item 1 |
| `Integration:Bi:*` | §11.2 dependency |
| `Integration:Smtp:*` | `RedirectAllMail` **true** outside production (TR-NTF-07) |
| `ASPNETCORE_ENVIRONMENT` | Set in `web.config` by the deployment script |

## Rollback

TR-NFR-19 requires deployment without data loss and a documented rollback:

1. Deploy the previous application publish output to the site directory.
2. Do **not** roll the database back. The schema is backward compatible for one version, and
   `db/mssql` scripts contain no destructive statements — nothing is dropped and nothing is
   deleted (TR-DAT-07).
3. If a schema change genuinely has to be reversed, it is a new forward script, reviewed and
   applied the same way.

## Backup and recovery

TR-NFR-08 and TR-NFR-09: daily full backups, transaction-log backups at most 15 minutes apart,
RPO 15 minutes, RTO 4 hours, restore tested at least twice a year. This is SQL Server Agent and
the DBA's backup policy — configured on the instance, not in this repository, but the log-backup
interval is what makes the 15-minute RPO real, so it is worth confirming rather than assuming.

## Monitoring

TR-NFR-16 requires alerting on availability, error rate, integration queue depth, dead-letter
volume, BI sync freshness and mail dispatch failures. The data is in place —
`ops.IntegrationMessage` by status, `portal.ActiveLine.BiSyncedAt`, `ops.Notification` by status
— and `/health/ready` reports dependency reachability. Wiring these into the monitoring platform
is an operations task that has not been done.

Note that `/health/live` deliberately reports only whether the process is up. A CRM outage must
not cause IIS to recycle a portal that is still usable in read mode (TR-NFR-07).
