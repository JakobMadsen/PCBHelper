# Security And Secrets

## Public Repository Rule

This repository must be safe to publish.

Do not commit:

- API keys
- hosted AI provider tokens
- Codex credentials
- private KiCad projects
- manufacturer account credentials
- customer data
- local machine paths that reveal sensitive project information

## BYOK Configuration

PCBHelper should support bring-your-own-key workflows.

Provider-specific credentials should live in:

- local environment variables
- ignored `.env` files
- OS keychains
- user-specific Codex or agent configuration
- CI secrets if CI is added later

The core KiCad tool layer should work without committed credentials.

## File System Safety

Tool implementations should:

- scope operations to the selected project root
- reject path traversal
- distinguish read-only tools from mutating tools
- report changed files after mutations
- avoid deleting files unless the user explicitly requested it

## Manufacturing Safety

PCBHelper may export manufacturing files, but V1 must not automatically order boards.

The user must review:

- schematic
- board layout
- ERC and DRC reports
- BOM
- manufacturing zip contents
- manufacturer preview

## Agent Safety

Agent actions should be inspectable.

Recommended defaults:

- small tools
- explicit inputs
- structured outputs
- dry-run support for risky operations
- before and after summaries for file mutations

