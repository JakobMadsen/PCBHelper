# MCP Tool Contract Draft

## Agent contract

The default workflow profile includes `get_capabilities` and `get_agent_guide`. Capabilities expose the current operation catalog, defaults, limitations, guide URI, and Design Plan schema URI. The same canonical content is available as MCP resources:

- `pcbhelper://agent-guide/v1` (`text/markdown`)
- `pcbhelper://design-plan/v1/schema` (`application/schema+json`)

The `operate_pcbhelper_project` prompt accepts `projectPath` and `goal` and bootstraps the standard context, Design Plan, gate, and release workflow. These resources and prompt are available in `workflow`, `legacy`, and `all` profiles. Clients without resource or prompt support use the equivalent tools directly.

The default public surface is the Design Plan workflow described in [Design Plan V1](design-plan-v1.md). The primitive contracts below are retained as the `legacy` profile for debugging and compatibility.

## Design Rules

- The default MCP profile should be small and workflow-oriented.
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

### `list_unrouted_connections`

List disconnected board-routing islands per net.

Inputs:

- `project_path`
- optional `net`

Outputs:

- resolved nets with disconnected components
- pads and coordinates per component
- nearest missing pad-to-pad connection per adjacent component

### `validate_track_clearance`

Validate a proposed track polyline before writing it.

Inputs:

- `project_path`
- `net`
- `points`: `x1,y1;x2,y2;...`
- `layer`: `F.Cu` or `B.Cu`
- `width_mm`

Outputs:

- resolved net
- proposed points
- clearance used
- violations when blocked

Stable error:

- `ROUTING_CLEARANCE_VIOLATION` when the proposed copper touches or violates clearance against a different net.

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

First implementation note: `add_track` is a low-level compatibility primitive. Prefer `validate_track_clearance` plus `add_track_polyline` for agent-driven routing.

### `add_track_polyline_preview` / `add_track_polyline`

Preview or add one atomically validated track polyline.

Inputs:

- `project_path`
- `net`
- `points`: `x1,y1;x2,y2;...`
- `layer`: `F.Cu` or `B.Cu`
- `width_mm`

Outputs:

- proposed or written segment text
- changed file
- change report path for real changes
- DRC summary for real changes

Stable error:

- `ROUTING_CLEARANCE_VIOLATION` and no file write if any segment is unsafe.

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

### `setup_freerouting_preview` / `setup_freerouting`

Preview or download FreeRouting into PCBHelper's local tools cache.

Inputs:

- none

Outputs:

- current FreeRouting discovery
- Java discovery
- selected release asset
- target cache path
- generated files for real setup

First implementation note: setup downloads the latest FreeRouting JAR from the official GitHub release API into the user's local PCBHelper tools cache. Java is still required to run the JAR.

### `autoroute_board_preview` / `autoroute_board`

Preview or run the FreeRouting DSN/SES backend.

Inputs:

- `project_path`

Outputs:

- backend discovery for KiCad CLI, FreeRouting, and Java
- routing workspace under `.pcbhelper/routing/<timestamp>/`
- generated DSN/SES/log metadata paths when available

Stable errors:

- `ROUTING_BACKEND_UNAVAILABLE` when KiCad CLI, FreeRouting, Java, or DSN/SES command support is unavailable.
- `BOARD_OUTLINE_MISSING` when the board has no `Edge.Cuts` outline.

## Schematic Authoring Tools

### `list_schematic_symbols`

List placed schematic symbols and lightweight schematic counts.

Inputs:

- `project_path`

Outputs:

- schematic file
- symbol references, catalog ids, units, values, footprints, positions, and fields
- wire count
- label count
- wires with UUID and endpoint coordinates
- labels with UUID, net text, and coordinates

### `create_schematic_symbol_preview` / `create_schematic_symbol`

Preview or place one approved catalog symbol instance.

Inputs:

- `project_path`
- `symbol`: an approved schematic catalog id, for example `Device:R`, `Device:D_Photo`, or `Amplifier_Operational:OPA2325`
- `reference`
- `x` / `y` in millimeters
- optional `unit`, default `1`; multi-unit parts use one placed symbol per KiCad unit with the same reference
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
- `from`: `<ref.pin>` or `<ref:pin>`
- `to`: `<ref.pin>` or `<ref:pin>`
- optional `net`

Outputs:

- proposed or written wire and optional label text
- changed file snapshot
- change report path for real changes
- ERC/DRC report paths for real changes

Stable errors include `SCHEMATIC_PIN_NOT_FOUND` for unknown references or pins. Dot notation is canonical, and colon notation is accepted for agent/user convenience.

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

### `delete_net_label_by_uuid_preview` / `delete_net_label_by_uuid`

