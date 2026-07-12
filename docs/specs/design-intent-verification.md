# Design Intent Verification

PCBHelper uses several independent checks because each answers a different question:

- ERC checks whether schematic pins and wires are electrically legal.
- Design Intent checks whether the circuit matches the project's declared purpose.
- Simulation evaluates a mathematical component model.
- DRC checks whether board geometry can be manufactured without rule conflicts.
- Test-access checks whether important signals can be measured on assembled hardware.
- Rating checks compare sourced datasheet limits with explicit observed or simulated values.

None of these proves that a physical product works. A prototype and bench test remain necessary.

## Project contract

Committed intent lives at `.pcbhelper/design-intent.json`. Version 1 declares supply ranges, signal roles, ADC ranges, required test access, connector nets, and sourced component evidence. PCBHelper never invents missing datasheet values. Missing intent or an unknown pin map is reported as unavailable or not proven, never passed.

An agent should update intent using the `set-design-intent` Design Plan operation. Analysis is read-only and any proposed testpoint is applied later through the existing `add-testpoint` operation and transaction workflow.

## Human interpretation

A `proven` rule means PCBHelper found the specific evidence described by that rule. `not-proven` means evidence is missing or contradicts the requirement. `not-applicable` means the rule does not apply to the declared design. Errors block PCBWay release; warnings remain visible for engineering review.

The review package explains findings in ordinary language and includes a measurement plan for required testpoints. Critical semiconductor identity, datasheet, pin-map, and rating evidence must be explicit before release.
