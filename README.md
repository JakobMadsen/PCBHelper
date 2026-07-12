# PCBHelper

PCBHelper is a local, open-source bridge between an AI agent and KiCad for designing small, simple, reviewable PCBs through conversation.

Describe what a low-voltage board should do. The agent builds a constrained Design Plan, PCBHelper previews and applies it as a reversible transaction, and deterministic tools run checks, simulation, design-intent verification, and manufacturing export. You do not need to know KiCad for the normal workflow, but every result remains a real KiCad project that an expert can inspect.

> **Public alpha:** Windows 11 x64, KiCad 10, and an MCP-compatible client such as VS Code with GitHub Copilot. PCBHelper reduces risk; it does not guarantee that a physical circuit works.

## What It Does

- Reads KiCad projects, components, nets, footprints, routing, and board geometry.
- Creates and edits supported schematic symbols, values, connections, footprints, placement, routing primitives, zones, and testpoints.
- Groups changes into hash-bound Design Plans with preview, atomic apply, and conflict-safe restore.
- Runs KiCad ERC/DRC and creates Gerber, drill, BOM, CPL, review, and PCBWay release packages.
- Runs deterministic ngspice assertions for operating-point, AC, transient, tolerance, battery, and noise scenarios.
- Checks declared design intent: common circuit mistakes, ADC ranges, test access, connector requirements, and sourced component ratings.
- Reports what was proved, what failed, and what remains unknown instead of asking an LLM to grade its own work.

PCBHelper never places an order, pays, publishes a design, or approves a component substitution.

## How It Works

```text
Conversation
    |
    v
AI agent through MCP
    |
    v
PCBHelper Design Plan -> preview -> reversible transaction
    |
    +-> KiCad project files and kicad-cli
    +-> ngspice simulation assertions
    +-> Design Intent verification
    +-> ERC / DRC / manufacturing gates
    |
    v
Review package and manufacturer files
```

The AI proposes and explains. PCBHelper performs structured, reproducible operations and evaluates the resulting evidence. Routine reversible work is autonomous; the user is involved for ambiguous requirements, material risk, unusual parts, release decisions, orders, and payment.

## 10-Minute Quick Start

### 1. Install KiCad

Install [KiCad 10 for Windows](https://www.kicad.org/download/windows/). PCBHelper discovers `kicad-cli` through `KICAD_CLI`, `PATH`, or known installation locations.

### 2. Download PCBHelper

Download and extract a [Windows alpha release](https://github.com/JakobMadsen/PCBHelper/releases). The ZIP is self-contained; .NET is only required when building from source.

Run:

```powershell
./pcbhelper.exe doctor --json
```

### 3. Authorize Project Directories

The MCP server refuses project access until you explicitly authorize one or more roots:

```powershell
setx PCBHELPER_ALLOWED_ROOTS "C:\PCB;C:\Projects\Boards"
```

Restart VS Code after changing the environment variable.

### 4. Configure MCP

Point your MCP client at the extracted executable:

```json
{
  "servers": {
    "pcbhelper": {
      "type": "stdio",
      "command": "C:\\Tools\\PCBHelper\\PCBHelper.Mcp.exe",
      "env": {
        "PCBHELPER_MCP_PROFILE": "workflow",
        "PCBHELPER_ALLOWED_ROOTS": "C:\\PCB;C:\\Projects\\Boards"
      }
    }
  }
}
```

The `workflow` profile is the supported product interface. The larger `legacy` profile exists for development and debugging.

### 5. Try the Included Fixture

```text
Use PCBHelper on the included kicad-getting-started-led fixture.
Read the agent guide and capabilities, summarize the project, preview changing R1 to 300R,
apply the exact previewed plan, run the engineering gate, and restore the transaction.
```

The canonical agent workflow is documented in the [Agent Guide](docs/agent-guide-v1.md) and [Copilot setup guide](docs/copilot-mcp.md).

## Design Plans

Normal mutations are submitted as one declarative JSON plan rather than a sequence of improvised shell or GUI actions:

```json
{
  "version": 1,
  "goal": "Change the indicator resistor and sensor spacing",
  "operations": [
    {
      "id": "resistor",
      "type": "set-component-value",
      "reference": "R1",
      "value": "300R"
    },
    {
      "id": "spacing",
      "type": "set-component-spacing",
      "fixedReference": "S1",
      "movingReference": "S2",
      "distanceMm": 15,
      "axis": "x"
    }
  ]
}
```

PCBHelper validates the plan, returns a canonical SHA-256 hash, prepares all file changes in isolation, and applies only the exact previewed hash. Each transaction records before/after snapshots and can be restored when the project has not changed unexpectedly.

## Engineering Evidence

PCBHelper deliberately separates different kinds of confidence:

| Check | Question it answers |
| --- | --- |
| ERC | Are schematic pins and connections electrically legal? |
| Design Intent | Does the circuit match the requirements we explicitly declared? |
| Simulation | Does the mathematical model satisfy numerical assertions? |
| DRC | Does the board obey connectivity and manufacturing geometry rules? |
| Test access | Can critical signals be measured on the assembled board? |
| Ratings | Do sourced component limits cover the declared or simulated load? |
| Manufacturing | Are Gerber, drill, BOM, and CPL outputs mutually consistent? |

No single check proves physical operation. Prototype assembly, visual inspection, and bench measurement remain part of responsible hardware development.

See [Design Intent Verification](docs/specs/design-intent-verification.md) and [Simulation Assertion Tests](docs/specs/simulation-assertion-tests.md).

## Build And Test From Source

Requirements: .NET 10 SDK. KiCad 10 and ngspice are needed for their respective E2E tests.

```powershell
dotnet build PCBHelper.slnx -c Release
dotnet test tests/PCBHelper.Core.Tests/PCBHelper.Core.Tests.csproj -c Release
dotnet test tests/PCBHelper.Contract.Tests/PCBHelper.Contract.Tests.csproj -c Release
```

Docker Desktop is optional and used only as a clean-room development environment:

```powershell
./scripts/Test-DockerCleanRoom.ps1 -Target core-test
./scripts/Test-DockerCleanRoom.ps1 -Target eda-test
```

The EDA image installs KiCad 10 and ngspice, then tests checks, Design Plans, simulation, export, and packaging from `git archive HEAD`. Docker is not required for normal Windows use.

## Scope And Limitations

PCBHelper currently targets small, simple, low-voltage, two-layer prototype boards.

It is not intended for:

- mains voltage, medical, safety-critical, RF, high-speed, or high-current design
- unconstrained component or footprint invention
- complex autorouting or guaranteed signal integrity
- replacing datasheets, engineering review, simulation models, or physical testing
- unattended ordering, payment, publication, or substitution approval

KiCad GUI refresh and zone refill are capability-gated. File changes may require reopening or reloading the project in KiCad. Linux is tested through clean-room CI but is not an official alpha user platform.

## Documentation

- [Product requirements](docs/specs/prd.md)
- [Architecture](docs/specs/architecture.md)
- [Design Plan V1](docs/specs/design-plan-v1.md)
- [MCP tool contract](docs/specs/mcp-tool-contract.md)
- [Design Intent Verification](docs/specs/design-intent-verification.md)
- [Simulation Assertion Tests](docs/specs/simulation-assertion-tests.md)
- [Board finishing and PCBWay release](docs/specs/board-finishing-and-release.md)
- [Testing strategy](docs/testing-strategy.md)
- [Support matrix](docs/support-matrix.md)
- [Security policy](.github/SECURITY.md)
- [Contributing](CONTRIBUTING.md)

## License

PCBHelper is licensed under the [Apache License 2.0](LICENSE).
