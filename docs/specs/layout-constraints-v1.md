# Layout Constraints V1

The default project document is `.pcbhelper/constraints-v1.json`. It is project-scoped and versioned independently from release policy.

```json
{
  "version": 1,
  "constraints": [
    { "id": "front-only", "type": "allowed-layers", "net": "SENSE", "allowedLayers": ["F.Cu"] },
    { "id": "u1-bypass", "type": "max-feature-distance", "from": "U1.3", "to": "C1.1", "maximumMm": 2 },
    { "id": "ground-copper", "type": "min-copper-area", "net": "GND", "layer": "F.Cu", "minimumSquareMm": 400 }
  ]
}
```

Supported types:

- `allowed-layers`: optional `net`, required `allowedLayers`.
- `max-feature-distance`: `from`, `to`, `maximumMm`; features use `REF` or `REF.PAD`.
- `min-copper-area`: `net`, `layer`, `minimumSquareMm`; requires filled KiCad zone polygons.
- `max-zone-islands`: `net`, `layer`, `maximumCount`; requires filled KiCad zone polygons.
- `max-trace-length`: `net`, `maximumMm`; requires at least one routed segment on the resolved net.
- `matched-trace-length`: `nets`, `maximumDifferenceMm`; every resolved net requires at least one routed segment.
- `max-vias`: optional `net`, `maximumCount`; a named net must resolve, while zero vias is a valid measurement.
- `footprint-region`: `feature` plus `minimumXmm`, `maximumXmm`, `minimumYmm`, and `maximumYmm`.

Every result contains a measured value where applicable, limit, involved features, outcome, summary, and the combined board/constraint input hash. Outcomes are `passed`, `failed`, and `unavailable`. Missing nets, missing routed segments, missing features, and absent filled polygons are `unavailable`; they never count as `passed`.

`validate_layout_constraints` performs schema and semantic validation only. `run_layout_constraint_proofs` measures the current board and atomically writes `.pcbhelper/reports/constraints/<input-hash>.json`. It does not modify KiCad sources.
