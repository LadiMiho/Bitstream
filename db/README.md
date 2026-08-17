# Bitstream Portal — physical data model (MSSQL)

The database schema is owned by the numbered T-SQL scripts in `mssql/`. EF Core maps onto
this schema and never generates it. The reasoning is in
[`../docs/adr/0002-physical-data-model.md`](../docs/adr/0002-physical-data-model.md).

## Applying

```powershell
# From a Windows deployment host with the SqlServer module installed
.\Deploy-Database.ps1 -ServerInstance SQLUAT01 -Database BitstreamPortal -AppUser 'CORP\svc_bitstream_uat'
```

Every script is idempotent and re-runnable, so the same command brings a Development, UAT or
Production database up to date (TR-ARC-07, TR-ARC-08). Run order is the numeric prefix.

| Script | Contents |
| --- | --- |
| `0001_schemas_and_version.sql` | `sec`, `portal`, `ops` schemas; `ops.SchemaVersion` ledger |
| `0002_security_tables.sql` | Role, Permission, RolePermission, Isp, User, UserPasswordHistory, AuditLog |
| `0003_portal_tables.sql` | ActivationRequest, ActiveLine, ComplaintTicket, TicketComment, ServiceChangeRequest |
| `0004_operations_tables.sql` | Notification, IntegrationMessage (outbox + inbox) |
| `0005_identifier_series.sql` | Gap-free public identifier counter and `ops.usp_NextPublicIdentifier` |
| `0006_integrity_guards.sql` | Append-only audit, comment immutability, no-delete triggers |
| `0007_seed_roles_permissions.sql` | Seeded roles, permission codes and the baseline mapping |
| `0008_permissions.sql` | Grants for the application service account |
| `0009_sessions_and_two_factor.sql` | UserSession, TwoFactorChallenge — TRD 4 access management (schema version 2) |

## Checking where a database actually is

```powershell
.\Get-SchemaStatus.ps1 -ServerInstance SQLUAT01 -Database BitstreamPortal -ExpectedVersion 2
```

Compares the files in `mssql/` with the rows in `ops.SchemaVersion` and lists what is pending.
Exit code 0 means the database matches the application build, 1 means scripts are pending or
the version differs, 2 means the database is unreachable — so it works as a deployment gate.

It also flags the reverse case: applied scripts with no matching file, which means the database
is *ahead* of the checkout and deploying this build would move the application backwards.

## Changing the schema

1. Add a new numbered script. Never edit an applied one — an idempotent script that has already
   run is history, and editing it means two environments silently differ.
2. Keep it backward compatible with the previous application version: add columns nullable, add
   tables, do not drop or rename. The deployment order is database first, then application, and
   TR-NFR-19 requires that to work without downtime.
3. Bump `BitstreamDbContext.ExpectedSchemaVersion` and pass the same value as
   `-SchemaVersion` to `Deploy-Database.ps1`.
4. Update the EF configuration in `src/Bitstream.Infrastructure.Persistence/Configurations/`
   to match, including index names and filter predicates.

`SchemaVersionGuard` compares the two at start-up and refuses to start on a mismatch, so an
application deployed ahead of its schema fails immediately rather than on the first request that
touches a new column. An unreachable database at start-up is treated differently — the host
starts and the readiness probe reports it, because a database that is not up yet is not the same
fault as a wrong schema and must not crash-loop the site under IIS.

## Schema layout

| Schema | Holds | Why separate |
| --- | --- | --- |
| `sec` | Identity, RBAC, audit | Different grant profile: audit is append-only, and the whole schema is denied DELETE |
| `portal` | ISP-visible business records | The set an ISP-scoped query ever touches |
| `ops` | Outbox/inbox, notifications, counters, version ledger | Operational data with its own retention and archival rules |

## Mapping to TRD §3.1

Every logical entity in TRD §3.1 has exactly one table. Physical naming is PascalCase
singular; the TRD leaves naming and typing to the implementation team and binds only the
attributes and constraints.

| TRD §3.1 entity | Table |
| --- | --- |
| ISP | `sec.Isp` |
| User | `sec.[User]` |
| Role | `sec.Role` |
| Permission | `sec.Permission` |
| RolePermission | `sec.RolePermission` |
| ActivationRequest | `portal.ActivationRequest` |
| ActiveLine | `portal.ActiveLine` |
| ComplaintTicket | `portal.ComplaintTicket` |
| TicketComment | `portal.TicketComment` |
| ServiceChangeRequest | `portal.ServiceChangeRequest` |
| Notification | `ops.Notification` |
| AuditLog | `sec.AuditLog` |
| IntegrationMessage | `ops.IntegrationMessage` |

