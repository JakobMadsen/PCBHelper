# Board Finishing and PCBWay Release

PCBHelper exposes board-finishing mutations through Design Plan V1 so they receive the same preview hash, transaction, restore, and engineering gates as placement and routing.

Supported operations are `add-copper-zone`, `update-copper-zone`, `move-reference-text`, `hide-reference-text`, `cleanup-silkscreen`, `add-testpoint`, `add-mounting-hole`, and `add-mechanical-keepout`. Polygon points use the constrained `x,y;x,y;...` form. V1 zones and keep-outs support `F.Cu` and `B.Cu` only.

KiCad CLI does not expose zone refill. `refill_zones` therefore returns `KICAD_ZONE_REFILL_UNAVAILABLE`; it never reports a file mutation as a refill. The release requirements gate blocks boards that contain zones without saved filled polygons.

`generate_pcbway_release` runs release gates and writes one release directory containing:

- a fabrication-only Gerber/drill ZIP
- PCBWay-oriented BOM CSV
- CPL CSV
- `pcbway-order-settings.json`
- `release-review.json`

It does not place an order. The settings are conservative defaults and remain subject to review on PCBWay's site.

The requirements gate scans project-owned Markdown, JSON, and text specifications. It currently recognizes explicit required/mandatory/needed testpoints and mounting holes, then verifies corresponding board footprints. More requirement rules should be added as typed checks rather than inferred by the LLM.

KiCad simulation export validates models before invoking `kicad-cli`: passives may use controlled built-in behavior, while non-passive symbols require an explicit SPICE model and pin map. Battery, tolerance, and noise sweeps use constrained circuit placeholders `{{BATTERY_V}}`, `{{TOLERANCE_SCALE}}`, and `{{NOISE_V}}`; arbitrary commands remain forbidden.
