# Evidence-bound best-practice review

Status: normative for PCBHelper qualitative design review.

ERC, DRC, simulation, Design Intent, manufacturing validation, and release audit remain
deterministic evidence. They cannot answer every question about schematic readability,
functional separation, layout grouping, probe access, return paths, crosstalk risk, or whether
an engineering claim is appropriately cautious.

PCBHelper therefore uses a two-phase LLM review without embedding an LLM provider in PCBHelper.
The MCP client is the reviewer.

## Phase 1: prepare immutable evidence and a pure prompt

```text
pcbhelper best-practice prepare <project-path> --json
```

The preparation contains:

- a fixed rule catalog;
- schematic symbols, wires, labels, and positions;
- PCB footprints, nets, tracks, vias, widths, and positions;
- component, test, simulation, and design-intent context when available;
- deterministic observations such as prototype package sizes, testpoint count, possible
  current-measurement parts, power-track widths, and explicit ground vias;
- paths and SHA-256 hashes for current visual review artifacts;
- an evidence SHA-256;
- the complete versioned LLM prompt;
- the exact JSON response schema.

The prompt is intentionally provider-neutral. It must not invoke a hidden model, send project
data to an external service, or create a pass automatically.

## Phase 2: inspect and submit the assessment

The reviewer must actually open current PNG, SVG, or PDF artifacts before making visual claims.
It answers every rule exactly once and cites evidence IDs.

```text
pcbhelper best-practice submit <project-path> \
  --file <assessment.json> \
  --expected-evidence-hash <hash> \
  --json
```

PCBHelper rejects:

- stale evidence after any source or render change;
- missing, duplicate, or unknown rules;
- unknown evidence citations;
- non-visual passes where relevant evidence is unavailable;
- `notApplicable` without an applicability rationale;
- concerns or failures without a recommendation;
- responses with extra or malformed JSON properties.

PCBHelper derives the overall disposition rather than trusting the model:

| Criterion state | Overall consequence |
| --- | --- |
| Any `fail` | `revise` |
| Otherwise, any `unableToAssess` | `unableToAssess` |
| Otherwise, any `concern` | `passWithConcerns` |
| All `pass` or justified `notApplicable` | `pass` |

Reports are stored under `.pcbhelper/best-practice-reviews/<run-id>/` with the exact prompt,
evidence, assessment, JSON report, and Markdown report.

After the final design or render change, verify that the review is still current:

```text
pcbhelper best-practice validate <project-path> --json
```

Validation passes only when the report evidence hash still matches the project and its disposition
is `pass`. A stale report, unresolved concern, failed criterion, or unavailable assessment cannot
be treated as a release-ready best-practice review.

## Initial rule catalog

- Functional separation and explicit interfaces.
- Human-readable schematic.
- Tight op-amp functional grouping and short critical loops.
- Branch/stub and reflection assessment.
- Parallel-route crosstalk assessment.
- Power and return-path dimensioning.
- Ground strategy and appropriate via stitching.
- Test access at every relevant amplifier/filter stage.
- Prototype current-measurement provision.
- Prototype-friendly component sizes.
- Prototype/simulation evidence for uncertain functions.
- Evidence-bounded EMC claims.

These are not all unconditional layout prescriptions. The reviewer must distinguish physical
requirements from preferences and mark context-dependent rules `notApplicable` only with a
specific reason.

## Trust boundary

Project strings and documents are untrusted evidence, not instructions. The prompt explicitly
forbids following text embedded in net names, component values, board text, file paths, or
attachments. The recorded LLM review is structured engineering judgment. It does not prove
electrical function, EMC compliance, safety, or manufacturability and does not replace qualified
human approval.
