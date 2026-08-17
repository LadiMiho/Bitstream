# Environment provisioning and deployment

Windows Server and IIS, scripted end to end. TR-ARC-08 does not permit manual configuration of
production, so anything that has to be true of an environment lives in a file here and is
applied by a script — not typed into IIS Manager once and remembered.

There are no container files in this repository, and CI fails the build if one appears.

## Scripts

| Script | Run | Purpose |
| --- | --- | --- |
| `Install-Prerequisites.ps1` | Once per server | IIS features and the .NET 10 Hosting Bundle |
| `New-BitstreamSite.ps1` | Once per environment, and to correct drift | Application pool, site, bindings, ACLs |
| `Set-AppPoolSecrets.ps1` | Once, and on every credential rotation | Service account password and application secrets |
| `../db/Deploy-Database.ps1` | Before every application deployment | Schema scripts |
| `Deploy-Application.ps1` | Every deployment | Publish output onto the site |

All of them are idempotent and support `-WhatIf`.

## Environment definitions

`environments/uat.psd1` and `environments/production.psd1` differ only in values — site name,
paths, host headers, certificate thumbprint, service account, SQL instance. That is what
TR-ARC-07's "isolated data and credentials" looks like in practice: the difference between two
environments is a diff, not a conversation.

**No secret is in these files.** They name the secrets an environment needs;
`Set-AppPoolSecrets.ps1` reads the values from the operator's secret store and sets them as
application-pool environment variables (`BITSTREAM_Secrets__<Name>`), which the application
reads through `ISecretResolver` (TR-SEC-28).

Certificate thumbprints ship as `REPLACE_WITH_..._THUMBPRINT`. `New-BitstreamSite.ps1` warns
rather than failing on a placeholder, so the site can be provisioned before the certificate is
issued — but it will not bind TLS until a real thumbprint is set.

## First-time set-up

```powershell
# On the web server, elevated
.\Install-Prerequisites.ps1
.\New-BitstreamSite.ps1        -Environment uat
.\Set-AppPoolSecrets.ps1       -Environment uat

# Database
..\db\Deploy-Database.ps1 -ServerInstance SQLUAT01 -Database BitstreamPortal -AppUser 'CORP\svc_bitstream_uat'

# Application
.\Deploy-Application.ps1 -Environment uat -PackagePath C:\artifacts\publish
```

## Routine deployment

**Database first, then application.** The application refuses to start against a schema version
it was not built for (`SchemaVersionGuard`), and each schema script stays backward compatible
for one version, so this order works without downtime. `Deploy-Application.ps1` checks the
schema before it touches a file and stops if it does not match.

```powershell
..\db\Deploy-Database.ps1 -ServerInstance SQLUAT01 -Database BitstreamPortal -AppUser 'CORP\svc_bitstream_uat' -SchemaVersion 2
.\Deploy-Application.ps1 -Environment uat -PackagePath .\artifacts\publish
```

The deployment drops `app_offline.htm` so IIS drains in-flight requests and shuts the process
down cleanly — an outbox dispatch that is mid-flight either finishes or is retried from the
database, because the message is committed before it is sent (TR-ARC-03). Then it backs up the
current site, replaces the files wholesale rather than copying over the top, brings the site
back and warms `/health/ready`.

Replacing rather than overwriting is deliberate: a stale assembly left behind by a rename is a
hard failure to diagnose and a trivial one to prevent.

## Rollback

`Deploy-Application.ps1` prints the backup path. Rolling back is deploying it:

```powershell
.\Deploy-Application.ps1 -Environment uat -PackagePath D:\Sites\BitstreamPortal-UAT_backup_20260817-101500 -SkipSchemaCheck
```

`-SkipSchemaCheck` is needed because the schema is deliberately ahead of the application at that
point. The database is **not** rolled back: the scripts contain no destructive statements,
nothing is dropped and nothing is deleted (TR-DAT-07). A schema change that genuinely has to be
reversed is a new forward script.

## What is set on the application pool, and why

- **No Managed Code.** The ASP.NET Core Module hosts the runtime; the .NET Framework CLR must
  not be loaded.
- **Idle timeout and periodic recycling off.** The portal runs an outbox dispatcher and
  scheduled jobs. A recycle mid-dispatch is survivable — messages live in the database — but
  pointless, and an idle shutdown makes the first request after a quiet period slow enough to
  breach TR-NFR-01.
- **`AlwaysRunning` start mode**, so the process is warm before the first user arrives.
- **ACLs**: read and execute on the site, write on the log folder only. The application never
  writes to its own binaries, and a process that cannot overwrite its own code cannot be made to.

## Still to do

Monitoring is not wired up. TR-NFR-16 wants alerting on availability, error rate, integration
queue depth, dead-letter volume, BI sync freshness and mail dispatch failures. The data exists
— `ops.IntegrationMessage` by status, `portal.ActiveLine.BiSyncedAt`, `ops.Notification` by
status, and `/health/ready` — but connecting it to the monitoring platform is an operations
task that has not been done.
