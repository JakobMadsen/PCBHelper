# Product Requirements: Human-in-the-Loop KiCad Operator

## Problem Statement

Simple PCB projects take too long when the user has to manually perform every KiCad setup, placement, checking, and export step. The user is comfortable with AI-assisted software work and wants a similar loop for PCB work: an agent can execute precise, repetitive operations, run checks, and explain results, while the human remains responsible for visual review and engineering judgement.

The current pain is not lack of imagination. It is the friction between an electronics idea and a reviewable KiCad design.

## Solution

Build a local tool layer, likely exposed as an MCP server, that lets Codex operate on KiCad projects through supported APIs and `kicad-cli`.

The tool layer should expose small, typed tools for creating projects, reading board state, placing footprints, measuring distances, running checks, highlighting relevant design objects, and exporting manufacturing files.

The system should be BYOK-friendly and public-repo-safe. Core KiCad automation should not depend on a private hosted service or committed credentials. Users should be able to connect their own Codex setup, API keys, local agents, or future compatible clients.

## User Stories

1. As a user, I want to create a KiCad project from an approved template, so that I can start from known-good structure.
2. As a user, I want the agent to read the existing project structure, so that it understands the board before making changes.
3. As a user, I want the agent to summarize components, footprints, nets, and board outline, so that I can quickly inspect the design state.
4. As a user, I want to place a footprint at exact millimeter coordinates, so that mechanical constraints can be satisfied.
5. As a user, I want to move a footprint by a precise delta, so that placement edits are controlled.
6. As a user, I want to set center-to-center spacing between two components, so that sensor geometry can be enforced.
7. As a user, I want the agent to measure distances between components, so that I can verify placement constraints.
8. As a user, I want the agent to highlight selected components or nets in KiCad, so that I can visually review what it is talking about.
9. As a user, I want ERC to run from the tool layer, so that schematic problems are caught early.
10. As a user, I want DRC to run from the tool layer, so that board rule violations are caught early.
11. As a user, I want check reports to be parsed into understandable summaries, so that I know what requires attention.
12. As a user, I want Gerber and drill files exported consistently, so that manufacturing output is repeatable.
13. As a user, I want BOM and position files exported where possible, so that assembly workflows can be tested.
14. As a user, I want a manufacturing zip generated, so that the prototype flow can be exercised end to end.
15. As a user, I want the agent to use approved parts and templates, so that it does not invent unsafe or unavailable designs.
16. As a user, I want meaningful changes grouped into transactions, so that I can inspect or undo them together.
17. As a user, I want a dry-run or plan mode for risky operations, so that the agent can explain intent before changing files.
18. As a user, I want all secret configuration kept local, so that the repository can remain public.
19. As a contributor, I want clear tool contracts, so that integrations can be built without guessing.
20. As a contributor, I want test fixtures based on KiCad projects, so that tool behavior can be verified without manual GUI work.

## Functional Requirements

- Create a new KiCad project from an approved template.
- Inspect project files and return a board summary.
- Read components, footprints, nets, design rules, and board outline.
- Place and move footprints in millimeter units.
- Measure distances between footprints and named anchor points.
- Enforce or report a target spacing between two components.
- Run ERC and DRC through KiCad command line tooling.
- Parse ERC and DRC output into structured results.
- Export Gerber, drill, BOM, and position files where supported.
- Build a manufacturing zip from export artifacts.
- Support approved parts and templates as explicit project data.
- Keep agent actions visible and reviewable.

## Non-Functional Requirements

- Local-first: KiCad operations run on the user's machine.
- Public-repo-safe: no committed credentials or private account assumptions.
- Provider-flexible: the core should not require one AI vendor.
- Deterministic where possible: tool outputs should be structured and testable.
- Conservative by default: prefer read-only inspection unless a tool is explicitly mutating.
- Recoverable: mutating operations should either be atomic or have a clear rollback story.
- Cross-platform intent: design for Windows first if that is the development environment, but avoid unnecessary OS coupling.

## Implementation Decisions

- Use a local MCP server or equivalent tool host as the main integration boundary.
- Use `kicad-cli` for ERC, DRC, and manufacturing exports where available.
- Use KiCad Python or IPC APIs for board inspection, placement, measurements, and highlighting.
- Keep AI provider configuration outside the core KiCad automation layer.
- Treat templates and approved parts as first-class inputs.
- Prefer small tools such as `get_board_summary`, `place_component`, and `run_drc` over broad autonomous commands.
- Represent measurements in millimeters at the public tool boundary.
- Return structured JSON-like results from tools, with human-readable summaries as secondary fields.
- Design mutating tools around transactions or explicit before/after reporting.

## Testing Decisions

- Unit-test pure parsing, validation, geometry, and packaging logic.
- Use fixture KiCad projects for integration tests.
- Test tool behavior through public tool contracts rather than internal implementation details.
- Validate exported manufacturing archives by checking required files and naming conventions.
- Treat ERC, DRC, and future SPICE assertions as the PCB equivalent of software tests.
- Require at least one headless end-to-end test that exercises the public tool boundary from project creation through checks and manufacturing export.
- Require local KiCad end-to-end smoke testing before claiming a release works with a supported KiCad version.

## Out of Scope For V1

- Free-form complex circuit design.
- High-speed layout.
- RF layout.
- Fully autonomous routing.
- Arbitrary web-based component search.
- Automatic board ordering.
- GUI mouse automation as the primary control path.
- Firmware workflow beyond possible placeholders.

## Success Criteria

The first prototype succeeds when the user can ask the agent to create a simple dual optical sensor board, place two sensors at 15 mm center spacing, run ERC and DRC, export manufacturing files, and review the result in KiCad.
