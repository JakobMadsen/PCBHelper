# MCP Tool Contract Draft

This document sketches the first tool surface. Names and schemas are provisional.

## Design Rules

- Tools should be narrow and explicit.
- Read-only tools should be clearly separate from mutating tools.
- Inputs should use millimeters at the public boundary.
- Outputs should include structured data plus a short human-readable summary.
- Mutating tools should return changed objects and enough context for review.
- Tools should validate that paths stay inside the selected project root.

## Project Tools

### `create_project_from_template`

Create a new KiCad project from an approved template.

Inputs:

- `template_id`
- `project_name`
- `destination_dir`
- optional template variables

Outputs:

- project path
- created files
- warnings

### `get_project_summary`

Read the project structure.

Inputs:

- `project_path`

Outputs:

- schematic path
- board path
- configured outputs path
- KiCad version if detectable
- template metadata if present

## Board Inspection Tools

### `get_board_summary`

Return components, footprints, nets, board outline, and basic board metadata.

Inputs:

- `project_path`

Outputs:

- board dimensions
- footprint list
- net list
- design rule summary
- known warnings

First implementation note: the current board summary is intentionally lightweight and reports fixture-level footprint references, sides, and positions.

### `list_components`

List components detected from the board and schematic files.

Inputs:

- `project_path`

Outputs:

- reference
- value, board value, and schematic value when available
- footprint name
- side and position when available
- pad count

### `get_component_value`

Read the value locations for one component.

Inputs:

- `project_path`
- `reference`

Outputs:

- matching board and/or schematic value locations
- exact value strings
- source file paths

### `list_nets`

List board nets and connected pads.

Inputs:

- `project_path`

Outputs:

- net code
- net name
- connected footprint pads

### `get_net_summary`

Read one board net by name or numeric code.

Inputs:

- `project_path`
- `net`

Outputs:

- net code
- net name
- connected pads

### `list_footprint_pads`

List pad-level data for one footprint.

Inputs:

- `project_path`
- `reference`

Outputs:

- pad names
- pad types
- local pad positions when detectable
- net names/codes
- pin functions when detectable
- absolute board position when detectable

### `list_tracks`

List top-level board track segments.

Inputs:

- `project_path`
- optional `net`

Outputs:

- id/uuid
- net name/code
- start/end coordinates
- layer
- width

### `list_vias`

List top-level board vias.

Inputs:

- `project_path`
- optional `net`

Outputs:

- id/uuid
- net name/code
- at coordinate
- size and drill
- layers

### `get_net_routing`

Return pads, tracks, and vias for one board net.

Inputs:

- `project_path`
- `net`

Outputs:

- resolved net
- connected pads with absolute board position
- matching tracks
- matching vias

First implementation note: routing inspection reads top-level `segment` and `via` objects only.

### `get_selected_items`

Return currently selected KiCad objects if supported by the chosen KiCad API or IPC layer.

Inputs:

- `project_path`

Outputs:

- selected components
- selected nets
- selected board items

## Placement And Measurement Tools

### `place_component`

Place a footprint at an absolute board coordinate.

Inputs:

- `project_path`
- `reference`
- `x_mm`
- `y_mm`
- optional `rotation_deg`
- optional `side`

Outputs:

- previous placement
- new placement
- change report path for real moves
- check summary and generated check report paths for real moves
- changed files or objects

### `move_component`

Move a footprint by a delta or to a target coordinate.

Inputs:

- `project_path`
- `reference`
- either absolute target or delta

Outputs:

- previous placement
- new placement

### `measure_distance`

Measure distance between two components or anchors.

Inputs:

- `project_path`
- `from`
- `to`
- optional anchor names

Outputs:

- distance in millimeters
- dx and dy in millimeters
- anchor points used

First implementation note: measurements are footprint top-level position to footprint top-level position.

### `set_component_spacing`

Move one component so two component anchors have a target center-to-center distance.

Inputs:

- `project_path`
- `fixed_reference`
- `moving_reference`
- `target_distance_mm`
- optional axis constraint

Outputs:

- previous distance
- new distance
- moved component
- change report path for real spacing changes
- check summary and generated check report paths for real spacing changes

