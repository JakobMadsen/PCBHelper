# Human-readable schematics and boards

PCBHelper separates deterministic presentation evidence from electrical meaning. Geometry may reveal collisions or dispersion; only Design Intent may declare semantic blocks.

## Schematic readability V2

`analyze_schematic_readability(projectPath)` and `pcbhelper schematic-readability analyze <project-path> --json` are byte-read-only. The report retains the original connectivity metrics and adds `textBoxCount`, `textBoxOverlaps`, `wireTextBoxBoundaryCrossings`, and `visualBoxDispersionSquareMillimeters`.

The parser preserves each top-level KiCad text box's text, UUID, position, size, rotation, stroke, fill, effects, and original source range. An optional `presentation.blocks[].textBoxUuid` explicitly maps one box to one Design Intent block. Proximity never creates a semantic or electrical relationship.

Automatic `arrange-schematic` currently fails with `SCHEMATIC_TEXT_BOX_RELAYOUT_UNSUPPORTED` whenever a text box exists. The failure is returned before planning or writing, including through Design Plan preview/apply.

## Board readability V1

`analyze_board_readability(projectPath)` and `pcbhelper board-readability analyze <project-path> [--output-dir <dir>] [--json]` inspect visible board text, footprint text/properties, silkscreen primitives, Edge.Cuts, pad/mask openings, and component envelopes derived from Courtyard, then Fab, then conservative pad bounds. Incomplete envelopes produce `BOARD_GEOMETRY_INCOMPLETE` INFO evidence rather than speculative collisions.

Stable diagnostic codes are:

- `BOARD_TP_LABEL_MISSING`
- `BOARD_CONNECTOR_LABEL_MISSING`
- `BOARD_JUMPER_PURPOSE_MISSING`
- `BOARD_ORIENTATION_MARK_MISSING`
- `BOARD_SILK_PAD_OVERLAP`
- `BOARD_SILK_TEXT_OVERLAP`
- `BOARD_SILK_OCCLUDED_AFTER_ASSEMBLY`
- `BOARD_GEOMETRY_INCOMPLETE`

Reference and value text do not count as functional labels. Labels are matched against normalized pad net names. Orientation requirements derive from component/pin metadata and require explicit visible silkscreen evidence. LLM review may add judgment but cannot replace or erase deterministic findings.

Report writing requires an explicit authorized output directory and never changes the KiCad project. The release policy's optional `boardReadability.required` and `boardReadability.blockingDiagnosticCodes` fields make the report advisory by default.
