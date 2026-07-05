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
