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
- `list_tracks`
- `list_vias`
- `get_net_routing`
- `add_track_preview`
- `add_track`
- `delete_track_preview`
- `delete_track`
- `add_via_preview`
- `add_via`
- `delete_via_preview`
- `delete_via`
- `list_schematic_symbols`
- `create_schematic_symbol_preview`
- `create_schematic_symbol`
- `set_symbol_field_preview`
- `set_symbol_field`
- `connect_schematic_pins_preview`
- `connect_schematic_pins`
- `add_net_label_preview`
- `add_net_label`
- `update_pcb_from_schematic_preview`
- `update_pcb_from_schematic`
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
- `list_test_specs`
- `validate_test_specs`
- `evaluate_test_results`
- `open_project_in_kicad`
- `get_kicad_gui_capabilities`
- `refresh_project_in_kicad`
- `focus_component_in_kicad`

No AI provider keys are required by PCBHelper itself. Copilot, Codex, Ollama, or any future client is responsible for its own model configuration.

Real mutation tools create `.pcbhelper/changes/<change-id>/change.json` so the human can review exactly what changed and ask the agent to restore the previous footprint placement, component value, track, via, schematic edit, or PCB update.

Routing tools are primitive V1 operations. They inspect tracks/vias and can add/delete straight segments or through vias; they do not autoroute.

Schematic authoring tools are primitive V1 operations. They can place approved catalog symbols, set symbol fields, connect known pins, add labels, and create missing template footprints from the schematic; they do not synthesize arbitrary KiCad libraries.

Simulation assertion tools are deterministic V0 operations. They read JSON test specs from `.pcbhelper/tests/*.json` and evaluate external measurement result JSON files; they do not run ngspice or KiCad SPICE export yet.

Current KiCad 10.0.4 Windows builds tested here do not expose `kicad-cli api-server`. PCBHelper therefore reports `KICAD_IPC_UNAVAILABLE` for live GUI refresh/focus instead of pretending that file edits were pushed into the running KiCad window. The fallback is to reload or reopen the project in KiCad.