Preview or delete one schematic net label by KiCad UUID.

Inputs:

- `project_path`
- `uuid`

Outputs:

- removed label text
- changed file snapshot
- change report path for real changes
- ERC/DRC report paths for real changes

### `delete_net_label_preview` / `delete_net_label`

Preview or delete one schematic net label by net name and coordinate.

Inputs:

- `project_path`
- `net`
- `x` / `y` in millimeters
- optional `tolerance_millimeters`, default `0.05`

Outputs:

- removed label text
- changed file snapshot
- change report path for real changes
- ERC/DRC report paths for real changes

### `delete_schematic_wire_by_uuid_preview` / `delete_schematic_wire_by_uuid`

Preview or delete one schematic wire by KiCad UUID.

Inputs:

- `project_path`
- `uuid`

Outputs:

- removed wire text
- changed file snapshot
- change report path for real changes
- ERC/DRC report paths for real changes

### `delete_schematic_wire_preview` / `delete_schematic_wire`

Preview or delete one schematic wire by endpoints.

Inputs:

- `project_path`
- `x1` / `y1` in millimeters
- `x2` / `y2` in millimeters
- optional `tolerance_millimeters`, default `0.05`

Outputs:

- removed wire text
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

### `regenerate_board_footprint_preview` / `regenerate_board_footprint`

Preview or regenerate one existing board footprint from the approved schematic symbol template.

Inputs:

- `project_path`
- `reference`

Outputs:

- regenerated footprint text
- changed board file snapshot
- change report path for real changes
- ERC/DRC report paths for real changes

First implementation note: regeneration is intentionally single-reference. It preserves the existing board footprint position and rotation, rebuilds pads from the latest approved template, and falls back to existing board pad net assignments when the schematic no longer contains enough labels/wires to infer nets.

## Simulation Assertion Tools

### `list_test_specs`

List JSON assertion test specs under `.pcbhelper/tests/*.json`.

Inputs:

- `project_path`

Outputs:

- tests directory
- files
- test ids
- test counts

### `validate_test_specs`

Validate JSON assertion test specs without running a simulator.

Inputs:

- `project_path`

Outputs:

- validation status
- file count
- test count
- diagnostics

Stable errors include `TEST_SPEC_INVALID`, `TEST_TYPE_UNSUPPORTED`, and `TEST_MEASUREMENT_NOT_FOUND`.

### `evaluate_test_results`

Evaluate external measurement JSON against validated test specs.

Inputs:

- `project_path`
- `results_path`

Outputs:

- normalized measurements
- per-test assertion results
- pass/fail counts

First implementation note: V0 evaluates external measurement files only. It does not run ngspice, export KiCad SPICE netlists, or mutate project files. Assertion failures return stable error code `TEST_ASSERTIONS_FAILED` with result data.

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

### `export_assembly_bom`

Export a PCBWay-oriented assembly BOM CSV.

Inputs:

- `project_path`

Outputs:

- generated CSV path
- row count
- generated files

First implementation note: this is an assembly BOM derived from schematic and board fields, not the raw KiCad BOM export.

### `export_cpl`

Export a PCBWay-oriented component placement/centroid CSV.

Inputs:

- `project_path`

Outputs:

- generated CSV path
- row count
- generated files

First implementation note: SMD and mixed-mount assembled footprints are included; through-hole parts are warned and excluded from the CPL by default.

### `validate_assembly_package`

Validate assembly BOM/CPL readiness.

Inputs:

- `project_path`

Outputs:

- validity
- error and warning counts
- diagnostics
- BOM and CPL row counts

Diagnostics include duplicate or unannotated references, missing placement data, BOM/CPL mismatches, DNP/excluded components, missing part numbers, through-hole CPL exclusions, and polarity/orientation review warnings.

### `export_pcbway_assembly_package`

Create a PCBWay assembly archive.

Inputs:

- `project_path`

Outputs:

- zip path
- manifest path
- assembly BOM path
- CPL path
- validation report path
- included files
- validation result

First implementation note: the package includes Gerber/drill output plus PCBWay-oriented assembly BOM, CPL, validation JSON, and manifest. It does not upload or order boards.

## Simulation Workflow Tools

- `get_simulation_capabilities` reports ngspice discovery without executing a test.
- `validate_simulation_tests` validates constrained tests and project-contained circuit paths.
- `run_simulation_tests` runs all tests or one optional `testId` and returns numeric measurements and assertions.
- `get_simulation_report` retrieves a project-scoped report using its `runId`.

Simulation execution never accepts raw simulator commands. Missing backends are unavailable, assertion failures are
findings, and simulator/process failures are execution failures.
