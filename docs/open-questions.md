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

- What is the minimum approved-parts format for V1?
- What templates should ship with the repo?
- What manufacturer profiles should be supported first?
- Should the Hello World optical board include an analog front-end in V1, or should V1 focus only on placement, checks, and export?

## Agent Integration

- How should BYOK provider configuration be documented?
- Which tool calls should require explicit user confirmation?
