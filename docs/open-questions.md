# Open Questions

These decisions are intentionally left open in the first spec pass.

## Repository And Governance

- Should the default branch be `main` instead of `master`?
- Which contribution expectations should be documented before inviting outside contributors?

## Implementation

- Which KiCad API path should be primary for board edits: Python scripting, IPC, or a hybrid?
- How should live KiCad highlighting be implemented?
- Should each accepted board mutation be captured as a git commit?

## Product Scope

- Which measurable limits define a small V1 board: board area, layers, component count, net count, current, voltage, and supported circuit classes?
- Which design recipes should ship first after the optical sensor board?
- What is the minimum project design-lock and part-evidence format for V1?
- Which price, stock, MOQ, lifecycle, and evidence thresholds can be approved automatically?
- When should the user approve a costly or unusual part, and when should PCBHelper refuse it entirely?
- How fresh must price and availability evidence be before release?
- Which supplier and manufacturer data sources are legally and technically suitable for a public BYOK project?
- What manufacturer profiles should be supported first?
- Is the first manufacturing target bare PCB, PCBWay assembly, or both?

## Agent Integration

- How should BYOK provider configuration be documented?
- Which tool calls should require explicit user confirmation?
- Should MCP expose workflow profiles so the agent sees only inspection, authoring, or manufacturing tools for the current phase?
- Which beginner-readable artifacts are required before final approval?
