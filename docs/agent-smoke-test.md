# Agent Smoke Test

This is the first human-in-the-loop test for PCBHelper through an agent client.

The goal is to prove that a human can ask an agent to inspect, preview, mutate, check, review, and restore a KiCad board through PCBHelper MCP tools.

## Preconditions

- .NET 10 SDK is installed.
- KiCad 9+ is installed.
- `kicad-cli` is discoverable by PCBHelper through `PATH`, `KICAD_CLI`, or a known KiCad install location.
- The repository is open as the workspace root in VS Code, Codex CLI, or the Codex IDE extension.
- KiCad is allowed to reload project files changed outside the GUI.

Run this first:

```powershell
dotnet run --project src/PCBHelper.Cli -- doctor
```

## VS Code / Copilot Setup

This repository includes `.vscode/mcp.json`, so VS Code-compatible MCP clients can start PCBHelper with:

```text
dotnet run --project src/PCBHelper.Mcp
```

Start the `pcbhelper` MCP server from VS Code's MCP UI, then use Copilot Agent Mode.

## Codex Setup

This repository includes `.codex/config.toml` with a project-scoped `pcbhelper` MCP server.

Codex CLI and the Codex IDE extension share MCP configuration. In a trusted checkout, start a new Codex session in this repository and use `/mcp` or the MCP settings UI to confirm that `pcbhelper` is active.

## Test Board

Use the beginner fixture:

```text
fixtures/kicad-getting-started-led
```

Open it in KiCad before the agent test:

```powershell
dotnet run --project src/PCBHelper.Cli -- open fixtures/kicad-getting-started-led
```

KiCad may ask whether to reload files after PCBHelper writes the board. Accept the reload when prompted.

## Agent Prompt

Use one prompt like this:

```text
Use PCBHelper MCP tools on fixtures/kicad-getting-started-led.

1. Run doctor.
2. Get the project summary and board summary.
3. Measure R1 to D1.
4. Preview setting D1 to 25 mm from R1 on the x-axis.
5. If the preview is reasonable, apply the spacing change.
6. Run checks.
7. Report the change report path.
8. Ask me to visually inspect KiCad before restoring anything.
```

Expected result:

- The agent uses PCBHelper tools instead of shelling out directly.
- `D1` moves from about `68 mm` X to about `70 mm` X.
- PCBHelper writes `.pcbhelper/changes/<change-id>/change.json`.
- Checks run after the real mutation.
- The agent pauses for human visual inspection before restore.

## Restore Prompt

After visual inspection in KiCad, ask:

```text
Use PCBHelper to restore the last change report, then run board summary and checks again.
```

Expected result:

- `D1` returns to about `68 mm` X.
- A new restore change report is written.
- Checks still run.

## Pass Criteria

The smoke test passes when:

- The agent can see PCBHelper MCP tools.
- The agent can summarize the tutorial project.
- The agent can preview before mutation.
- The agent can apply one controlled footprint placement change.
- The human can see the changed placement in KiCad after reload/reopen.
- The agent can restore from the change report.
- The board still passes the headless check/export/package pipeline.

## Known Limitation

PCBHelper currently edits the KiCad board file and relies on KiCad reload behavior. It does not yet use KiCad IPC to move footprints live inside the running GUI.
