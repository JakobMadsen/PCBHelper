# PCBHelper

PCBHelper is an experimental conversational tool for turning small, simple electronics ideas into reviewable, verified, manufacturer-ready PCBs.

The goal is not to replace an electronics engineer or generate arbitrary PCBs from text. The goal is to let a person without KiCad expertise describe a constrained board to an AI, review understandable decisions and evidence, and receive a PCBWay-ready package. KiCad remains the internal source of truth and an optional expert review surface.

> **v0.1-alpha target:** Windows 11 x64, KiCad 10, and VS Code/Copilot or another MCP-compatible agent. PCBHelper is experimental and does not guarantee that a physical circuit works.

## 10 Minute Quick Start

1. Install [KiCad 10](https://www.kicad.org/download/windows/) and confirm `kicad-cli` is available.
2. Download and extract the self-contained Windows alpha ZIP, or build the solution with .NET 10.
3. Run the environment check:

```powershell
./pcbhelper.exe doctor --json
```

4. Authorize the directories that PCBHelper MCP may access, then restart VS Code:

```powershell
setx PCBHELPER_ALLOWED_ROOTS "C:\PCB;C:\Users\Public\Documents\PCBHelper"
```

5. Add `mcp.example.json` to your client's MCP configuration and update the executable path.
6. Ask the agent:

```text
Use PCBHelper on the included kicad-getting-started-led fixture.
Read the agent guide and capabilities, summarize the project, preview changing R1 to 300R,
apply the exact previewed plan, run the engineering gate, and restore the transaction.
```

Source checkout users can run the same workflow with `dotnet run --project src/PCBHelper.Cli -- doctor` and the checked-in `.vscode/mcp.json`.

Docker is optional and intended for clean-room development tests. It is not required by normal Windows users:

```powershell
./scripts/Test-DockerCleanRoom.ps1 -Target core-test
./scripts/Test-DockerCleanRoom.ps1 -Target eda-test
```

See the [support matrix](docs/support-matrix.md), [agent guide](docs/agent-guide-v1.md), and [security policy](.github/SECURITY.md) before using PCBHelper on a new design.

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

The human stays in the loop by approving intended function, dimensions, connectors, component costs and risks, and final release. Codex may propose parts and operate KiCad, but deterministic tools must verify identity, footprint and pin compatibility, engineering checks, and manufacturing readiness. Expensive, scarce, obsolete, or uncertain parts require a conversation before design lock.

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
- choose components without verified identity, compatibility, sourcing evidence, and project approval
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
- `pcbhelper restore-change <project-path> --change <change-id-or-path>`
- `pcbhelper list-changes <project-path>`
- `pcbhelper show-change <project-path> --change <change-id-or-path>`
- `pcbhelper list-components <project-path>`
- `pcbhelper get-value <project-path> --ref <ref>`
- `pcbhelper set-value <project-path> --ref <ref> --value <value>`
- `pcbhelper list-nets <project-path>`
- `pcbhelper get-net <project-path> --net <name-or-code>`
- `pcbhelper list-footprint-pads <project-path> --ref <ref>`
- `pcbhelper list-tracks <project-path>`
- `pcbhelper list-vias <project-path>`
- `pcbhelper get-net-routing <project-path> --net <name-or-code>`
- `pcbhelper add-track <project-path> --net <name-or-code> --start-x <mm> --start-y <mm> --end-x <mm> --end-y <mm> --layer F.Cu|B.Cu --width <mm>`
- `pcbhelper delete-track <project-path> --track <uuid-or-id>`
- `pcbhelper add-via <project-path> --net <name-or-code> --x <mm> --y <mm> --size <mm> --drill <mm> --layers F.Cu,B.Cu`
- `pcbhelper delete-via <project-path> --via <uuid-or-id>`
- `pcbhelper list-schematic-symbols <project-path>`
- `pcbhelper create-schematic-symbol <project-path> --symbol <catalog-id> --ref <ref> --x <mm> --y <mm> [--unit <n>]`
- `pcbhelper set-symbol-field <project-path> --ref <ref> --field <name> --value <value>`
- `pcbhelper connect-schematic-pins <project-path> --from <ref.pin|ref:pin> --to <ref.pin|ref:pin> --net <name>`
- `pcbhelper add-net-label <project-path> --net <name> --x <mm> --y <mm>`
- `pcbhelper delete-net-label-by-uuid <project-path> --uuid <uuid>`
- `pcbhelper delete-net-label <project-path> --net <name> --x <mm> --y <mm> [--tolerance <mm>]`
- `pcbhelper delete-schematic-wire-by-uuid <project-path> --uuid <uuid>`
- `pcbhelper delete-schematic-wire <project-path> --x1 <mm> --y1 <mm> --x2 <mm> --y2 <mm> [--tolerance <mm>]`
- `pcbhelper update-pcb-from-schematic <project-path>`
- `pcbhelper regenerate-board-footprint <project-path> --ref <ref>`
- `pcbhelper list-tests <project-path>`
- `pcbhelper validate-tests <project-path>`
- `pcbhelper evaluate-test-results <project-path> --results <path>`
- `pcbhelper export <project-path>`
- `pcbhelper export-bom <project-path>`
- `pcbhelper export-position-files <project-path>`
- `pcbhelper package <project-path>`
- `pcbhelper export-assembly-bom <project-path>`
- `pcbhelper export-cpl <project-path>`
- `pcbhelper validate-assembly-package <project-path>`
- `pcbhelper package-assembly <project-path>`
- `pcbhelper open <project-path>`
- `pcbhelper kicad-gui-status <project-path>`
- `pcbhelper refresh-gui <project-path>`
- `pcbhelper focus-component <project-path> --ref <ref>`
- `pcbhelper plan validate|preview|apply <project-path> --file <plan.json>`
- `pcbhelper transaction show|restore <project-path> --id <transaction-id>`
- MCP stdio server for VS Code/Copilot-compatible clients
- unit, contract, and headless E2E test projects

Start with:

- [Project context](CONTEXT.md)
- [Product requirements](docs/specs/prd.md)
- [Architecture sketch](docs/specs/architecture.md)
- [MCP tool contract](docs/specs/mcp-tool-contract.md)
- [Design Plan V1](docs/specs/design-plan-v1.md)
- [Simulation assertion tests](docs/specs/simulation-assertion-tests.md)
- [Board finishing and PCBWay release](docs/specs/board-finishing-and-release.md)
- [Hello World board spec](docs/specs/hello-world-optical-board.md)
- [Roadmap](docs/roadmap.md)
- [Testing strategy](docs/testing-strategy.md)
- [Implementation decisions](docs/decisions.md)
- [VS Code / Copilot MCP setup](docs/copilot-mcp.md)
- [Agent smoke test](docs/agent-smoke-test.md)
- [Agent Guide V1](docs/agent-guide-v1.md)
- [Agent evaluation scenarios](docs/agent-evals.md)
- [Security and secrets](docs/security.md)
- [Support matrix](docs/support-matrix.md)
- [Contributing](CONTRIBUTING.md)
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
dotnet run --project src/PCBHelper.Cli -- restore-change fixtures/kicad-getting-started-led --change <change-id-or-path> --dry-run
dotnet run --project src/PCBHelper.Cli -- list-components fixtures/kicad-getting-started-led
dotnet run --project src/PCBHelper.Cli -- get-value fixtures/kicad-getting-started-led --ref R1
dotnet run --project src/PCBHelper.Cli -- set-value fixtures/kicad-getting-started-led --ref R1 --value 300R --dry-run
dotnet run --project src/PCBHelper.Cli -- list-nets fixtures/kicad-getting-started-led
dotnet run --project src/PCBHelper.Cli -- get-net-routing fixtures/kicad-getting-started-led --net LED_A
dotnet run --project src/PCBHelper.Cli -- add-track fixtures/kicad-getting-started-led --net LED_A --start-x 10 --start-y 10 --end-x 20 --end-y 10 --layer F.Cu --width 0.25 --dry-run
dotnet run --project src/PCBHelper.Cli -- add-via fixtures/kicad-getting-started-led --net GND --x 73 --y 45 --size 1.2 --drill 0.6 --layers F.Cu,B.Cu --dry-run
dotnet run --project src/PCBHelper.Cli -- list-schematic-symbols fixtures/blank-authoring
dotnet run --project src/PCBHelper.Cli -- create-schematic-symbol fixtures/blank-authoring --symbol Device:R --ref R1 --x 50 --y 50 --value 330R --dry-run
dotnet run --project src/PCBHelper.Cli -- connect-schematic-pins fixtures/blank-authoring --from R1.1 --to R1.2 --net LOOP --dry-run
dotnet run --project src/PCBHelper.Cli -- connect-schematic-pins fixtures/blank-authoring --from R1:1 --to R1:2 --net LOOP --dry-run
dotnet run --project src/PCBHelper.Cli -- update-pcb-from-schematic fixtures/blank-authoring --dry-run
dotnet run --project src/PCBHelper.Cli -- validate-tests fixtures/simulation-assertions
dotnet run --project src/PCBHelper.Cli -- evaluate-test-results fixtures/simulation-assertions --results fixtures/simulation-assertions/measurements-pass.json
dotnet run --project src/PCBHelper.Cli -- simulation status --json
dotnet run --project src/PCBHelper.Cli -- simulation validate fixtures/simulation-ngspice-rc --json
dotnet run --project src/PCBHelper.Cli -- simulation run fixtures/simulation-ngspice-rc --json
dotnet run --project src/PCBHelper.Cli -- export fixtures/kicad-getting-started-led
dotnet run --project src/PCBHelper.Cli -- export-bom fixtures/kicad-getting-started-led
dotnet run --project src/PCBHelper.Cli -- export-position-files fixtures/kicad-getting-started-led
dotnet run --project src/PCBHelper.Cli -- package fixtures/kicad-getting-started-led
dotnet run --project src/PCBHelper.Cli -- kicad-gui-status fixtures/kicad-getting-started-led
dotnet run --project src/PCBHelper.Cli -- open fixtures/kicad-getting-started-led --dry-run
```

Add `--json` to any command for structured output.

The `simulation` commands use an optional ngspice installation discovered through `NGSPICE`, PATH, or known
Windows locations. PCBHelper generates constrained simulator input, stores artifacts under `.pcbhelper/simulations/`,
and evaluates numeric assertions itself. A missing simulator is reported as unavailable, never as a passing test.

Real `move`, `set-spacing`, `set-value`, routing add/delete, schematic authoring, PCB update, and `restore-change` operations write a review report under `.pcbhelper/changes/<change-id>/change.json` and run KiCad checks after the edit. Dry-runs report proposed before/after values without writing project files or a change report.

Routing V1 is intentionally primitive: it can inspect tracks/vias and add/delete straight segments or through vias. It is not an autorouter.

Schematic authoring V1 is intentionally catalog-based: it can place approved LED/resistor/battery symbols, set fields, draw simple wires/labels, and create missing template footprints. It is not arbitrary KiCad library synthesis.

## License

PCBHelper is licensed under the [Apache License 2.0](LICENSE).

## Alpha Limitations

- Small, simple, low-voltage two-layer boards only.
- ERC and DRC do not prove electrical function; simulation is evidence, not a physical guarantee.
- KiCad GUI refresh is capability-gated, and file changes may require an explicit reload.
- Copper-zone refill may require opening and saving the board in KiCad.
- PCBHelper generates review and manufacturing artifacts but never places orders, pays, publishes, or approves substitutions.
- Linux is used for clean-room CI but is not an official v0.1 user platform.
