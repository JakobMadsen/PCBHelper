# VS Code / Copilot MCP Setup

PCBHelper exposes its first agent integration as a local MCP stdio server.

This repository includes `.vscode/mcp.json`, so opening the repository root in VS Code should make a `pcbhelper` MCP server available to VS Code-compatible MCP clients.

The checked-in config is:

```json
{
  "servers": {
    "pcbhelper": {
      "type": "stdio",
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "src/PCBHelper.Mcp"
      ]
    }
  }
}
```

Then start the server from VS Code's MCP UI and use Copilot Agent Mode.

For the first end-to-end agent trial, follow [Agent smoke test](agent-smoke-test.md).

First exposed tools:

- `doctor`
- `get_project_summary`
- `get_board_summary`
- `list_components`
- `get_component_value`
- `set_component_value_preview`
- `set_component_value`
- `measure_distance`
- `move_component_preview`
- `move_component`
- `set_component_spacing_preview`
- `set_component_spacing`
- `restore_change_preview`
- `restore_change`
- `list_recent_changes`
- `get_change_report`
- `list_nets`
- `get_net_summary`
- `list_footprint_pads`
- `run_erc`
- `run_drc`
- `run_checks`
- `get_check_summary`
- `export_gerbers`
- `export_drill`
- `export_manufacturing_files`
- `export_manufacturing_zip`
- `export_bom`
- `export_position_files`
- `open_project_in_kicad`
- `get_kicad_gui_capabilities`
- `refresh_project_in_kicad`
- `focus_component_in_kicad`

No AI provider keys are required by PCBHelper itself. Copilot, Codex, Ollama, or any future client is responsible for its own model configuration.

Real mutation tools create `.pcbhelper/changes/<change-id>/change.json` so the human can review exactly what changed and ask the agent to restore the previous footprint placement or component value.

Current KiCad 10.0.4 Windows builds tested here do not expose `kicad-cli api-server`. PCBHelper therefore reports `KICAD_IPC_UNAVAILABLE` for live GUI refresh/focus instead of pretending that file edits were pushed into the running KiCad window. The fallback is to reload or reopen the project in KiCad.
