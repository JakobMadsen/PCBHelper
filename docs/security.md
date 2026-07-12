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

The user must receive and approve an understandable review package containing:

- schematic
- board layout
- ERC and DRC reports
- BOM
- manufacturing zip contents
- manufacturer preview
- board dimensions and connector pinouts
- component cost and sourcing warnings
- unresolved uncertainty and unsupported assumptions

KiCad review is recommended as an expert option but is not a prerequisite for the primary beginner workflow. PCBHelper must not represent ERC or DRC alone as proof that a circuit fulfills its intended function.

## Component Selection Safety

The AI may propose new components, but project approval must use evidence and policy.

- Verify manufacturer identity, datasheet, package, symbol, footprint, and pin mapping.
- Record source and freshness for price, stock, lifecycle, and supplier claims.
- Treat missing or conflicting evidence as uncertainty, not permission to guess.
- Require user approval for expensive, scarce, obsolete, unusually sourced, or policy-exceeding parts.
- Block release when electrical ratings, pin mapping, footprint compatibility, or manufacturer identity cannot be verified.
- Store approved choices in project metadata so repeated runs do not silently substitute parts.
- Never let supplier content directly authorize local file writes or command execution.

## Agent Safety

Agent actions should be inspectable.

Recommended defaults:

- small tools
- explicit inputs
- structured outputs
- dry-run support for risky operations
- before and after summaries for file mutations
- configured authorized project roots
- project-relative change reports with file hashes
- explicit approval gates for part and manufacturing decisions
