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
      ],
      "env": {
        "PCBHELPER_MCP_PROFILE": "workflow",
        "PCBHELPER_ALLOWED_ROOTS": "${env:PCBHELPER_ALLOWED_ROOTS}"
      }
    }
  }
}
```

Then start the server from VS Code's MCP UI and use Copilot Agent Mode.

Copilot should read the canonical `pcbhelper://agent-guide/v1` resource at the start of a PCBHelper task. Clients without MCP resource support can call `get_agent_guide`. Then call `get_capabilities` rather than guessing Design Plan operation fields; the machine-readable schema is also available at `pcbhelper://design-plan/v1/schema`.

For the first end-to-end agent trial, follow [Agent smoke test](agent-smoke-test.md).

The default `workflow` profile deliberately exposes only:

- `get_capabilities`
- `get_agent_guide`
- `get_project_context`
- `validate_design_plan`
- `preview_design_plan`
- `apply_design_plan`
- `get_transaction`
- `restore_transaction`
- `run_engineering_gate`
- `analyze_design_intent`
- `get_design_intent_report`
- `generate_review_package`
- `generate_pcbway_package`
- `generate_pcbway_release`
- `validate_release_requirements`
- `refill_zones`
- `get_simulation_capabilities`
- `validate_simulation_tests`
- `run_simulation_tests`
- `get_simulation_report`
- `validate_kicad_simulation_models`
- `export_kicad_spice_netlist`
- `run_simulation_sweep`

Set `PCBHELPER_MCP_PROFILE=legacy` for the primitive debugging surface, or `all` for both. MCP project access is denied unless `PCBHELPER_ALLOWED_ROOTS` contains one or more semicolon-separated authorized roots. Configure it as a user-level environment variable so private local paths are not committed to the public repository. For example: `C:\projects\PCBHelper;D:\projects`.

Legacy tools include:

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
- `list_unrouted_connections`
- `validate_track_clearance`
- `add_track_preview`
- `add_track`
- `add_track_polyline_preview`
- `add_track_polyline`
- `delete_track_preview`
- `delete_track`
- `add_via_preview`
- `add_via`
- `delete_via_preview`
- `delete_via`
- `setup_freerouting_preview`
- `setup_freerouting`
- `autoroute_board_preview`
- `autoroute_board`
- `list_schematic_symbols`
- `create_schematic_symbol_preview`
- `create_schematic_symbol`
- `set_symbol_field_preview`
- `set_symbol_field`
- `connect_schematic_pins_preview`
- `connect_schematic_pins`
- `add_net_label_preview`
- `add_net_label`
- `delete_net_label_by_uuid_preview`
- `delete_net_label_by_uuid`
- `delete_net_label_preview`
- `delete_net_label`
- `delete_schematic_wire_by_uuid_preview`
- `delete_schematic_wire_by_uuid`
- `delete_schematic_wire_preview`
- `delete_schematic_wire`
- `update_pcb_from_schematic_preview`
- `update_pcb_from_schematic`
- `regenerate_board_footprint_preview`
- `regenerate_board_footprint`
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
- `export_assembly_bom`
- `export_cpl`
- `validate_assembly_package`
- `export_pcbway_assembly_package`
- `list_test_specs`
- `validate_test_specs`
- `evaluate_test_results`
- `open_project_in_kicad`
- `get_kicad_gui_capabilities`
- `refresh_project_in_kicad`
- `focus_component_in_kicad`

No AI provider keys are required by PCBHelper itself. Copilot, Codex, Ollama, or any future client is responsible for its own model configuration.

Real mutation tools create `.pcbhelper/changes/<change-id>/change.json` so the human can review exactly what changed and ask the agent to restore the previous footprint placement, component value, track, via, schematic edit, or PCB update.

Routing tools are primitive V1 operations plus a guarded FreeRouting backend. Prefer `list_unrouted_connections` -> `validate_track_clearance` -> `add_track_polyline_preview` -> `add_track_polyline` for manual/agent routing. `add_track` remains a low-level straight-segment primitive. If FreeRouting is missing, use `setup_freerouting_preview` / `setup_freerouting` to download the latest FreeRouting JAR into PCBHelper's local tools cache. `autoroute_board_preview` / `autoroute_board` use the local FreeRouting DSN/SES backend only when KiCad CLI, FreeRouting, Java, and board outline prerequisites are available.

For PCBWay assembly output, prefer `get_check_summary` -> `export_assembly_bom` -> `export_cpl` -> `validate_assembly_package` -> `export_pcbway_assembly_package`. The older `export_bom` and `export_position_files` tools remain raw KiCad exports; the assembly tools add grouping, DNP handling, BOM/CPL consistency checks, part-number warnings, and orientation-review diagnostics.

Schematic authoring tools are primitive V1 operations. They can place approved catalog symbols, set symbol fields, connect known pins, add labels, and create missing template footprints from the schematic; they do not synthesize arbitrary KiCad libraries.

Legacy simulation assertion tools read JSON test specs from `.pcbhelper/tests/*.json` and evaluate external
measurement files. The workflow profile additionally exposes `get_simulation_capabilities`,
`validate_simulation_tests`, `run_simulation_tests`, and `get_simulation_report`. These run constrained,
project-contained SPICE fixtures through ngspice when `NGSPICE` or PATH makes it available; they never accept raw
simulator commands.

Current KiCad 10.0.4 Windows builds tested here do not expose `kicad-cli api-server`. PCBHelper therefore reports `KICAD_IPC_UNAVAILABLE` for live GUI refresh/focus instead of pretending that file edits were pushed into the running KiCad window. The fallback is to reload or reopen the project in KiCad.
