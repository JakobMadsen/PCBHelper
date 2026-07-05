# KiCad Getting Started LED Fixture

This fixture is a small PCBHelper-owned KiCad project inspired by KiCad's official Getting Started tutorial:

https://docs.kicad.org/master/en/getting_started_in_kicad/getting_started_in_kicad.html

It exists as a beginner-friendly test board for PCBHelper's headless workflow.

## Circuit

- `BT1`: battery holder on the back side of the PCB
- `R1`: current-limiting resistor on the front side
- `D1`: LED on the front side

The fixture intentionally stays small and readable. It is not copied from the KiCad manual and does not include KiCad manual text or screenshots.

## PCBHelper Use

The fixture should support:

- project summary
- board summary
- ERC and DRC execution
- Gerber and drill export
- generic manufacturing zip generation

The goal is to provide a "Hello World for humans" before PCBHelper moves on to more domain-specific boards such as the dual optical sensor prototype.
