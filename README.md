# PCBHelper

PCBHelper is an experimental tool layer for using Codex as a human-in-the-loop KiCad operator.

The goal is not to replace an electronics engineer or generate perfect PCB designs from text. The goal is to let an AI coding agent perform small, precise, reviewable KiCad operations while a human keeps KiCad open, reviews the result visually, and decides what to accept.

## Project Thesis

Simple PCB work often takes too long because the mechanical steps are repetitive:

- creating projects from known templates
- reading footprints, nets, board outlines, and constraints
- placing components at exact distances
- running ERC and DRC
- interpreting reports
- exporting manufacturing files

PCBHelper aims to turn those steps into explicit tools that Codex can call through a local integration layer, likely an MCP server.

```text
Codex
  -> local MCP server / tool layer
  -> KiCad Python or IPC API and kicad-cli
  -> KiCad project files and KiCad GUI
```

The human stays in the loop. Codex may propose, move, measure, check, and export, but the user must be able to inspect the board in KiCad, approve or reject changes, and recover from mistakes.

## Bring Your Own Key

This repository is intended to be public and BYOK-friendly.

Users should be able to run the local KiCad tool layer with their own AI provider credentials, local agent, or Codex environment. The project should avoid hard-coding hosted accounts, API keys, or private services into the core KiCad automation layer.

Configuration belongs in local environment files, OS keychains, or user-specific agent settings. Secrets must not be committed.

## First Prototype

The first end-to-end demo is a small optical sensor PCB:

- two optical sensors or photodiodes
- exact 15 mm center-to-center spacing
- two matched analog channels
- connector pins for power, ground, AMP_A, and AMP_B
- ERC and DRC checks
- manufacturing export package

The point of this board is not to be the final product. It is a compact test of the full loop:

1. express requirements
2. create a KiCad project
3. place components precisely
4. enforce mechanical constraints
5. run electrical and layout checks
6. export production files
7. review everything in KiCad

## Non-Goals For V1

PCBHelper v1 is not a general AI electronics engineer.

It should not:

- design complex analog circuits freely
- do high-speed or RF layout
- autoroute complex boards
- choose arbitrary components from the internet
- order boards automatically
- hide file changes from the user
- depend on GUI clicking or screen scraping as the primary control method

The first version should be a controlled, template-based KiCad operator.

## Current Status

This repository contains the first implementation slice:

- .NET 10 solution structure
- `pcbhelper doctor`
- `pcbhelper summary <project-path>`
- `pcbhelper check <project-path>`
- `pcbhelper board-summary <project-path>`
- `pcbhelper measure <project-path> --from <ref> --to <ref>`
- `pcbhelper move <project-path> --ref <ref> --x <mm> --y <mm>`
- `pcbhelper set-spacing <project-path> --fixed <ref> --moving <ref> --distance <mm>`
- `pcbhelper export <project-path>`
- `pcbhelper package <project-path>`
- MCP stdio server for VS Code/Copilot-compatible clients
- unit, contract, and headless E2E test projects

Start with:

- [Project context](CONTEXT.md)
- [Product requirements](docs/specs/prd.md)
- [Architecture sketch](docs/specs/architecture.md)
- [MCP tool contract](docs/specs/mcp-tool-contract.md)
- [Hello World board spec](docs/specs/hello-world-optical-board.md)
- [Roadmap](docs/roadmap.md)
- [Testing strategy](docs/testing-strategy.md)
- [Implementation decisions](docs/decisions.md)
- [VS Code / Copilot MCP setup](docs/copilot-mcp.md)
- [Security and secrets](docs/security.md)
- [Open questions](docs/open-questions.md)

## V1 Targets

- Runtime: .NET 10 LTS
- KiCad: KiCad 9+
- First client: VS Code/Copilot through MCP stdio
- First KiCad integration: `kicad-cli`

## CLI Quick Start

```text
dotnet run --project src/PCBHelper.Cli -- doctor
dotnet run --project src/PCBHelper.Cli -- summary fixtures/minimal-board
dotnet run --project src/PCBHelper.Cli -- check fixtures/minimal-board
dotnet run --project src/PCBHelper.Cli -- board-summary fixtures/kicad-getting-started-led
dotnet run --project src/PCBHelper.Cli -- measure fixtures/kicad-getting-started-led --from R1 --to D1
dotnet run --project src/PCBHelper.Cli -- move fixtures/kicad-getting-started-led --ref D1 --x 75 --y 35 --dry-run
dotnet run --project src/PCBHelper.Cli -- set-spacing fixtures/kicad-getting-started-led --fixed R1 --moving D1 --distance 25 --axis x --dry-run
dotnet run --project src/PCBHelper.Cli -- export fixtures/kicad-getting-started-led
dotnet run --project src/PCBHelper.Cli -- package fixtures/kicad-getting-started-led
```

Add `--json` to any command for structured output.

## License

PCBHelper is licensed under the [Apache License 2.0](LICENSE).
