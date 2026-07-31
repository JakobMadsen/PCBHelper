# Changelog

All notable changes will be documented here. PCBHelper uses semantic versioning once the first alpha is tagged.

## Unreleased

- Consolidated the remaining branch-only KiCad fixes: KiCad 10 named routing and pad-net preservation, rotated rectangular-pad clearance, mounting-hole routing rejection, and validated atomic zone refill with retained evidence.
- Added a project-policy-driven release audit with CLI and workflow MCP surfaces, blocking evidence checks, and JSON/Markdown reports.

### Added

- Transactional Design Plan workflow and reduced MCP surface.
- KiCad project inspection, schematic authoring, placement, routing, board finishing, checks, export, and restore tools.
- Deterministic ngspice assertions and constrained sweeps.
- PCBWay release packaging and requirement-aware release gates.
- Agent guide, Copilot instructions, Docker clean-room, and public contribution infrastructure.

### Known limitations

- Windows 11 x64 with KiCad 10 is the only supported alpha user platform.
- PCBHelper targets small, simple two-layer boards and does not prove physical electrical performance.
- Live KiCad GUI refresh and zone refill depend on capabilities unavailable in current `kicad-cli` builds.
