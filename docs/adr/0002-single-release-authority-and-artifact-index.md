# ADR-0002: Single release authority and project artifact index

## Status

Accepted

## Context

PCBHelper produces transactions, reviews, simulations, release audits, constraint reports, and other evidence below `.pcbhelper`. Agents need a compact way to discover this evidence. The former architecture branch paired artifact discovery with a separate release-policy engine and evidence ledger, creating competing interpretations of release state.

## Decision

The existing release audit remains the sole release-disposition authority. The workflow artifact module only lists content-addressed project artifacts, reads bounded text, and summarizes the latest existing release audit without running or reinterpreting it.

The module excludes lock directories and filesystem reparse points. Binary content is never returned inline; text is capped at 256 KiB. Artifact identifiers bind project-relative paths to content SHA-256.

## Consequences

- Agents gain locality for project evidence through one small interface.
- Artifact reads cannot silently create a newer or different release disposition.
- Corrupt latest release-audit JSON is reported explicitly rather than ignored.
- Supplier-specific release adapters remain outside this decision until a second manufacturing flow is a concrete product requirement.
