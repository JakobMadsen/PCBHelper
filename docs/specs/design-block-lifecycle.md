# PCBHelper native design block lifecycle

Status: normative for PCBHelper design-block libraries.

PCBHelper uses KiCad 10's native design-block layout. A library is a directory ending in
`.kicad_blocks`; each block is a direct child ending in `.kicad_block`. The block contains a
schematic, an optional PCB layout, and KiCad metadata. PCBHelper adds
`pcbhelper-block.json` as the engineering contract and `REVIEW.md` as the human gate.

The native structure is documented by KiCad:
<https://docs.kicad.org/10.0/en/eeschema/eeschema.html#schematic-design-blocks>.

## Non-negotiable rules

1. Blocks are copied into a library; PCBHelper never overwrites an existing block.
2. Every block has a semantic version, source, license, attribution status, and redistribution
   decision.
3. Every external connection is a typed port. Net names are part of the contract.
4. Every block has a layout policy: `schematicOnly`, `grouped`, `anchored`, or `locked`.
5. `reference` is the default maturity. Higher maturity requires attached, passed evidence.
6. `published` requires a schematic, redistribution permission, and passed human review.
7. A changed circuit or layout is a new reviewed version. Projects do not silently follow it.
8. Preview and apply are separate operations. Apply requires the exact SHA-256 preview hash.

## Route A: import an existing block

Use this for KiCad's official library, manufacturer examples already converted to KiCad, and
open-hardware blocks.

1. **Intake.** Place the untouched source `.kicad_block` in a review workspace. Record its exact
   URL, title, license, attribution, retrieval date, and redistribution permission.
2. **Triage.** Reject unsupported technology, unclear licensing, missing primary documentation,
   or a circuit outside PCBHelper's engineering scope.
3. **Normalize.** Redraw or clean the schematic into the PCBHelper house style. Do not treat a
   picture or a copied page as an accepted schematic.
4. **Contract.** Define ports, electrical domains, net names, required/optional connections,
   component ratings, and layout policy in the manifest.
5. **Preview.**

   ```text
   pcbhelper blocks preview-import <library.kicad_blocks> \
     --source <candidate.kicad_block> --manifest <manifest.json> --json
   ```

6. **Review the preview.** Check target paths, payload hashes, source/license, port contract, and
   layout policy.
7. **Apply the exact preview.**

   ```text
   pcbhelper blocks apply-import <library.kicad_blocks> \
     --source <candidate.kicad_block> --manifest <manifest.json> \
     --expected-hash <preview-plan-hash> --json
   ```

8. **Verify.** Run library validation, ERC/DRC and appropriate simulation. Complete `REVIEW.md`.
   Add evidence before changing maturity or lifecycle.

## Route B: make a new block

Use this for an internal circuit or a clean implementation derived from a manufacturer reference.

1. **Author in KiCad.** Build and review a small standalone `.kicad_sch`. If layout reuse matters,
   create one matching `.kicad_pcb`. A source directory may contain at most one of each.
2. **Use the house style.** Left-to-right functional flow, named power rails, explicit connectors,
   readable values, functional notes, decoupling next to the relevant device, and no overlapping
   text or wires.
3. **Contract.** Create the manifest before packaging. New internal work still needs provenance:
   use `internal://...` or the primary manufacturer reference, not an empty source.
4. **Preview.**

   ```text
   pcbhelper blocks preview-create <library.kicad_blocks> \
     --source <block-source-directory> --manifest <manifest.json> --json
   ```

   An explicit `--board <file.kicad_pcb>` can accompany a standalone schematic.

5. **Review and apply** with `blocks apply-create` and the returned hash.
6. **Verify and publish** using the same gates as imported blocks.

## Maturity evidence

| Maturity | Required passed evidence |
| --- | --- |
| `reference` | None; suitability is unproven |
| `simulated` | `simulation` |
| `prototypeTested` | `simulation`, `benchTest` |
| `qualified` | `simulation`, `benchTest`, `review` |
| `productionProven` | All above plus `productionRun` |

Evidence may be an HTTPS URI or a file stored inside the block. Local evidence files should carry
a SHA-256 in the manifest. A failing or informational result does not satisfy a maturity gate.

## Layout policies

- `schematicOnly`: only topology is reused; placement and routing are new engineering work.
- `grouped`: footprints should remain a logical group, but can be arranged for the target board.
- `anchored`: critical relative placement is preserved; documented constraints may allow tuning.
- `locked`: the supplied layout is part of the validated design. A PCB payload is mandatory.

Prefer the least restrictive policy that is technically honest. Power loops, precision analog
front ends, antennas, controlled-impedance networks, and sensitive references often need
`anchored` or `locked`; ordinary logic commonly does not.

## Library validation

```text
pcbhelper blocks list <library.kicad_blocks> --json
pcbhelper blocks inspect <library.kicad_blocks> --id <block-id> --json
pcbhelper blocks validate <library.kicad_blocks> --json
```

Validation checks the native directory shape, KiCad file headers, generated metadata, payload
hashes, path safety, provenance, ports, layout contract, evidence files, lifecycle gates, maturity
gates, and duplicate IDs. Passing these deterministic checks is necessary but does not replace an
electronics engineer's review.
