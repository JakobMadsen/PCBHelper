# Changelog

All notable changes will be documented here. PCBHelper uses semantic versioning once the first alpha is tagged.

## Unreleased

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
