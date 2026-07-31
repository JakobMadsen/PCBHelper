# ADR-0001: Deterministic layout proof evidence

## Status

Accepted

## Context

KiCad source is authoritative, but DRC does not express every project-specific placement, routing, copper-area, or matching requirement. The former architecture branch implemented these checks but allowed empty measurements to pass for several missing-net and unfilled-zone cases.

## Decision

PCBHelper uses a project-scoped Layout Constraints V1 document and a single deterministic proof module. Each report is bound to the exact board and constraint contents by SHA-256. A proof may be `passed`, `failed`, or `unavailable`; unresolved or absent evidence is never `passed`.

The proof module writes reports below `.pcbhelper/reports/constraints` and does not mutate KiCad sources. Release policy may require current proof evidence later, but the proof module does not create a second release disposition.

## Consequences

- Constraint evidence is reproducible and stale reports are detectable.
- Missing nets, tracks, features, or zone fills remain visible.
- The current release audit remains the only module that determines `READY`, `PROTOTYPE-ONLY`, or `BLOCKED`.
