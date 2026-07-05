# VS Code / Copilot MCP Setup

PCBHelper exposes its first agent integration as a local MCP stdio server.

Use this with VS Code Copilot Agent Mode by adding an MCP server entry similar to this in `.vscode/mcp.json`:

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

First exposed tools:

- `doctor`
- `get_project_summary`
- `get_board_summary`
- `measure_distance`
- `move_component_preview`
- `move_component`
- `set_component_spacing_preview`
- `set_component_spacing`
- `restore_change_preview`
- `restore_change`
- `run_erc`
- `run_drc`
- `run_checks`
- `export_gerbers`
- `export_drill`
- `export_manufacturing_files`
- `export_manufacturing_zip`
- `open_project_in_kicad`

No AI provider keys are required by PCBHelper itself. Copilot, Codex, Ollama, or any future client is responsible for its own model configuration.

Real mutation tools create `.pcbhelper/changes/<change-id>/change.json` so the human can review exactly what moved and ask the agent to restore the previous footprint placement.
