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

## Deferred Decisions

- Board mutation is deferred until `doctor`, project summary, checks, MCP, and headless E2E are stable.
- Python remains allowed later as a thin KiCad adapter if `kicad-cli` and IPC do not cover a required operation.
- KiCad IPC is the preferred long-term direction for live GUI interaction and language-agnostic board operations.
