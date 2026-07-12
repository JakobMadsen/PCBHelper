# Implementation Decisions

## V1 Technical Targets

- Primary implementation language: C#/.NET.
- Runtime target: .NET 10 LTS.
- First supported KiCad line: KiCad 9+.
- First KiCad integration point: `kicad-cli`.
- First agent client target: VS Code/Copilot through MCP stdio.
- First MCP implementation: official C# `ModelContextProtocol` SDK.
- First issue tracker: GitHub Issues on `JakobMadsen/PCBHelper`.

## Integration Boundary

The core KiCad logic lives in `PCBHelper.Core`. CLI and MCP projects are thin facades over the same services so behavior stays testable and consistent across humans, Copilot, Codex, and future local clients.

## Product Boundary

- The primary user is not expected to know or operate KiCad.
- PCBHelper targets small, simple, constrained two-layer boards rather than general PCB design.
- KiCad remains the source of truth and optional expert escape hatch.
- The primary interface is conversational; granular KiCad tools are agent-facing implementation details.
- V1 prepares and validates manufacturer files but does not place or pay for orders.

## Component Selection

- The AI may discover and propose part candidates.
- A part becomes approved only for a specific project after deterministic identity, footprint, pin-map, rating, sourcing, and policy checks.
- Price, stock, MOQ, lifecycle, supplier, and evidence freshness are part of the approval decision.
- Expensive, scarce, obsolete, unusual, or uncertain candidates require explicit user approval.
- Unverified electrical compatibility, pin mapping, footprint, or manufacturer identity blocks release.
- Approved choices and their evidence are recorded in a versioned project design lock.

## Human Review

- The user approves function, dimensions, connectors, costs, risks, and final release through beginner-readable artifacts.
- KiCad review is optional for the primary flow.
- Engineering gates provide deterministic evidence; the LLM explains results but does not define pass/fail alone.

## Deferred Decisions

- Board mutation is deferred until `doctor`, project summary, checks, MCP, and headless E2E are stable.
- Python remains allowed later as a thin KiCad adapter if `kicad-cli` and IPC do not cover a required operation.
- KiCad IPC is the preferred long-term direction for live GUI interaction and language-agnostic board operations.
