# PCBHelper Agent Instructions

PCBHelper targets small, simple, reviewable PCBs. Use the MCP `workflow` profile for product work and `legacy` only for debugging.

Before operating a project, read `pcbhelper://agent-guide/v1` (or call `get_agent_guide`), then call `get_capabilities` and `get_project_context`. Build supported changes as a Design Plan, validate and preview it, and apply only the exact returned hash. Keep ordinary reversible work autonomous, but leave orders, payments, publication, substitutions, and material risk decisions to the user.

The versioned MCP guide is canonical. Do not duplicate or override its policy here.