First implementation note: spacing is axis-constrained and defaults to the X axis. It updates only the moving footprint's top-level `(at ...)`.

### `restore_change`

Restore a placement or component value from a PCBHelper change report.

Inputs:

- `project_path`
- change id or path to `change.json`

Outputs:

- source change id
- previous placement or value before restore
- restored placement or value
- new change report path for real restores

First implementation note: restore supports placement changes from `move_component`, `set_component_spacing`, value changes from `set_component_value`, and previous `restore_change` reports.

### `list_recent_changes`

List PCBHelper change reports for a project.

Inputs:

- `project_path`

Outputs:

- change ids
- operations
- references
- report paths
- restore commands

### `get_change_report`

Read one PCBHelper change report by id or path.

Inputs:

- `project_path`
- `change`

Outputs:

- full change report JSON model
- placement fields when present
- value fields when present

## Component Value Mutation Tools

### `set_component_value_preview`

Preview a component value edit without writing project files.

Inputs:

- `project_path`
- `reference`
- `value`
- optional `scope`: `available`, `schematic`, `board`, or `both`

Outputs:

- before value locations
- after value locations
- changed files that would be touched

### `set_component_value`

Change a component value and write a change report.

Inputs:

- `project_path`
- `reference`
- `value`
- optional `scope`: `available`, `schematic`, `board`, or `both`

Outputs:

- before value locations
- after value locations
- changed files
- change report path
- check summary and generated check report paths

First implementation note: PCBHelper preserves the exact value string; it does not normalize `300` to `300R`.

## Routing Mutation Tools

### `add_track_preview` / `add_track`

Preview or add one straight top-level track segment.

Inputs:

- `project_path`
- `net`
- `start_x_mm`, `start_y_mm`
- `end_x_mm`, `end_y_mm`
- `layer`: `F.Cu` or `B.Cu`
- `width_mm`

Outputs:

- proposed or written segment text
- changed file
- change report path for real changes
- DRC summary for real changes

### `delete_track_preview` / `delete_track`

Preview or delete one top-level track segment.

Inputs:

- `project_path`
- `track`

Outputs:

- original segment text
- changed file
- change report path for real changes
- DRC summary for real changes

### `add_via_preview` / `add_via`

Preview or add one through via.

Inputs:

- `project_path`
- `net`
- `x_mm`, `y_mm`
- `size_mm`
- `drill_mm`
- `layers`: currently `F.Cu,B.Cu`

Outputs:

- proposed or written via text
- changed file
- change report path for real changes
- DRC summary for real changes

### `delete_via_preview` / `delete_via`

Preview or delete one via.

Inputs:

- `project_path`
- `via`

Outputs:

- original via text
- changed file
- change report path for real changes
- DRC summary for real changes

First implementation note: routing V1 is not an autorouter. It supports only straight segments and through vias.

## Schematic Authoring Tools

### `list_schematic_symbols`

List placed schematic symbols and lightweight schematic counts.

Inputs:

- `project_path`

Outputs:

- schematic file
- symbol references, catalog ids, values, footprints, positions, and fields
- wire count
- label count

### `create_schematic_symbol_preview` / `create_schematic_symbol`

Preview or place one approved catalog symbol instance.

Inputs:

- `project_path`
- `symbol`: `Device:R`, `Device:LED`, `Device:D`, or `Device:Battery_Cell`
- `reference`
- `x` / `y` in millimeters
- optional `value`
- optional `footprint`

Outputs:

- proposed or written schematic text
- changed file snapshot
- change report path for real changes
- ERC/DRC report paths for real changes

First implementation note: this places an instance of an approved catalog symbol. It does not create a reusable KiCad library symbol.

### `set_symbol_field_preview` / `set_symbol_field`

Preview or set a field/property on a placed schematic symbol.

Inputs:

- `project_path`
- `reference`
- `field`
- `value`

Outputs:

- before/after file snapshot
- change report path for real changes
- ERC/DRC report paths for real changes

### `connect_schematic_pins_preview` / `connect_schematic_pins`

Preview or draw a simple Manhattan wire between two known catalog pins.

Inputs:

