# Bitstream Portal — Production Deployment Guide

This guide takes the Bitstream Portal from a tagged build to a running, verified production
environment, and covers routine upgrades, rollback and day-to-day operation. It is written for
the people who do the deployment: the Windows/IIS administrator and the DBA. Every command here
has a script behind it in `deploy/` or `db/`; production is never configured by hand
(TR-ARC-08).

> **Production runs as `Production`, never as `Development`.** In Development the portal applies
> database scripts and seeds an administrator at start-up, which must never happen on its own in
> production. `ASPNETCORE_ENVIRONMENT=Production` is set by the deployment scripts.

## 1. What you are deploying

| Component | What it is | Who uses it | Reachable from |
| --- | --- | --- | --- |
| **Bitstream.Web** | The portal: screens, sign-in, the endpoints the screens call | ISP users, service desk, administrators | Users' networks, over HTTPS (HTTP only redirects) |
| **Bitstream.Api** | The CRM inbound event API, plus the background workers that send to CRM (outbox) and send notification emails | CRM | CRM's network only, HTTPS only |
| **SQL Server database** `BitstreamPortal` | All data; schema owned by `db/mssql` scripts | Both sites, through one service account | The web server only |

Both sites are needed. **Bitstream.Api is not optional:** it is the only thing that sends
anything to CRM and the only thing that sends notification emails. Without it, requests are
accepted and then stay in *Pending CRM Sync*.

External systems: CRM (SOAP endpoint, and CRM calling our inbound API), the email gateway, and
BI (fills the phone-number table).

**Order, always:** database first, then the application. Each application build checks the
schema version at start-up and refuses to run against a schema it was not built for.

## 2. Before you start

### 2.1 Servers and software

| Item | Requirement |
| --- | --- |
| Web server | Windows Server 2019 or later, IIS. Both sites can share one server. |
| .NET | .NET 10 Hosting Bundle (installed by `Install-Prerequisites.ps1`) |
| IIS features | Includes **Application Initialization**, needed to keep Bitstream.Api running (installed by `Install-Prerequisites.ps1`) |
| SQL Server | 2019 or later; daily full backups and log backups every 15 minutes (TR-NFR-08/09) |
| Deployment host | PowerShell 5.1+, the `SqlServer` module (`Install-Module SqlServer`), run elevated |
| Build machine | .NET 10 SDK; outbound access to GitHub the first time (Tailwind CLI download) |

### 2.2 Information to collect

| You need | Example | Used in |
| --- | --- | --- |
| Portal address | `https://bitstream.one.al` | DNS, certificate, `production.psd1`, `PortalBaseUrl` |
| CRM inbound API address | `https://crm-bitstream.one.al` | DNS, certificate, `production.psd1`, given to CRM |
| TLS certificate(s) and thumbprint(s) | `A1B2…` | `production.psd1` |
| Service account (app pool identity) | `CORP\svc_bitstream_prod` | `production.psd1`, SQL login |
| SQL Server instance and database | `SQLPROD01`, `BitstreamPortal` | `production.psd1`, connection string |
| CRM SOAP endpoint | `http://10.3.21.103:5040/integration/sap` | `appsettings.Production.json` |
| CRM inbound API key | long random string, shared with CRM | secret `CrmInboundApiKey` |
| Email gateway address, sender, API key | `http://10.9.4.9:9941`, `no-reply@one.al` | `appsettings.Production.json`, secret |
| First administrator's email | `wholesale.admin@one.al` | step 4.8 |
| Data Protection certificate thumbprint | installed on the web server | `appsettings.Production.json` |

Agree the firewall rules before you start: users → Web (443, 80), CRM → Api (443 only),
web server → SQL Server (1433), web server → CRM SOAP endpoint and email gateway.

## 3. Build the release

Build from the release commit on `main`, on the build machine:

