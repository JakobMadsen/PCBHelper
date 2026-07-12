# Product Requirements: Conversational Small-PCB Builder

## Problem Statement

People with good product ideas are often blocked by electronics terminology, KiCad operation, component selection, layout rules, and manufacturer file requirements. For small and well-understood circuits, the user should be able to explain the desired behavior in ordinary language and receive a design that is understandable, checked, and ready for manufacturer review.

The current pain is the gap between an electronics idea and a trustworthy PCBWay-ready package. Requiring the user to learn KiCad merely moves that gap rather than closing it.

## Solution

Build a local, BYOK-friendly system, exposed through MCP and CLI, that lets an AI clarify requirements, compose constrained design recipes, evaluate parts, operate KiCad, run deterministic engineering gates, and create manufacturer-ready review packages.

The AI may use small, typed KiCad tools internally. The primary user interaction is conversational and task-oriented: describe the board, answer material questions, review understandable previews and risks, and approve the final package. Opening KiCad is an optional expert path, not a normal prerequisite.

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
21. As a beginner, I want to describe a small PCB without knowing KiCad, so that the implementation details do not block me.
22. As a user, I want the AI to propose suitable parts using current price, availability, lifecycle, and compatibility evidence.
23. As a user, I want the AI to ask before using an expensive, scarce, obsolete, unusually sourced, or uncertain part.
24. As a user, I want a plain-language review package with board images, dimensions, connectors, BOM cost, checks, and unresolved risks.
25. As a user, I want PCBWay-ready Gerber, drill, BOM, and CPL outputs to be validated before I approve release.
26. As a user, I want unsupported or safety-critical requests to be refused or escalated instead of receiving confident guesswork.

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
- Discover part candidates through replaceable data-source adapters.
- Evaluate part candidates for symbol and footprint identity, pin mapping, electrical limits, package, stock, price, MOQ, lifecycle, supplier, and manufacturer identifiers.
- Apply configurable project policies for budget, preferred suppliers, minimum availability evidence, and approval thresholds.
- Require explicit user approval when a part exceeds policy or has incomplete or conflicting evidence.
- Lock approved part evidence into project metadata so later runs are reproducible.
- Generate beginner-readable visual and textual review artifacts without requiring KiCad knowledge.
- Validate generic and PCBWay-oriented manufacturing and assembly outputs.
- Keep agent actions visible and reviewable.

## Non-Functional Requirements

- Local-first: KiCad operations run on the user's machine.
- Public-repo-safe: no committed credentials or private account assumptions.
- Provider-flexible: the core should not require one AI vendor.
- Deterministic where possible: tool outputs should be structured and testable.
- Conservative by default: prefer read-only inspection unless a tool is explicitly mutating.
- Recoverable: mutating operations should either be atomic or have a clear rollback story.
- Cross-platform intent: design for Windows first if that is the development environment, but avoid unnecessary OS coupling.
- Evidence-based: the LLM may propose and explain, while deterministic tools validate file scope, part identity, compatibility, engineering checks, and release readiness.
- Honest uncertainty: missing datasheets, ambiguous pin maps, stale pricing, or unavailable stock must be visible and block automatic approval.

## Implementation Decisions

- Use a local MCP server or equivalent tool host as the main integration boundary.
- Use `kicad-cli` for ERC, DRC, and manufacturing exports where available.
- Use KiCad Python or IPC APIs for board inspection, placement, measurements, and highlighting.
- Keep AI provider configuration outside the core KiCad automation layer.
- Treat design recipes, part candidates, project-approved parts, and design locks as first-class inputs.
- Do not make the LLM the sole authority for footprint or pin-map correctness.
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
- Unconstrained component selection without evidence and project policy.
- Automatic board ordering.
- GUI mouse automation as the primary control path.
- Firmware workflow beyond possible placeholders.

## Success Criteria

The first prototype succeeds when a user without KiCad knowledge can describe the simple optical sensor board, answer requirement and part-choice questions, approve two sensors at 15 mm spacing, receive understandable previews and engineering-gate results, and obtain a validated PCBWay-ready package. An expert may inspect the source in KiCad, but the primary flow must not require it.