`sec.UserSession` and `sec.TwoFactorChallenge` are not TRD §3.1 entities — see
"Tables beyond §3.1" below.

### Columns beyond §3.1

§3.1 lists key attributes, not a complete column list. These additions each exist to satisfy
a specific "Must", and nothing else has been added:

| Addition | Required by |
| --- | --- |
| `sec.[User].PasswordHash`, `PasswordHashAlgorithm`, `PasswordUpdatedAt`, `TotpSecret` | TR-SEC-02, TR-SEC-04 |
| `sec.UserPasswordHistory` (whole table) | TR-SEC-03 — no reuse of the last 5 passwords |
| `portal.ActivationRequest.FinancialCode` | TR-INT-11 |
| `portal.ActivationRequest.StatusReason` | TR-ACT-13, TR-INT-19 |
| `portal.ComplaintTicket.ClearingText`, `ClosureDecisionAt`, `ClosureDecisionBy` | TR-PAS-18, TR-PAS-23 |
| `portal.ComplaintTicket.ConfirmationDueAt` | TR-PAS-21a, TR-PAS-21h |
| `portal.ComplaintTicket.ParentTicketId` | TR-PAS-21f — post-closure challenge link |
| `portal.ComplaintTicket.LastAppliedEventAt` | TR-INT-25 — ordering per ticket |
| `ops.IntegrationMessage.InterfaceCode`, `MessageType`, `ResponsePayload`, `CorrelationId` | TR-INT-02, TR-INT-25 |
| `ops.PublicIdentifierSeries` (whole table) | TR-DAT-02b, TR-DAT-03 |
| `ops.SchemaVersion` (whole table) | TR-ARC-08, TR-NFR-19 |

### Tables beyond §3.1

| Table | Required by |
| --- | --- |
| `sec.UserSession` | TR-SEC-07 — a session token must be invalidated at logout and at lock, which only a server-side record can be |
| `sec.TwoFactorChallenge` | TR-SEC-04 — the second-factor state between the password check and the code submission must survive across requests and worker processes |

Neither carries a no-delete trigger the way `sec.Isp` and `sec.[User]` do: TR-DAT-07 binds
business and identity data, and a session or a 2FA challenge is neither — both are meaningless
once expired or revoked. The application account is still denied DELETE at the schema level
(`0008_permissions.sql`), so nothing in the running application can remove a row from either
table regardless; a future retention job (TRD 11.4 open item 10) is the intended place to prune
them.

## Things worth knowing before you change anything

**The identifier counter is not a SEQUENCE, on purpose.** TR-DAT-02b asks for a *gap-free*
series. A SQL Server `SEQUENCE` is monotonic but not gap-free — it allocates outside your
transaction, so rollbacks and restarts burn numbers. `ops.usp_NextPublicIdentifier` takes a
row lock inside the caller's transaction instead, which is what makes the series gap-free and
collision-free (TR-DAT-03). The cost is that submissions serialise per series for the length
of the transaction. That is the right trade at the stated load (TR-NFR-03: 200 concurrent
users), but it is the constraint to revisit first if throughput requirements change.

**Prefixes are placeholders.** `ops.PublicIdentifierSeries` is seeded with `ISP`, `TKT` and
`SCR`. The agreed production prefix, and the distinct non-production prefix required by
TR-DAT-02e, are TRD §11.4 open item 2 and must be set per environment before go-live.

**Ticket status has no CHECK constraint.** TR-PAS-16 requires the notifiable status set to be
configurable, and the CRM status list has not been supplied (open item 4). A constraint here
would have to be altered every time CRM adds a status. Unknown values are rejected at the
inbound API with 422 (TR-INT-27) — that is where the vocabulary is validated. Activation
request status *is* constrained, because TRD §5.3 defines that state machine in full.

**Archival is not implemented yet.** TR-DAT-10 requires audit records and integration
messages to be retained for at least 24 months and archived rather than purged. The retention
periods themselves are open item 10, so no archive tables and no archival job are created.
When they are, the job runs under a DBA account: the application account is denied DELETE
everywhere (`0008_permissions.sql`), and that should stay true.