```powershell
# Web: compiles the stylesheet (-p:BuildFrontend=true) and carries db\mssql with it
dotnet publish src\Bitstream.Web -c Release -p:BuildFrontend=true -o .\artifacts\web
dotnet publish src\Bitstream.Api -c Release -o .\artifacts\api

# Identity schema (users, roles, tokens) as a reviewable, idempotent SQL script for the DBA
cd src\Bitstream.Web
dotnet tool restore
dotnet dotnet-ef migrations script --idempotent --context BitstreamIdentityDbContext `
    --project ..\Bitstream.Infrastructure.Persistence --startup-project . `
    -o ..\..\artifacts\identity-schema.sql
cd ..\..
```

Copy `artifacts\` and the repository's `db\` and `deploy\` folders to the deployment host.

## 4. First-time installation

Run on the web server, PowerShell **as administrator**, from the `deploy` folder.

### 4.1 Install prerequisites (once per server)

```powershell
.\Install-Prerequisites.ps1
```

Installs the IIS features (including Application Initialization) and the .NET 10 Hosting
Bundle. Restart IIS (`iisreset`) or the server afterwards.

### 4.2 Describe the environment

Edit `deploy\environments\production.psd1` and replace every example value:

| Field | Set to |
| --- | --- |
| `Sites.Web.Bindings` / `Sites.Api.Bindings` | Real host names and `CertificateThumbprint`s (replace `REPLACE_WITH_PROD_CERT_THUMBPRINT`) |
| `Sites.*.PhysicalPath`, `LogPath` | Folders on the server, e.g. `D:\Sites\BitstreamPortal-PROD` |
| `Sites.*.AppPoolIdentity`, `Database.AppUser` | The service account, e.g. `CORP\svc_bitstream_prod` |
| `Database.ServerInstance`, `Database.Name` | `SQLPROD01`, `BitstreamPortal` |
| `Database.SchemaVersion` | `23` — must equal `BitstreamDbContext.ExpectedSchemaVersion` of the build |

The file holds no secrets; commit your values to the repository so the environment is
reproducible.

### 4.3 Create the two IIS sites

```powershell
.\New-BitstreamSite.ps1 -Environment production -Component Web
.\New-BitstreamSite.ps1 -Environment production -Component Api
```

Creates each application pool (No Managed Code, `AlwaysRunning`, no idle time-out, no timed
recycle, the service account), the site with **Preload enabled**, the bindings and the folder
permissions (read/execute on the site, write on the log folder only).

### 4.4 Create the database (DBA)

1. Create the empty database `BitstreamPortal` and a login for the service account
   (Windows login `CORP\svc_bitstream_prod`, or a SQL login).
2. Apply the identity schema produced in step 3, in SSMS or with `sqlcmd -I`:

   ```powershell
   sqlcmd -S SQLPROD01 -d BitstreamPortal -I -i .\artifacts\identity-schema.sql
   ```

3. Apply all schema scripts and the service account's permissions:

   ```powershell
   ..\db\Deploy-Database.ps1 -ServerInstance SQLPROD01 -Database BitstreamPortal `
       -AppUser 'CORP\svc_bitstream_prod' -SchemaVersion 23
   ```

4. Check the result — exit code 0 means the database matches the build:

   ```powershell
   ..\db\Get-SchemaStatus.ps1 -ServerInstance SQLPROD01 -Database BitstreamPortal -ExpectedVersion 23
   ```

> **`sqlcmd` needs `-I`.** Without it `sqlcmd` runs with `QUOTED_IDENTIFIER OFF` and statements
> on tables with filtered indexes fail. SSMS and `Invoke-Sqlcmd` (used by `Deploy-Database.ps1`)
> are correct by default.

The service account gets only what the application needs: read/insert/update on `sec`,
`portal`, `ops` (0008), the identity tables and the numbering procedure (0032), **no DELETE on
business data, no UPDATE on the audit log, no DDL**. Do not make it `db_owner`.

### 4.5 Server settings: `appsettings.Production.json`

