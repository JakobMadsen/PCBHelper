# Project Context

## Domain

PCBHelper is a local automation layer for KiCad-based PCB development. It lets an AI coding agent, initially Codex, perform precise and reviewable KiCad operations while the user remains responsible for engineering judgement and final approval.

The project sits between four domains:

- KiCad project files and command line tools
- local agent tooling, likely MCP
- PCB design workflow and manufacturing export
- human-in-the-loop review

## Core Beliefs

- KiCad is the source of truth for the design.
- The GUI should remain visible to the human reviewer.
- Agent actions should be small, explicit, inspectable, and reversible where possible.
- The tool layer should use supported KiCad APIs and CLI commands instead of GUI automation.
- V1 should be template-driven and constrained.
- Approved parts and known templates are safer than unconstrained component selection.
- Checks and simulations should become the PCB equivalent of software tests.

## Main Actors

- User: a human builder who wants faster simple PCB workflows without giving up control.
- Codex or another agent: plans and calls tools, but does not silently own the design.
- KiCad: stores and displays the design.
- MCP server or local tool layer: exposes KiCad operations as typed tools.
- Manufacturer flow: consumes Gerber, drill, BOM, CPL/position, and zip outputs.

## Glossary

- Human-in-the-loop: the user reviews and approves meaningful design changes.
- Tool layer: local code that translates agent requests into KiCad API or CLI calls.
- Transaction: a group of related changes that can be reviewed and ideally undone together.
- Approved part: a component that is allowed by project policy or template metadata.
- Board summary: a machine-readable snapshot of project, board, components, nets, constraints, and checks.
- Manufacturing zip: a package of production files suitable for PCBWay, JLCPCB, or similar services.

## V1 Boundary

V1 should prove the end-to-end loop on one simple board. It should favor narrow tools with clear behavior over broad autonomous design.

The most important V1 capabilities are:

- create a project from a known template
- inspect a KiCad project
- place and move footprints using millimeter coordinates
- set spacing between two components
- run ERC and DRC
- parse and summarize reports
- export manufacturing files
- keep human review explicit