- `project_path`
- `from`: `<ref.pin>`
- `to`: `<ref.pin>`
- optional `net`

Outputs:

- proposed or written wire and optional label text
- changed file snapshot
- change report path for real changes
- ERC/DRC report paths for real changes

Stable errors include `SCHEMATIC_PIN_NOT_FOUND` for unknown references or pins.

### `add_net_label_preview` / `add_net_label`

Preview or add a schematic net label at a coordinate.

Inputs:

- `project_path`
- `net`
- `x` / `y` in millimeters

Outputs:

- proposed or written label text
- changed file snapshot
- change report path for real changes
- ERC/DRC report paths for real changes

### `update_pcb_from_schematic_preview` / `update_pcb_from_schematic`

Preview or create missing board footprints and board net declarations from the approved-catalog schematic.

Inputs:

- `project_path`

Outputs:

- changed board file snapshot
- number of created footprints in the summary
- change report path for real changes
- ERC/DRC report paths for real changes

First implementation note: PCB update lite preserves existing board placement/routing and creates only missing template footprints. It does not route, annotate, or run arbitrary KiCad library lookup.

## Visual Review Tools

### `highlight_net`

Highlight a net in KiCad if supported.

Inputs:

- `project_path`
- `net_name`

Outputs:

- highlighted objects
- fallback instructions if live highlighting is unavailable

### `highlight_component`

Highlight a component in KiCad if supported.

Inputs:

- `project_path`
- `reference`

Outputs:

- highlighted object
- fallback instructions if live highlighting is unavailable

### `open_project_in_kicad`

Open the local KiCad project in the KiCad GUI.

Inputs:

- `project_path`

Outputs:

- project file
- KiCad executable path
- process id if started

First implementation note: this launches KiCad with the `.kicad_pro` file and does not automate the GUI.

### `get_kicad_gui_capabilities`

Detect whether live KiCad GUI refresh/focus is available.

Inputs:

- `project_path`

Outputs:

- detected `kicad-cli` path
- detected KiCad GUI path
- whether IPC/API server support is available
- fallback guidance

### `refresh_project_in_kicad`

Request a live GUI refresh if supported by the installed KiCad build.

Inputs:

- `project_path`

Outputs:

- whether a live action was performed
- fallback guidance when unsupported

First implementation note: when IPC is unavailable this returns `KICAD_IPC_UNAVAILABLE` and does not pretend that file edits were refreshed in the GUI.

### `focus_component_in_kicad`

Focus a component in the KiCad GUI if supported by the installed KiCad build.

Inputs:

- `project_path`
- `reference`

Outputs:

- whether a live action was performed
- fallback guidance when unsupported

## Check Tools

### `run_erc`

Run electrical rule checks.

Inputs:

- `project_path`

Outputs:

- status
- report path
- structured findings
- summary

### `run_drc`

Run design rule checks.

Inputs:

- `project_path`

Outputs:

- status
- report path
- structured findings
- summary

### `get_check_summary`

Run available KiCad checks and return compact parsed findings plus raw report metadata.

Inputs:

- `project_path`

Outputs:

- raw check result model
- finding kind
- severity when present
- message

## Export Tools

### `export_gerbers`

Export Gerber files.

Inputs:

- `project_path`
- optional output directory

Outputs:

- generated files
- warnings

### `export_drill`

Export drill files.

Inputs:

- `project_path`
- optional output directory

Outputs:

- generated files
- warnings

### `export_bom`

Export BOM.

Inputs:

- `project_path`
- optional format

Outputs:

- generated file
- row count if available

### `export_position_files`

Export component placement files.

Inputs:

- `project_path`
- optional format

Outputs:

- generated files
- warnings

First implementation note: position files are generic KiCad CSV output, not manufacturer-specific.

### `export_manufacturing_zip`

Create a manufacturing archive.

Inputs:

- `project_path`
- optional manufacturer profile

Outputs:

- zip path
- included files
- missing optional files
- validation warnings

## Future Simulation Tool

### `run_spice_test`

Run a named SPICE simulation test and evaluate assertions.

Inputs:

- `project_path`
- `test_id`

Outputs:

- pass/fail status
- waveform or measurement outputs
- failed assertions
- summary