Create `appsettings.Production.json` in **each** site folder (Web and Api). It holds this
server's non-secret settings and is kept by every later deployment (`Deploy-Application.ps1`
does not delete it). Start from this template:

```json
{
  "ConnectionStrings": {
    "BitstreamDb": "Server=SQLPROD01;Database=BitstreamPortal;Trusted_Connection=True;Encrypt=True;TrustServerCertificate=False;MultipleActiveResultSets=True"
  },
  "DataProtection": {
    "CertificateThumbprint": "<thumbprint of the key-ring certificate, same on both sites>"
  },
  "Security": {
    "TwoFactor": { "Channel": "Totp" }
  },
  "Integration": {
    "Crm": {
      "Soap": { "Endpoint": "http://10.3.21.103:5040/integration/sap" }
    },
    "Email": {
      "BaseUrl": "http://10.9.4.9:9941",
      "Sender": "no-reply@one.al",
      "RedirectAllMailTo": "",
      "LogPayloads": false
    }
  },
  "Notifications": {
    "Email": { "PortalBaseUrl": "https://bitstream.one.al" }
  }
}
```

| Setting | Notes |
| --- | --- |
| `ConnectionStrings:BitstreamDb` | With `Trusted_Connection=True` the app pool identity is the SQL login. No password in the file. `TrustServerCertificate=False` needs a certificate on SQL Server that the web server trusts; use `True` only if SQL Server has none, and plan to fix that. |
| `DataProtection:CertificateThumbprint` | Encrypts the key ring that protects sign-in cookies and authenticator keys. Install the certificate (with private key, readable by the service account) on the server. Same certificate on both sites. |
| `Security:TwoFactor:Channel` | `Totp` (authenticator app). `EmailOtp` sends the code by email instead. |
| `Integration:Crm:Soap:Endpoint` | CRM's SOAP workflow endpoint (Api site; harmless on Web). |
| `Integration:Email:*` | Email gateway. `RedirectAllMailTo` must be empty in production. |
| `Notifications:Email:PortalBaseUrl` | The portal's public address, for the *Open request* link in emails (Api site). |

Never put keys or passwords in this file — they go in step 4.6.

### 4.6 Secrets

```powershell
.\Set-AppPoolSecrets.ps1 -Environment production -Component Api
.\Set-AppPoolSecrets.ps1 -Environment production -Component Web
```

Asks for the service account's password and each secret, and stores them as environment
variables on the application pool (never in a file). Leave a prompt blank to skip it.

| Secret | Api pool | Web pool |
| --- | --- | --- |
| `CrmInboundApiKey` — the `X-Api-Key` CRM must send | **Required** (without it every CRM call gets 401) | Skip |
| `Integration__Email__ApiKey` — email gateway key | **Required** (notification emails) | Required only with `Channel = EmailOtp` |
| `CrmClientSecret` | Skip while CRM is SOAP | Skip |

Give CRM the same `CrmInboundApiKey` value through a secure channel.

### 4.7 Deploy the application

```powershell
.\Deploy-Application.ps1 -Environment production -Component Api -PackagePath ..\artifacts\api
.\Deploy-Application.ps1 -Environment production -Component Web -PackagePath ..\artifacts\web
```

