# ADR-0002 — Hand-written T-SQL rather than EF Core migrations

**Status:** Accepted · **Date:** August 2026 · **Affects:** TR-DAT-02b, TR-DAT-03, TR-DAT-07,
TR-DAT-09, TR-SEC-24, TR-ARC-08, TR-NFR-19

## Decision

The physical schema is defined by numbered, idempotent T-SQL scripts in `db/mssql/`, applied
by `db/Deploy-Database.ps1`. EF Core is mapped onto that schema through
`IEntityTypeConfiguration` classes and **does not generate it**: there are no migrations, and
`EnsureCreated` is never called. `ops.SchemaVersion` records what has been applied, and
`BitstreamDbContext.ExpectedSchemaVersion` is checked at start-up so drift fails fast.

## Why

**Several requirements are DDL that a migration builder expresses badly, or not at all.**

- *TR-SEC-24 — the audit store is append-only.* Enforced by an `INSTEAD OF UPDATE, DELETE`
  trigger and by denying the application account UPDATE and DELETE. Neither is expressible in
  the fluent model; both would end up as raw SQL inside a migration anyway.
- *TR-DAT-07 — no physical deletion.* Same: triggers plus `DENY DELETE ON SCHEMA`.
- *TR-DAT-02b — a gap-free, monotonically increasing series.* This is the interesting one. EF's
  `HasSequence` maps to a SQL Server `SEQUENCE`, which is monotonic but **not** gap-free —
  values are handed out outside the caller's transaction, so a rollback or a restart burns
  numbers. Satisfying the requirement takes a counter table and a stored procedure that
  allocates inside the caller's transaction (`ops.usp_NextPublicIdentifier`). That is a
  database object with real semantics, and it deserves to be readable as SQL rather than
  buried in `migrationBuilder.Sql("...")`.
- *TR-PAS-27, TR-DAT-04 — immutability of comments and issued identifiers.* Triggers again.

**The deployment target makes scripts the natural unit.** The platform runs on Windows Server
and IIS with no containers, and TR-ARC-08 requires scripted, repeatable provisioning with no
manual configuration of production. In this shape the DBA runs numbered scripts against a
change ticket. That is also what makes TR-NFR-19 — deployment without data loss, with a
documented rollback — reviewable: the exact DDL is in the pull request, not a generated diff
whose SQL nobody reads until it runs.

**Reviewability.** A migration file is generated output; the artefact that gets reviewed
should be the artefact that gets executed.

## Consequences and how they are handled

**The EF model and the schema can drift.** This is the real cost, and it is accepted
deliberately rather than waved away. Three things hold them together:

1. Column names, types, lengths, index names and filters in the `IEntityTypeConfiguration`
   classes mirror the DDL exactly, including filtered-index predicates and `INCLUDE` columns.
2. `ops.SchemaVersion` versus `BitstreamDbContext.ExpectedSchemaVersion` is checked at
   start-up: an application deployed against the wrong schema refuses to start rather than
   failing later on a missing column.
3. A schema-comparison check belongs in CI — run the app's model against a freshly scripted
   database and fail the build on a difference. `dotnet ef dbcontext script` produces the
   model's view of the schema for that comparison. This is not built yet; it is the first
   thing to add once the modules exist and there is something to compare.

**No `dotnet ef database update` in the developer loop.** Developers run
`db/Deploy-Database.ps1` against their local instance. The scripts are idempotent, so re-running
is the normal way to catch up.

## Alternatives considered

**EF Core migrations, with raw SQL for triggers and grants.** Workable, and the standard choice
for a greenfield .NET project. Rejected because the parts of this schema that carry the
compliance requirements — append-only audit, no-delete, gap-free identifiers — would all be
raw SQL inside migrations, which gives up the benefit of migrations while keeping their
generated-artefact review problem. Worth revisiting if the trigger-based rules are ever moved
into the application, but TR-SEC-24 and TR-DAT-07 are stated as properties of the system, not
of a code path, so they should stay in the database.

**A SQL Server Database Project (.sqlproj) with DACPAC deployment.** A good fit for a
DBA-owned, Windows-hosted schema, and a legitimate alternative. Rejected for now to avoid
requiring SQL Server Data Tools on every build agent, and because DACPAC's generated deployment
plan reintroduces the "review the generator, not the SQL" problem for exactly the objects that
matter here. If the DBA team prefers `.sqlproj`, the scripts translate into one directly.

## Note on the environment this was written in

The .NET SDK could not be installed in the authoring environment (the egress policy blocks
`builds.dotnet.microsoft.com`), so no EF migration could have been generated and verified here
in any case. That did not drive the decision — the reasons above stand on their own — but it is
worth stating plainly: the C# in this scaffold has not been compiled, and the T-SQL has not been
run against an instance. Both need a first pass on a machine with the SDK and a SQL Server
instance. See the "Verification status" section of the root README.
