# ADR-0001 — Minimal APIs rather than MVC controllers

**Status:** Accepted · **Date:** August 2026 · **Affects:** TR-INT-01, TR-INT-22, TR-INT-29,
TR-INT-30

## Decision

The portal-exposed HTTP surface is built with ASP.NET Core **minimal APIs**, grouped per
interface in `src/Bitstream.Api/Endpoints/`, with the OpenAPI document generated from those
definitions by `Microsoft.AspNetCore.OpenApi` and served at `/openapi/v1.json`.

## Why

**The surface is small and machine-facing.** TRD §7.1 lists thirteen interfaces, but only
three of them are things the portal *exposes*: INT-CRM-03, INT-CRM-05 and INT-CRM-07, and
TR-INT-22 requires all three to arrive on a *single* endpoint. Everything else is an outbound
call. A controller hierarchy is scaffolding for a large, varied, human-facing API; that is not
what this is.

**The route group maps one-to-one onto the versioned contract.** TR-INT-29 puts the version
in the path and requires the previous version to keep working during a transition. A
`MapGroup("/api/v1/tickets")` next to a future `MapGroup("/api/v2/tickets")` makes that
concrete: two groups, two sets of handlers, one file each, no attribute routing to reconcile.

**Response metadata is the contract here.** TRD §7.3.2 specifies exactly what CRM must do for
each of 200 / 200-duplicate / 400 / 401 / 404 / 409 / 422 / 5xx. `Produces` and
`ProducesProblem` on the endpoint put that table into the generated document without the
attribute noise that the same declarations need on a controller action.

**Less indirection to a stub.** At scaffold stage every handler returns 501. Minimal APIs let
the endpoint definition — the part that is real and that CRM signs off — stay legible without
a class-per-endpoint ceremony around a one-line body.

## What this does not change

Handlers stay thin: they validate, call an application service, and translate the result. No
business logic lives in `Endpoints/`, exactly as it would not live in a controller. The
layering rule (TR-ARC-01) is unaffected by the endpoint style and is enforced separately by
`tests/Bitstream.ArchitectureTests`.

## Consequences

- Model binding attributes (`[FromRoute]`, `[FromBody]`, `[FromQuery]`) are used explicitly on
  the handler parameters so that the generated document is unambiguous.
- There is no filter pipeline by convention; cross-cutting behaviour is either middleware
  (correlation ID) or an endpoint filter added deliberately.
- If the portal later grows a large ISP-facing API with many similar CRUD shapes, revisiting
  this for controllers is a reasonable thing to do. It would not require changing the
  application layer.

## Alternatives considered

**MVC controllers.** Familiar, and model validation attributes come for free. Rejected because
the exposed surface is one event endpoint plus a handful of operational endpoints, and the
per-endpoint response tables are more legible in fluent form.

**A hand-written OpenAPI YAML as the source of truth, with code generated from it.** Genuinely
attractive for TR-INT-01, since the contract is signed off by two teams before development.
Rejected because it introduces a generation step and a second artefact that can drift from the
running code. Generating the document from the endpoints means the published contract is
always what the portal actually serves. The signed-off artefact is then a *published version*
of that document, checked out of the build.