Each run checks the schema version, takes the site offline (`app_offline.htm`), backs it up,
replaces the files (keeping `logs\` and `appsettings.Production.json`), brings it back, recycles
the pool and calls `/health/ready`.

### 4.8 Create the first administrator

A new database has no users. Create the first administrator **once**, from an administrator's
workstation (not the production server), with the same release checkout and a DBA connection:

```powershell
# PowerShell, in the repository root of the release commit
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Database__DevelopmentAutoMigrate = 'true'
$env:ConnectionStrings__BitstreamDb = 'Server=SQLPROD01;Database=BitstreamPortal;Trusted_Connection=True;Encrypt=True;MultipleActiveResultSets=True'
$env:Development__AdminEmail = 'wholesale.admin@one.al'
$env:Development__AdminPassword = '<a strong one-time password, 12+ characters>'
dotnet run --project src\Bitstream.Web
```

Wait for the log line `Development bootstrap: seeded administrator …`, then stop it (Ctrl+C)
and close the PowerShell window so the variables are gone. The run also re-applies the schema
scripts, which is harmless (they are idempotent). It refuses to run if the checkout's scripts are
older than the database.

Then sign in to the production portal with that account, scan the QR code with an authenticator
app, and **change the password immediately** (user menu › Change password).

### 4.9 Configure the portal

Signed in as the administrator:

1. **Activation Requests › Packages & durations** — add the packages and contract durations
   (no packages are pre-loaded).
2. **Activation Requests › Package offers** — add each package + duration with its CRM code
   (CLASS_3), as agreed with the CRM team.
3. **Activations › Support classifications** — check the three-level list (seeded by 0023).
4. **Access Management › ISP Administration** — add the ISPs (ticket code = activation ID prefix).
5. **Access Management › User Administration** — add users (ISP users, service desk, other
   administrators).
6. **Access Management › Notification settings** — choose which status changes notify whom.

## 5. Verify the installation

| Check | How | Expected |
| --- | --- | --- |
| Schema | `Get-SchemaStatus.ps1 … -ExpectedVersion 23` | Exit code 0 |
| Web health | `https://<portal>/health/ready` | 200 |
| Api health | `https://<crm-api>/health/ready` | 200 |
| Api background workers | `D:\Sites\BitstreamApi-PROD\logs\bitstream-api-<date>.log` | `Outbox dispatcher started …` and `Notification email dispatcher started …` |
| CRM API protected | `POST https://<crm-api>/api/v1/tickets/TEST/events` without key | 401 |
| CRM API key | Same with the right `X-Api-Key` (Postman collection `docs/integration/postman`) | Not 401 |
| Sign-in | Administrator signs in with password + authenticator code | Dashboard |
| End to end | Submit a test activation request for a test ISP | Moves from *Submitted* to *Pending CRM Sync* to *Waiting GIS Verification* within a minute; CRM shows the business partner and ticket |
| Email | A status change with email enabled | Email received, *Open request* link opens the portal |
| HTTPS | `http://<portal>` | Redirects to https |

Remove the test request's data afterwards only with the DBA (production has no delete path).

## 6. Routine upgrade

1. Read the release notes; check whether `ExpectedSchemaVersion` changed (`db/README.md`).
2. **Back up the database** (full backup).
3. Build the release (section 3).
4. Database: `Deploy-Database.ps1 … -SchemaVersion <version>` then `Get-SchemaStatus.ps1`.
   If the release changed the identity schema, apply the new `identity-schema.sql` first.
5. Application: `Deploy-Application.ps1` for **Api**, then for **Web**.
6. Verify (section 5: health, logs, sign-in).

If `production.psd1` gained new required secrets, run `Set-AppPoolSecrets.ps1` before step 5.
Users see a short maintenance page while each site is replaced.

## 7. Rollback

1. Re-deploy the backup printed by `Deploy-Application.ps1`:

   ```powershell
   .\Deploy-Application.ps1 -Environment production -Component Web `
       -PackagePath D:\Sites\BitstreamPortal-PROD_backup_<timestamp> -SkipSchemaCheck
   ```

2. Do **not** roll the database back. Scripts never drop or delete anything and each schema
   version stays compatible with the previous build, so the older application runs on it.
3. A schema change that must be undone is a new forward script.

Restore the database backup only for data corruption, and only with the DBA.

## 8. Operations

| Topic | Where / what |
| --- | --- |
| Application logs | `logs\` in each site folder: `bitstream-web-<date>.log`, `bitstream-api-<date>.log`, `crm-soap-<date>.log` (when SOAP logging is on) |
| Audit log | Portal › Access Management › Audit Logs (export to Excel) |
| Queues to watch | `ops.IntegrationMessage` by `Status` (Pending / DeadLettered), `ops.Notification` by `Status` (Failed) |
| Health | `/health/live` (process up), `/health/ready` (database, schema, integrations) |
| Backups | Database: full daily + log every 15 min. The key ring lives in the database (`ops.DataProtectionKeys`) and is backed up with it; keep the Data Protection certificate (with private key) in the certificate store backup. |
| Certificate renewal | Update the thumbprint in `production.psd1`, re-run `New-BitstreamSite.ps1`. For the Data Protection certificate, install the new one and update `appsettings.Production.json`; keep the old one installed until existing sessions expire. |
| Rotating the CRM API key | Agree a time with CRM, re-run `Set-AppPoolSecrets.ps1 -Component Api`, recycle the pool. |
| Never in production | `db\maintenance\*.sql` (test-data clean-up scripts), `Security:AllowInsecureHttp`, `ASPNETCORE_ENVIRONMENT=Development` |

## 9. Troubleshooting

| Symptom | Likely cause | Fix |
| --- | --- | --- |
| HTTP 500.30 / site does not start | Schema version mismatch, or an invalid setting (both are checked at start-up) | Read the newest log in `logs\`; run `Get-SchemaStatus.ps1`; fix the setting it names |
| `SELECT permission was denied on the object 'Users'` | Script 0032 not applied | `Deploy-Database.ps1 … -AppUser …` |
| Requests stay in *Pending CRM Sync* | Bitstream.Api not running, or CRM unreachable | Check Api health and its log; pool `AlwaysRunning`, site Preload enabled, Application Initialization installed |
| CRM gets 401 | Wrong or missing `X-Api-Key`, or `CrmInboundApiKey` not set | `Set-AppPoolSecrets.ps1 -Component Api`; compare with CRM |
| No emails | Email gateway key/address, or `RedirectAllMailTo` set | Check `Integration:Email:*`, `ops.Notification.Status`, Api log |
| Everyone signed out / authenticator codes rejected after a server move | Data Protection key ring or its certificate missing | Restore the certificate; keys are in `ops.DataProtectionKeys` |
| PUT/PATCH return 405 | IIS WebDAV module | Already removed in `web.config`; check a server-level WebDAV setting |
| 429 Too Many Requests | Global rate limits (`RateLimits:*`) | Raise the limits in `appsettings.Production.json` (see Security Overview, open item 1) |

## 10. Quick reference

```powershell
# First time (elevated, from deploy\)
.\Install-Prerequisites.ps1
.\New-BitstreamSite.ps1   -Environment production -Component Web
.\New-BitstreamSite.ps1   -Environment production -Component Api
sqlcmd -S SQLPROD01 -d BitstreamPortal -I -i ..\artifacts\identity-schema.sql
..\db\Deploy-Database.ps1 -ServerInstance SQLPROD01 -Database BitstreamPortal -AppUser 'CORP\svc_bitstream_prod' -SchemaVersion 23
..\db\Get-SchemaStatus.ps1 -ServerInstance SQLPROD01 -Database BitstreamPortal -ExpectedVersion 23
# appsettings.Production.json in each site folder (4.5)
.\Set-AppPoolSecrets.ps1  -Environment production -Component Api
.\Set-AppPoolSecrets.ps1  -Environment production -Component Web
.\Deploy-Application.ps1  -Environment production -Component Api -PackagePath ..\artifacts\api
.\Deploy-Application.ps1  -Environment production -Component Web -PackagePath ..\artifacts\web
# First administrator (4.8), then configure the portal (4.9)

# Every upgrade
#   back up the database, then:
..\db\Deploy-Database.ps1 -ServerInstance SQLPROD01 -Database BitstreamPortal -AppUser 'CORP\svc_bitstream_prod' -SchemaVersion 23
.\Deploy-Application.ps1  -Environment production -Component Api -PackagePath ..\artifacts\api
.\Deploy-Application.ps1  -Environment production -Component Web -PackagePath ..\artifacts\web
```
