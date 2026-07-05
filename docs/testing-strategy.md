# Testing Strategy

Tests are a core product feature of PCBHelper.

The project should make PCB work feel more like software work: small changes, automated checks, repeatable outputs, and clear pass/fail signals. End-to-end tests are especially important because the value of PCBHelper is not one function in isolation. The value is the full loop from agent request to KiCad project change to manufacturable output.

## Test Pyramid

PCBHelper should use several test layers.

### Unit Tests

Fast tests for pure logic.

Good candidates:

- geometry calculations
- unit conversion
- component spacing logic
- path validation
- ERC and DRC report parsing
- manufacturing zip validation
- approved-parts validation
- tool input schema validation

These tests should not require KiCad.

### Contract Tests

Tests for the public tool interface.

Good candidates:

- MCP tool schemas
- input validation errors
- structured output shapes
- read-only versus mutating tool behavior
- stable error codes
- dry-run behavior

These tests protect client compatibility for Copilot, Codex, VS Code, and future Ollama-based clients.

### Integration Tests

Tests against real or fixture KiCad project files.

Good candidates:

- read project summary from a fixture project
- read board summary from a fixture board
- move a footprint and verify the resulting board file
- measure distance between two known footprints
- run export logic into a temporary output directory

These tests may use KiCad libraries or `kicad-cli`, depending on the feature.

### End-to-End Tests

Tests that exercise the user-visible workflow.

The first E2E test should prove the Hello World loop:

1. create a new project from the optical sensor template
2. inspect the project
3. place two sensors 15 mm apart
4. measure and assert the spacing
5. run ERC
6. run DRC
7. export Gerber and drill files
8. create a manufacturing zip
9. verify expected outputs exist
10. produce a concise final report

This test should run through the same public tool boundary used by real agents. If the MCP server is the main boundary, the E2E test should call the MCP tools rather than internal functions.

## E2E Modes

PCBHelper should support more than one E2E mode.

### Headless E2E

Runs without opening the KiCad GUI.

Purpose:

- CI-friendly
- repeatable
- validates project files, CLI checks, and exports

Required for V1.

### Local KiCad E2E

Runs on a developer machine with KiCad installed.

Purpose:

- validates compatibility with the installed KiCad version
- catches real CLI and file-format behavior
- supports manual visual review after the test

Required before claiming a release works.

### Visual Review Smoke Test

Opens the generated project in KiCad for human review.

Purpose:

- confirms the board can be inspected in the real GUI
- confirms agent changes are visible and understandable

This can be manual at first. It should become more automated only if KiCad APIs make that practical.

## Fixtures

The repository should include small fixture projects where licensing allows it.

Candidate fixtures:

- empty KiCad project
- minimal board with two footprints
- beginner LED/resistor/battery-holder board inspired by KiCad's Getting Started tutorial
- Hello World optical sensor template
- board with known DRC violation
- schematic with known ERC warning
- completed manufacturing export fixture

Fixtures should be intentionally small and stable.

## CI Expectations

CI should start modestly.

Initial CI:

- run unit tests
- run contract tests
- run tests that do not require KiCad

KiCad-dependent CI:

- can be added once installation is reliable on CI runners
- may run nightly or release-gated if too slow
- should publish artifacts for failed E2E export tests

## Pass Criteria For V1

V1 should not be considered successful until:

- geometry tests prove 15 mm spacing logic
- report parser tests cover representative ERC and DRC findings
- contract tests prove stable tool outputs
- at least one headless E2E test creates and validates a manufacturing zip
- at least one local KiCad E2E test has been run and documented

## Testing Philosophy

- Test behavior through public interfaces.
- Use fixture projects instead of fragile mocks where possible.
- Keep pure logic isolated so it can be tested quickly.
- Treat ERC, DRC, export validation, and future SPICE assertions as PCB tests.
- Prefer clear failure reports over silent automation.
