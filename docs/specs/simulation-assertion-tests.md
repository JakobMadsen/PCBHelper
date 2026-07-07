# Simulation Assertion Tests

## Purpose

PCBHelper should let an agent create and run repeatable electronics tests in the same spirit as software unit tests and E2E tests.

The agent may help write the test, but the agent must not be the judge. The judge should be a deterministic pipeline:

```text
KiCad project
  -> netlist/topology/board extraction
  -> simulator or checker
  -> numeric measurements
  -> assertions with tolerances
  -> stable pass/fail report
```

The first practical target is not "prove this PCB works". The first target is "catch obvious functional mistakes continuously".

## Source Notes

Relevant implementation facts checked before drafting this document:

- KiCad 10 `kicad-cli sch export netlist` can export schematic netlists and supports `--format spice`.
  Source: [KiCad CLI docs](https://docs.kicad.org/10.0/en/cli/cli.html#schematic-export-netlist)
- KiCad simulation requires simulation models on symbols. R/L/C models can be inferred from normal passives, and KiCad supports built-in SPICE models and external unencrypted SPICE models.
  Source: [KiCad Schematic Editor simulation docs](https://docs.kicad.org/10.0/en/eeschema/eeschema.html#assigning-models)
- KiCad's simulator can export plotted data as CSV, but OP/PZ analyses are not exported this way through the GUI.
  Source: [KiCad simulation result export docs](https://docs.kicad.org/10.0/en/eeschema/eeschema.html#exporting-simulation-results)
- ngspice supports batch-style circuit execution and `.measure` / `.meas` for measurements after AC, DC, and transient analysis.
  Source: [ngspice manual](https://ngspice.sourceforge.io/docs/ngspice-manual.pdf)

## Design Principle

Simulation tests should be written as intent-level assertions, not as arbitrary simulator scripts.

Good:

```json
{
  "assert": {
    "measurement": "gain_at_100hz_db",
    "between": [-3.0, 0.5]
  }
}
```

Risky:

```spice
.control
  arbitrary ngspice script written by an LLM
.endc
```

PCBHelper should compile a constrained test specification into simulator-specific input. This keeps the public test format stable even if the backend later changes from ngspice to another simulator.

## Proposed Test File

Store tests in the project, for example:

```text
.pcbhelper/tests/
  circuit-tests.json
```

V0 example:

```json
{
  "version": 1,
  "tests": [
    {
      "id": "rc_lowpass_gain_100hz",
      "type": "simulation.ac",
      "description": "Low-pass output should be nearly unchanged at 100 Hz.",
      "measurements": [
        {
          "name": "gain_at_100hz_db",
          "kind": "gainDb",
          "unit": "dB"
        }
      ],
      "asserts": [
        {
          "measurement": "gain_at_100hz_db",
          "between": [-3.0, 0.5]
        }
      ]
    },
    {
      "id": "filter_rejects_10khz",
      "type": "simulation.ac",
      "description": "Low-pass output should be attenuated at 10 kHz.",
      "measurements": [
        {
          "name": "gain_at_10khz_db",
          "kind": "gainDb",
          "unit": "dB"
        }
      ],
      "asserts": [
        {
          "measurement": "gain_at_10khz_db",
          "lessThan": -20
        }
      ]
    }
  ]
}
```

The test file is intentionally declarative. The agent can propose or edit it, but PCBHelper validates it before execution.

## Test Types

### `simulation.op`

Operating point tests.

Use for:

- supply rails
- bias points
- LED current
- op-amp input common-mode checks
- output saturation checks

Example assertions:

```json
{
  "asserts": [
    {
      "measurement": "led_current_ma",
      "between": [2, 15]
    },
    {
      "measurement": "opamp_output_v",
      "between": [0.2, 4.8]
    }
  ]
}
```

### `simulation.ac`

Small-signal frequency-domain tests.

Use for:

- filter gain at specified frequencies
- cutoff frequency estimates
- relative attenuation
- rough bandwidth checks

Example assertions:

```json
{
  "asserts": [
    {
      "measurement": "gain_at_100hz_db",
      "between": [-3, 1]
    },
    {
      "measurement": "gain_at_10khz_db",
      "lessThan": -20
    }
  ]
}
```

### `simulation.tran`

Time-domain tests.

Use for:

- peak-to-peak output
- startup behavior
- pulse response
- settling time
- clipping

Example assertions:

```json
{
  "asserts": [
    {
      "measurement": "out_vpp",
      "between": [0.65, 0.75]
    },
    {
      "measurement": "settling_time_ms",
      "lessThan": 10
    }
  ]
}
```

### Non-Simulation Assertions

These should share the same test runner and report format:

- `topology`: component chain, no short between named nets, required nets exist.
- `geometry`: footprint spacing, side, orientation, board edge distance.
- `manufacturing`: ERC, DRC, export/package, BOM completeness.

This matters because many beginner PCB mistakes are not SPICE problems. A board can pass SPICE and still be unbuildable or laid out incorrectly.

## Proposed Pipeline

### 1. Validate Test Spec

PCBHelper validates:

- schema version
- known test type
- known nets/references
- units
- supported measurements
- supported assertion operators

Invalid tests should fail before simulator execution with stable errors such as:

- `TEST_SPEC_INVALID`
- `TEST_TYPE_UNSUPPORTED`
- `TEST_NET_NOT_FOUND`
- `TEST_REF_NOT_FOUND`
- `TEST_MEASUREMENT_UNSUPPORTED`

### 2. Prepare Simulation Netlist

For KiCad projects:

1. Run ERC first.
2. Export SPICE netlist with `kicad-cli sch export netlist --format spice`.
3. Add a PCBHelper-generated harness around the netlist:
   - stimulus sources
   - analysis commands
   - save/print/measure commands
   - controlled output path
4. Keep generated files under:

```text
.pcbhelper/simulations/<timestamp>/
```

### 3. Run Simulator

First backend: ngspice.

Preferred V0 execution mode:

- generate a complete `.cir` file
- run ngspice headlessly
- capture stdout/stderr/log files
- parse explicit measurement lines

Avoid relying on plots or GUI workbooks for assertions.

### 4. Parse Measurements

PCBHelper should normalize all measurements into a simple model:

```json
{
  "name": "gain_at_100hz_db",
  "value": -0.72,
  "unit": "dB",
  "source": "ngspice",
  "analysis": "ac"
}
```

### 5. Evaluate Assertions

Assertions are evaluated by PCBHelper, not by the simulator and not by the LLM.

Supported V0 operators:

- `equals` with tolerance
- `between`
- `lessThan`
- `greaterThan`
- `approximately`

Output:

```json
{
  "success": false,
  "summary": "1 of 2 simulation assertions failed.",
  "data": {
    "testId": "filter_rejects_10khz",
    "measurements": [
      {
        "name": "gain_at_10khz_db",
        "value": -14.2,
        "unit": "dB"
      }
    ],
    "assertions": [
      {
        "measurement": "gain_at_10khz_db",
        "operator": "lessThan",
        "expected": -20,
        "actual": -14.2,
        "passed": false
      }
    ],
    "artifacts": {
      "netlist": ".pcbhelper/simulations/...",
      "log": ".pcbhelper/simulations/..."
    }
  }
}
```

## MCP Tool Shape

Potential tools:

- `list_test_specs`
- `validate_test_spec`
- `run_pcbhelper_test`
- `run_pcbhelper_tests`
- `get_test_report`
- `create_test_spec_preview`

Mutating tool caution:

- `create_test_spec_preview` may generate a suggested test file.
- Real creation should be explicit and reviewable.
- Test execution itself should not mutate KiCad project files.

## First Slice Recommendation

Do not start with arbitrary KiCad schematic simulation.

Start with a narrow, testable path:

### Slice A: Test Spec And Assertion Engine

- Add `.pcbhelper/tests/*.json`.
- Implement schema validation.
- Implement assertion evaluator.
- Use fake measurement input in unit tests.
- No ngspice required yet.

### Slice B: Headless ngspice Fixture

- Add a plain SPICE RC-filter fixture independent of KiCad.
- Run ngspice.
- Parse measurements.
- Assert gain at 100 Hz and 10 kHz.
- Skip E2E with clear reason if ngspice is unavailable.

This proves the simulator/assertion loop without KiCad netlist complexity.

### Slice C: KiCad SPICE Netlist Export

- Use `kicad-cli sch export netlist --format spice`.
- Add a KiCad schematic fixture with simulation-ready symbols/models.
- Inject or compose a PCBHelper test harness.
- Run the same assertions.

### Slice D: MCP Agent Workflow

- Expose validate/run tools over MCP.
- Ask Copilot to create a test spec.
- Validate the spec.
- Run tests.
- Report failures without guessing.

## Example Agent Workflow

User:

```text
Create a PCBHelper test for this filter:
At 100 Hz, OUT should be within -3 dB to 0.5 dB of IN.
At 10 kHz, OUT should be below -20 dB.
Do not apply the test file until I approve it.
```

Expected agent behavior:

1. Inspect nets and components.
2. Propose a `.pcbhelper/tests/filter.json` test.
3. Call `validate_test_spec`.
4. Wait for approval.
5. Write the test file.
6. Run `run_pcbhelper_tests`.
7. Report numeric pass/fail.

## Known Limits

Simulation tests are useful but not authoritative.

Risks:

- missing or wrong SPICE models
- wrong pin mapping
- ideal passives hiding real-world behavior
- convergence failures
- AC analysis used where transient behavior matters
- layout parasitics ignored
- thermal/mechanical/manufacturing issues ignored

Therefore, PCBHelper should report simulation as one test layer, not as proof that a physical PCB will work.

## Open Questions

- Should test specs live in `.pcbhelper/tests/` or `tests/pcbhelper/` for easier CI discovery?
- Should YAML be added later for easier authoring, or should JSON remain the only committed format?
- Should PCBHelper own a tiny expression language for measurements, or only offer named measurement kinds?
- Should ngspice be optional local dependency, bundled adapter, or documented prerequisite?
- How should manufacturer/datasheet SPICE models be tracked without committing questionable license material?
- Should CI run ngspice tests by default, or only when the simulator is installed?
