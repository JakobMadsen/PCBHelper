# Roadmap

## Phase 0: Specs And Repo Shape

- Write public project description.
- Define V1 boundaries and non-goals.
- Draft tool contracts.
- Define BYOK and secret-handling expectations.
- Choose initial implementation language.
- Choose first supported KiCad version.
- Decision: .NET 10 LTS, KiCad 9+, VS Code/Copilot via MCP first.

## Phase 1: Read-Only KiCad Inspection

- Detect KiCad installation and CLI availability.
- Open or locate a KiCad project.
- Read project structure.
- Read board summary.
- List footprints, nets, and board outline.
- Add fixture projects for tests.
- Add initial unit and contract test harness.

## Phase 2: Checks And Reports

- Run ERC through `kicad-cli`.
- Run DRC through `kicad-cli`.
- Parse reports into structured findings.
- Produce concise human-readable summaries.
- Add tests for report parsing.
- Add fixture projects with known ERC and DRC findings.

## Phase 3: Controlled Placement

- Place and move footprints by millimeter coordinates.
- Measure component spacing.
- Set 15 mm sensor spacing for the Hello World board.
- Report before and after placement.
- Add geometry tests.
- Current placement support covers footprint measuring, dry-run moves, real top-level footprint moves, axis-constrained spacing, change reports, and restore of single-footprint placement changes.

## Phase 4: Manufacturing Export

- Export Gerbers.
- Export drill files.
- Export BOM.
- Export position files if supported by the template.
- Build manufacturing zip.
- Validate archive contents.
- Add headless E2E test for project creation, checks, export, and zip validation.
- Current generic export/package support covers Gerber, drill, and a manifest-backed manufacturing zip.

## Phase 5: Human Review Loop

- Add highlighting or review aids where KiCad APIs support them.
- Add explicit change summaries.
- Explore transaction IDs or rollback support.
- Document recommended git workflow for board changes.
- Add local KiCad E2E smoke test procedure for visual review.
- Current review support can open a project in the local KiCad GUI and records mutation reports under `.pcbhelper/changes/`.

## Phase 6: Simulation And PCB Tests

- Add first ngspice integration.
- Define simulation fixtures.
- Add Python assertions for optical sensor board behavior.
- Report simulation pass/fail status through the tool layer.
