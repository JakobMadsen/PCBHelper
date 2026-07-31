# Release Audit V1

PCBHelper evaluates reusable release rules while each PCB project owns its
specific electrical intent. Store the default policy at
`.pcbhelper/release-policy.json`, or pass another policy explicitly.

## Public surfaces

CLI:

```text
pcbhelper release audit <project-path> [--policy <policy.json>] [--output-dir <directory>] [--json]
```

Workflow MCP:

```text
run_release_audit(projectPath, policyPath?, outputDirectory?)
```

The audit writes `release-audit.json` and `release-audit.md`. The disposition is:

- `READY`: every required check passed and no advisory warning remains.
- `PROTOTYPE-ONLY`: required checks passed, but advisory warnings remain.
- `BLOCKED`: at least one required check failed.

CLI exit code `0` represents `READY` or `PROTOTYPE-ONLY`, `1` represents a
completed `BLOCKED` audit, and `2` represents invalid arguments, policy, or
execution.

## Policy

```json
{
  "version": 1,
  "name": "Receiver release gate",
  "git": {
    "required": true,
    "requireClean": true
  },
  "releaseEvidence": {
    "required": true,
    "requireFresh": true,
    "requiredChecks": [
      "erc",
      "drc",
      "manufacturing-validation",
      "design-intent",
      "simulation-assertions"
    ],
    "blockingAssemblyDiagnosticCodes": [
      "ASSEMBLY_ORIENTATION_REVIEW"
    ]
  },
  "bestPracticeReview": {
    "required": true,
    "requireFresh": true,
    "allowPassWithConcerns": false
  },
  "simulation": {
    "required": true,
    "minimumTests": 4
  },
  "intent": {
    "requiredVoltageLimits": [
      "FILTER_OUT"
    ]
  },
  "pinNetAssertions": [
    {
      "id": "filter-cap-return",
      "reference": "C1",
      "pad": "2",
      "net": "SUM"
    }
  ],
  "netSetAssertions": [
    {
      "id": "u1-decoupling",
      "reference": "CU1",
      "nets": ["VBAT", "GND"]
    }
  ],
  "dcFeedbackAssertions": [
    {
      "id": "filter-dc-feedback",
      "fromNet": "FILTER_OUT",
      "toNet": "SUM"
    }
  ],
  "mirroredValuePairs": [
    ["RX1", "RY1"]
  ],
  "manualEvidence": {
    "path": ".pcbhelper/release-signoff.json",
    "requiredItems": [
      "datasheet-pinout-review",
      "gerber-visual-review"
    ]
  },
  "referenceGroups": [
    {
      "id": "known-good-filter",
      "referenceProject": "../KnownGood",
      "required": true,
      "compareValues": true,
      "netMap": {
        "FILTER_OUT": "X_FILTER_OUT"
      },
      "components": {
        "R1": "RX1",
        "C1": "CX1"
      }
    }
  ]
}
```

Relative reference-project paths resolve from the policy directory. Relative
manual-evidence paths resolve from the audited project root.

## Deterministic checks

- KiCad schematic/board presence and SHA-256 fingerprints.
- Git commit traceability and clean worktree when required.
- Latest PCBHelper release-check presence and freshness.
- Current PCBHelper best-practice review disposition when required by policy.
- Selected blocking assembly diagnostic codes.
- Simulation backend and minimum project test count.
- Unrouted board connections and board/schematic value mismatches.
- Required analogue min/max voltage limits in Design Intent.
- Exact pad/net and unordered component net-set assertions.
- Resistor-only DC paths between declared analogue nodes.
- Mirrored channel component values.
- Mapped component topology against a known-good reference project.
- Evidence-backed manual sign-offs.

ERC and DRC remain separate evidence. They do not prove analogue behavior or
replace policy assertions, simulation, datasheet review, or physical validation.
When `bestPracticeReview.required` is true, missing, stale, `Revise`, or
`UnableToAssess` reviews block release. `PassWithConcerns` also blocks unless
`allowPassWithConcerns` is explicitly enabled.
When a project contains simulation tests, `generate_pcbway_release` requires
them and records the resulting `simulation` engineering check. The policy name
`simulation-assertions` is accepted as an alias for that release-check kind.

## Manual sign-off

Each required item must appear in the configured sign-off file:

```json
{
  "items": [
    {
      "id": "datasheet-pinout-review",
      "status": "approved",
      "reviewer": "name",
      "reviewedAtUtc": "2026-07-26T12:00:00Z",
      "evidence": "Link or project-relative report path"
    }
  ]
}
```

An absent item or missing approval metadata blocks release.
