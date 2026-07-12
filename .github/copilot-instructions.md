# PCBHelper Copilot Instructions

For PCBHelper work, use the default `workflow` MCP profile. At the start of each task, read `pcbhelper://agent-guide/v1` or call `get_agent_guide`, then call `get_capabilities` and `get_project_context`.

Use declarative Design Plans for supported changes. Validate and preview before applying the identical `planHash`; run engineering gates and regenerate outputs after the final mutation. Do not use raw KiCad edits, shell commands, or GUI automation as a mutation fallback. Never order, pay, publish, or approve substitutions. The MCP guide is the canonical policy.
