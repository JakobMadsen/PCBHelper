# PCBHelper Design Plan V1

Design Plan V1 is the default agent mutation boundary. An agent supplies declarative operations; PCBHelper validates and canonicalizes the JSON, prepares changes in isolation, applies them through one project transaction, and runs typed engineering gates. It never accepts shell commands, scripts, raw KiCad text, or file paths inside operations.

```json
{
  "version": 1,
  "goal": "Set sensor spacing and resistor value",
  "operations": [
    {
      "id": "sensor-spacing",
      "type": "set-component-spacing",
      "fixedReference": "S1",
      "movingReference": "S2",
      "distanceMm": 15,
      "axis": "x"
    },
    {
      "id": "resistor-value",
      "type": "set-component-value",
      "reference": "R1",
      "value": "300R",
      "scope": "available"
    }
  ],
  "engineeringGate": {
    "erc": "required",
    "drc": "required",
    "manufacturingValidation": "required"
  }
}
```

`projectPath` is a separate CLI or MCP argument. Preview returns canonical JSON and a SHA-256 `planHash`; apply requires that hash as `expectedPlanHash`. Operation IDs must be unique.

Supported operation types are `set-component-value`, `move-component`, `set-component-spacing`, `create-schematic-symbol`, `set-symbol-field`, `connect-schematic-pins`, `add-net-label`, `update-pcb-from-schematic`, `regenerate-board-footprint`, `add-track`, `add-track-polyline`, `delete-track`, `add-via`, and `delete-via`.

## Transactions

Prepared changes contain project-relative paths, before/after SHA-256 hashes, and before/after snapshots. They are stored under `.pcbhelper/transactions/<transaction-id>/`. Apply validates every before hash, uses a project lock and atomic file replacement, and rolls back files already written if execution fails. Restore rejects files changed since apply with `TRANSACTION_CONFLICT`.

## Autonomy

Routine reversible operations on small, simple boards are `automatic`. Disabling a default release gate is `user-required` and produces a stable decision ID bound to the plan hash. Unsupported safety-critical, mains, RF, medical, high-current, or high-speed work is `blocked`.

ERC, DRC, simulation assertions, and manufacturing validation return typed `passed`, `findings-present`, `unavailable`, or `execution-failed` outcomes. Findings block release but do not silently undo a fully applied design. Execution failure during apply triggers rollback.
