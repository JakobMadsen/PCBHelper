# Architecture Sketch

## Overview

PCBHelper is a local tool layer between an AI agent and KiCad.

```text
Agent client
  -> MCP server or local tool host
  -> KiCad adapter
  -> KiCad project files, KiCad GUI, kicad-cli
```

The agent does not manipulate the GUI directly. It calls explicit tools. The tools read or modify KiCad project files through KiCad-supported APIs and command line commands.

## Proposed Modules

### Agent Tool Server

Exposes a stable tool interface to Codex or another compatible agent.

Responsibilities:

- define tool schemas
- validate inputs
- call the KiCad service layer
- return structured results
- distinguish read-only and mutating operations

### KiCad Project Service

Owns project discovery, file paths, templates, and project metadata.

Responsibilities:

- create project from template
- locate schematic, board, rules, and output paths
- read project summaries
- prevent operations outside the selected project root

### Board Geometry Service

Owns coordinates, measurements, footprint lookup, and placement constraints.

Responsibilities:

- convert between KiCad units and millimeters
- find footprint anchors
- place and move footprints
- measure distances
- set spacing between components

### Check Runner

Runs and parses KiCad checks.

Responsibilities:

- run ERC
- run DRC
- parse reports
- classify errors, warnings, and informational messages
- produce human-readable summaries

### Export Service

Creates manufacturing artifacts.

Responsibilities:

- export Gerbers
- export drill files
- export BOM
- export position files
- assemble manufacturing zip
- validate expected outputs exist

### Approved Parts And Templates

Constrain what the agent can create or modify.

Responsibilities:

- list approved templates
- list approved parts and footprints
- expose metadata such as allowed substitutions
- prevent free-form component selection in V1

## Trust Boundary

The tool server is trusted local code running on the user's machine. The agent is not trusted to directly edit arbitrary files. Tool implementations should validate paths, inputs, and operation scope.

Important boundaries:

- AI provider credentials stay outside the repository.
- KiCad project operations are scoped to the selected project root.
- Mutating operations should report what changed.
- Manufacturing exports should be reproducible.

## Transaction Model

V1 may start with conservative before/after snapshots instead of a full undo engine.

Desired behavior:

1. agent proposes operation
2. tool validates scope
3. tool records relevant before state
4. tool applies a small change
5. tool returns changed objects and check status
6. user reviews in KiCad
7. user accepts, requests follow-up, or reverts through KiCad/git/tool support

Future versions can add explicit transaction IDs and rollback tools.

## Open Architecture Questions

- Which KiCad version is the first supported target?
- Is KiCad's Python API sufficient for highlighting, or is IPC required?
- Should the first MCP server be written in Python, TypeScript, or another runtime?
- Should board changes be committed to git after each accepted transaction?
- How should templates and approved parts be versioned?

