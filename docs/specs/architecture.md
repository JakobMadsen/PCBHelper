# Architecture Sketch

## Overview

PCBHelper is a local tool layer between an AI agent and KiCad.

```text
Conversation client
  -> MCP server or local tool host
  -> constrained design workflow
  -> project transaction and engineering gate
  -> KiCad and component-data adapters
  -> review package and manufacturer outputs
```

The user describes intent and reviews product-level decisions. The agent calls explicit tools and must not bypass project policy. KiCad is an internal source of truth and optional expert surface. Component claims and release decisions require deterministic evidence outside the LLM.

## Proposed Modules

### Agent Tool Server

Exposes a stable tool interface to Codex or another compatible agent.

Responsibilities:

- define tool schemas
- validate inputs
- call the KiCad service layer
- return structured results
- distinguish read-only and mutating operations
- expose only the tool profile needed for the current workflow
- mark destructive, read-only, and external-network behavior explicitly

### Design Workflow

Owns the beginner-facing path from intent to a constrained design.

Responsibilities:

- clarify function, power, interfaces, mechanics, quantity, and budget
- select supported design recipes
- identify unsupported or safety-critical requests
- create an understandable design plan before mutation
- coordinate part evaluation, KiCad operations, engineering gates, and review

### Part Evaluation And Project Policy

Allows AI-assisted selection without treating an LLM guess as engineering evidence.

Responsibilities:

- gather replaceable supplier, manufacturer, and datasheet evidence
- validate symbol, footprint, package, pin-map, ratings, and manufacturer identity
- evaluate price, stock, MOQ, lifecycle, sourcing, and assembly suitability
- automatically approve candidates only inside project policy
- request user approval for expensive, scarce, obsolete, unusual, or uncertain candidates
- write approved evidence and decisions into a reproducible project design lock

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
- provide known design recipes and starter parts
- expose metadata such as allowed substitutions
- feed candidates into project-specific part evaluation rather than acting as a permanent global whitelist

### Project Transaction

Owns every mutating operation as a project-scoped, reviewable transaction.

Responsibilities:

- enforce authorized project roots and path containment
- validate before-state hashes
- apply atomic file changes
- record versioned typed change reports using project-relative paths
- run engineering policy after mutation
- commit, roll back, or record an explicit incomplete state

### Engineering Gate

Returns typed evidence instead of requiring the agent to interpret prose.

Responsibilities:

- distinguish passed, findings present, unavailable, and execution failed
- combine ERC, DRC, simulation assertions, part policy, and manufacturing validation
- define which failures block release and which require user approval

### Review Package

Translates KiCad and engineering data into beginner-readable approval material.

Responsibilities:

- render board and schematic previews
- explain dimensions, connectors, power, intended behavior, and component choices
- summarize BOM cost, availability, checks, and unresolved uncertainty
- make KiCad review optional while preserving an expert escape hatch

## Trust Boundary

The tool server is trusted local code running on the user's machine. The agent is not trusted to directly edit arbitrary files. Tool implementations should validate paths, inputs, and operation scope.

Important boundaries:

- AI provider credentials stay outside the repository.
- KiCad project operations are scoped to configured authorized roots and the selected project root.
- Component data from the network is evidence, not executable project content.
- Part approval cannot rely solely on LLM output.
- Mutating operations should report what changed.
- Manufacturing exports should be reproducible.

## Transaction Model

Mutations use project-scoped transactions. Whole-file snapshots may be used initially, but reports store project-relative file identities and hashes and may never authorize writes outside the project root.

Desired behavior:

1. agent proposes operation
2. tool validates scope
3. tool records relevant before state
4. transaction verifies expected file hashes and applies atomic changes
5. engineering gate evaluates the changed project
6. tool returns changed objects, typed evidence, and beginner-readable review artifacts
7. user accepts, requests follow-up, or restores through PCBHelper; KiCad remains optional for expert review

Release approval and ordering remain separate actions. V1 prepares files but does not submit an order.

## Open Architecture Questions

- Which part and supplier data sources provide sufficiently current price, stock, lifecycle, and PCBWay assembly evidence?
- Which project-policy thresholds should have safe defaults, and which must be supplied by the user?
- Which design recipes form the smallest useful V1 catalog?
- Should board changes be committed to git after each accepted transaction?
- How should project design locks and evidence freshness be versioned?
