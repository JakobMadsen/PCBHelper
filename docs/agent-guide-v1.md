# PCBHelper Agent Guide V1

PCBHelper turns small, simple electronics requirements into reviewable KiCad projects and manufacturing outputs. Use the `workflow` MCP profile and declarative Design Plans for normal work. Primitive tools are a legacy debugging surface.

## Workflow

1. Call `get_capabilities`, then `get_project_context`.
2. Resolve only material requirement ambiguity. Routine reversible work is autonomous.
3. Build one coherent Design Plan using only operations advertised by `get_capabilities`.
4. Call `validate_design_plan`, then `preview_design_plan`.
5. Apply the identical plan with the returned `planHash` as `expectedPlanHash`.
6. Run required engineering gates. Inspect and autonomously correct ordinary findings with another plan.
7. Regenerate review and manufacturing outputs after the final mutation.
   Prefer `generate_pcbway_release` for an order-review bundle with a fabrication ZIP, BOM, CPL, settings, and review report.
8. Report evidence, limitations, and unresolved decisions without overstating confidence.

## Do

- `USE_WORKFLOW_PROFILE`: Prefer the small workflow surface and transactional Design Plans.
- `CONTEXT_BEFORE_PLAN`: Read current capabilities and project state before planning.
- `PREVIEW_HASH_REQUIRED`: Validate and preview before applying the exact plan hash.
- `GATES_NOT_JUDGMENT`: Treat ERC, DRC, simulation, and manufacturing checks as distinct evidence.
- `SIMULATION_EVIDENCE_REQUIRED_FOR_FUNCTION`: Require suitable simulation or physical evidence before claiming electrical function.
- `NO_STALE_EXPORTS`: Generate release files from the final design state.
- `PCBWAY_REQUIRES_GERBER_BOM_CPL`: For assembly, verify current and mutually consistent Gerber, BOM, and CPL files.
- `ASK_ONLY_FOR_EXCEPTION_DECISIONS`: Ask about material ambiguity, unusual price or sourcing, unsupported risk, gate overrides, and irreversible external actions.

## Do Not

- `NO_RAW_KICAD_OR_SHELL`: Do not use raw KiCad text, shell, PowerShell, scripts, or arbitrary file edits when a supported operation exists.
- `NO_GUI_AS_MUTATION_FALLBACK`: Do not use Computer Use or GUI automation as the normal mutation path.
- `NO_ASSERTION_WEAKENING`: Do not weaken a test merely to make the design pass.
- `NO_FALSE_GUI_REFRESH`: A project-file change is not proof that the KiCad GUI refreshed.
- `NO_ORDER_OR_PAYMENT`: Never place an order, pay, publish, or approve component substitutions without the user.

## Release Meaning

ERC checks schematic consistency. DRC checks board rules and connectivity. Simulation checks only the modeled behavior. Manufacturing validation checks output consistency. None alone proves that the physical product works. A PCBWay package may be generated only from a gate-passed transaction, but ordering remains a human action.

If PCBHelper lacks a required operation, report the capability gap. Do not silently bypass the transaction boundary.
