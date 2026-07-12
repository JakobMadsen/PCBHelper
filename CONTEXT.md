# Project Context

## Domain

PCBHelper is a conversational design tool for small, simple PCBs. It lets a person without KiCad or electronics expertise describe a need to an AI and receive a reviewable, verified, manufacturer-ready design. KiCad remains the internal design engine and source of truth, but using KiCad is not a requirement for the primary user journey.

The project sits between four domains:

- KiCad project files and command line tools
- local agent tooling, likely MCP
- PCB design workflow and manufacturing export
- beginner-facing review and approval
- component availability, cost, and manufacturing readiness

## Core Beliefs

- KiCad is the source of truth for the design.
- The primary user should not need to understand or operate KiCad.
- KiCad remains an optional expert review and recovery surface.
- Agent actions should be small, explicit, inspectable, and reversible where possible.
- The tool layer should use supported KiCad APIs and CLI commands instead of GUI automation.
- V1 should be template-driven and constrained.
- The AI may propose components, but project approval requires evidence for identity, pin mapping, footprint compatibility, price, availability, and lifecycle risk.
- Expensive, scarce, obsolete, unusually sourced, or uncertain parts require a conversation with the user before design lock.
- Checks and simulations should become the PCB equivalent of software tests.
- A clean ERC or DRC result is necessary but not sufficient evidence that a circuit fulfills its intended function.

## Main Actors

- User: a human builder who describes the intended function and approves understandable choices without needing KiCad expertise.
- Codex or another agent: clarifies requirements, proposes a constrained design, evaluates parts, calls tools, and explains evidence and uncertainty.
- KiCad: stores and displays the design.
- MCP server or local tool layer: exposes KiCad operations as typed tools.
- Component data sources: provide datasheets, price, stock, lifecycle, supplier, and manufacturer identifiers.
- Manufacturer flow: consumes Gerber, drill, BOM, CPL/position, and zip outputs.

## Glossary

- Human-in-the-loop: the user approves understandable product decisions, costs, risks, and final outputs; expert-level KiCad operation is optional.
- Tool layer: local code that translates agent requests into KiCad API or CLI calls.
- Transaction: a group of related changes that can be reviewed and ideally undone together.
- Design recipe: a known, constrained circuit or layout pattern with declared inputs, outputs, limits, tests, and compatible parts.
- Part candidate: a component proposed by the AI but not yet locked into the project.
- Approved part: a component approved for a specific project after deterministic compatibility checks and either automatic policy approval or explicit user approval.
- Design lock: the point where chosen symbols, footprints, pin mappings, values, and manufacturer part numbers become project inputs rather than AI suggestions.
- Engineering gate: deterministic ERC, DRC, simulation, manufacturing, and policy outcomes required before release.
- Board summary: a machine-readable snapshot of project, board, components, nets, constraints, and checks.
- Manufacturing zip: a package of production files suitable for PCBWay, JLCPCB, or similar services.

## V1 Boundary

V1 should prove a conversational end-to-end loop on one small, simple, two-layer board. The user should be able to reach a PCBWay-ready review package without knowing KiCad. The system should favor constrained design recipes and deterministic evidence over broad autonomous electronics design.

The most important V1 capabilities are:

- clarify functional, mechanical, power, connector, quantity, and budget requirements
- create a project from a known design recipe
- propose and evaluate project-specific parts
- escalate expensive, scarce, risky, or uncertain part choices to the user
- create and inspect the KiCad schematic and board internally
- place and route a small two-layer board within supported constraints
- run ERC, DRC, manufacturing checks, and relevant simulation assertions
- present beginner-readable previews, connector pinouts, dimensions, BOM cost, warnings, and remaining uncertainty
- export and validate PCBWay-ready manufacturing and assembly files
- keep final release and ordering approval explicit

V1 excludes mains voltage, high current, RF, high-speed digital layout, safety-critical or medical designs, complex power conversion, and arbitrary large boards. Unsupported requests must be declined or referred to a qualified electronics engineer.
