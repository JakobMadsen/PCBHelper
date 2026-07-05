# Hello World Board: Dual Optical Sensor Comparator

## Purpose

This board is the first end-to-end test case for PCBHelper.

It is intentionally small. The goal is to prove that Codex can use the KiCad tool layer to create, inspect, place, check, export, and support human review on a real PCB project.

## Concept

A modulated light source blinks around 10 kHz using visible light or near infrared. The board has two optical sensors placed 15 mm apart. The user wants to compare which sensor receives more of the modulated light.

## Required Board Features

- two photodiodes or approved optical sensors
- 15 mm center-to-center spacing between sensors
- two matched analog channels
- outputs named `AMP_A` and `AMP_B`
- connector pins for power, ground, `AMP_A`, and `AMP_B`
- board outline suitable for a small prototype
- design checks runnable through KiCad tooling
- manufacturing export package

## Optional Board Features

- transimpedance amplifier
- simple gain stage
- 10 kHz filter or envelope stage
- MCU input header
- test points for raw and amplified channel outputs

## Constraints

- Sensor spacing is a hard mechanical constraint.
- Channel A and channel B should be as symmetric as practical.
- Component choices should come from approved parts.
- The design should be simple enough for visual review.

## Candidate Validation Checks

- ERC passes or produces only accepted warnings.
- DRC passes or produces only accepted warnings.
- Sensor center spacing is 15 mm within a defined tolerance.
- Both channels have matching topology.
- Connector exposes required signals.
- Manufacturing zip contains required outputs.

## Future Simulation Checks

When simulation support exists, the project should test:

- ambient DC light plus 10 kHz modulated signal
- no saturation in the analog chain
- detectable 10 kHz signal at both outputs
- `AMP_A > AMP_B` when sensor A receives more modulated light
- balanced output when both sensors receive equal input

## Prototype Success Prompt

The intended first demo prompt is:

```text
Create a new KiCad project for a dual optical comparator board.
Place two sensors with 15 mm center spacing.
Create the board outline, connector, and required outputs.
Run ERC and DRC, then export manufacturing files.
```

The agent should create the project, perform precise placement, run checks, summarize results, export files, and leave the user able to inspect and approve the board in KiCad.

