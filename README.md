# ISP Platform — Bitstream Portal

Project scaffold for the ISP Platform (Bitstream Portal), built to
*Technical Requirements Document v1.0, August 2026*.

**Foundation stage: no feature code.** Structure, contracts, schema, configuration, logging,
health, CI and deployment are in place. Application services are unimplemented, no
authentication exists yet, and every business endpoint answers `501 Not Implemented`.

- .NET 10 (C#) backend, layered per TRD §2.1
- MSSQL, schema defined by T-SQL scripts
- Vanilla JavaScript with Tailwind CSS — no framework, no bundler
- Windows Server / IIS, no containers anywhere

## Layout

```
Bitstream.sln
src/
  Bitstream.Domain                      entities and enums (TRD §3.1)
  Bitstream.Application                 service contracts and integration ports
  Bitstream.Infrastructure.Persistence  EF Core mapped to the physical schema
  Bitstream.Infrastructure.Integration  CRM, BI, SAP and SMTP adapters
  Bitstream.Api                         minimal-API endpoints, composition root
  Bitstream.Web                         HTML, ES modules, Tailwind CLI build
tests/
  Bitstream.ArchitectureTests           layering rules, enforced at build time
  Bitstream.Api.Tests                   middleware, health endpoints, config validators
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

# Database (Windows, SqlServer PowerShell module)
./db/Deploy-Database.ps1 -ServerInstance . -Database BitstreamPortal -AppUser 'DOMAIN\svc_bitstream_dev'

# Frontend
cd src/Bitstream.Web && npm ci && npm run build:css

# Run — portal at /, generated contract at /openapi/v1.json
dotnet run --project src/Bitstream.Api
```

## Foundation layer (TRD §2.4)

| Requirement | Where |
| --- | --- |
| TR-ARC-06 externalised configuration | `Configuration/` in Application and Api, per-adapter options, validators run at start-up |
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

## The four Phase 0 deliverables

**1. Layered solution (TRD §2.1, TR-ARC-01/02).** Five projects with the reference direction
running one way only: Domain ← Application ← Infrastructure ← Api. The application layer
reaches external systems solely through ports in
`Bitstream.Application.Abstractions.Integration`; the adapters that implement them are the only
code holding an `HttpClient` or an SMTP client. `Bitstream.Api` references both Infrastructure
projects purely as the composition root. This is enforced by
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

**4. Tailwind build.** Tailwind v4 CLI, CSS-first configuration, no `tailwind.config.js`, no
PostCSS, no bundler. `npm run build:css` produces `wwwroot/css/app.css`; the browser loads the
ES modules directly. The API host serves the frontend so the portal is one IIS site. See
[`src/Bitstream.Web/README.md`](src/Bitstream.Web/README.md).

## Open items

Several TRD §11.4 items block design decisions, and none of them has been guessed at.
**Two stop work outright:**

- **Item 1 — the CRM Direction A contract.** Blocks every portal-to-CRM call and the whole
  activation flow. TR-INT-19 requires distinguishing a business rejection (never retried) from
  a technical failure (retried), which is only knowable from CRM's error semantics.
- **Item 5 — where the SAP financial code is populated.** Blocks INT-SAP-01 including its
  *direction*. Pull, push and manual entry put the interface in different components.

A third, from §11.2 rather than §11.4: **the BI active-lines reference table structure** blocks
the entire post-activation support module.

The full analysis — what each of the thirteen items blocks, what exists in the scaffold in the
meantime, and which are configuration rather than design — is in
[`docs/open-items.md`](docs/open-items.md).

## Verification status

Honest accounting of what has and has not been run:

| | |
| --- | --- |
| Tailwind build | **Verified** — `npm run build:css` runs clean and emits the expected classes |
| CI workflow YAML | **Verified** as valid YAML; never executed on a runner |
| C# compilation | **Not verified** — the .NET SDK could not be installed here; the egress policy blocks `builds.dotnet.microsoft.com` |
| Tests | **Not run** — they need the SDK |
| T-SQL execution | **Not verified** — no SQL Server instance available |
| PowerShell deployment scripts | **Not run** — they need Windows, IIS and SQL Server |

So the first thing to do on a machine with the .NET 10 SDK is `dotnet build` and
`dotnet test`, and to apply `db/mssql` against a scratch database. Expect small fixes —
package versions and a stray using are the usual candidates, and
`dotnet format --verify-no-changes` will likely want a first formatting pass, since the code
was written by hand rather than emitted by the formatter. Nothing in the design depends on that
pass; it is a compile-and-run check of code that has been written but not executed.
