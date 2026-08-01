# PCBHelper Agent Guide V1

PCBHelper turns small, simple electronics requirements into reviewable KiCad projects and manufacturing outputs. Use the `workflow` MCP profile and declarative Design Plans for normal work. Primitive tools are a legacy debugging surface.

## Workflow

1. Call `get_capabilities`. If no project exists yet, use `create_project_from_template`; then call `get_project_context`.
   For block-based design, call `list_design_blocks`, inspect candidate contracts, and validate the library before selecting a version.
2. Resolve only material requirement ambiguity. Routine reversible work is autonomous.
3. Build one coherent Design Plan using only operations advertised by `get_capabilities`.
   Select schematic symbols only from `approvedSymbols`; treat each entry's source, pin units, and default footprint as the authoritative catalog contract.
   Use `set-simulation-fixture` for project-contained ngspice evidence. It accepts only structured elements and declarative tests, never raw SPICE commands or arbitrary paths.
4. Call `validate_design_plan`, then `preview_design_plan`.
5. Apply the identical plan with the returned `planHash` as `expectedPlanHash`.
6. Run required engineering gates, including Design Intent when the project declares it. Inspect and autonomously correct ordinary findings with another plan.
   Call `analyze_schematic_readability` and `analyze_board_readability` before release when presentation quality matters. Both are read-only. A schematic containing top-level `text_box` objects may be analyzed, but `arrange-schematic` must fail with `SCHEMATIC_TEXT_BOX_RELAYOUT_UNSUPPORTED` until box-aware relayout is supported. If `.pcbhelper/constraints-v1.json` exists, validate it and run `run_layout_constraint_proofs`; `unavailable` evidence is not a pass.
   For qualitative architecture, readability, layout, DFT, signal-integrity, return-path, prototype, and EMC-practice review, call `prepare_best_practice_review`. Actually inspect its cited visual artifacts, answer every criterion using the returned pure prompt, and call `submit_best_practice_review` with the exact evidence hash. After the final design/render change, call `validate_best_practice_review`.
7. When the project declares `.pcbhelper/release-policy.json`, run `run_release_audit` and resolve every blocking finding. Use `get_workflow_status`, `list_artifacts`, and `get_artifact` to inspect existing evidence without creating a competing release disposition.
8. Regenerate review and manufacturing outputs after the final mutation.
   Prefer `generate_pcbway_release` for an order-review bundle with a fabrication ZIP, BOM, CPL, settings, and review report.
9. Report evidence, limitations, and unresolved decisions without overstating confidence.

## Do

- `USE_WORKFLOW_PROFILE`: Prefer the small workflow surface and transactional Design Plans.
- `CONTEXT_BEFORE_PLAN`: Read current capabilities and project state before planning.
- `PREVIEW_HASH_REQUIRED`: Validate and preview before applying the exact plan hash.
- `GATES_NOT_JUDGMENT`: Treat ERC, Design Intent, DRC, simulation, test access, ratings, and manufacturing checks as distinct evidence.
- `SIMULATION_EVIDENCE_REQUIRED_FOR_FUNCTION`: Require suitable simulation or physical evidence before claiming electrical function.
- `NO_STALE_EXPORTS`: Generate release files from the final design state.
- `BLOCKS_REQUIRE_PROVENANCE`: Import or create blocks only with source, license, attribution, redistribution status, and immutable payload hashes.
- `BLOCK_MATURITY_REQUIRES_EVIDENCE`: Do not increase block maturity or publish it without the required review, simulation, bench, or production evidence.
- `BEST_PRACTICE_REVIEW_REQUIRES_EVIDENCE`: Complete every qualitative criterion against the exact prepared evidence hash.
- `VISUAL_CLAIMS_REQUIRE_VISUAL_INSPECTION`: Inspect current visual artifacts before passing visual criteria; otherwise mark them unable to assess.
- `UNAVAILABLE_CONSTRAINTS_NOT_PASS`: Treat unresolved nets, missing routed segments, and unfilled zones as unavailable layout evidence, never as passing constraints.
- `GRAPHICS_DO_NOT_IMPLY_INTENT`: A schematic text box has semantic meaning only when Design Intent explicitly maps its UUID with `textBoxUuid`.
- `PCBWAY_REQUIRES_GERBER_BOM_CPL`: For assembly, verify current and mutually consistent Gerber, BOM, and CPL files.
- `ASK_ONLY_FOR_EXCEPTION_DECISIONS`: Ask about material ambiguity, unusual price or sourcing, unsupported risk, gate overrides, and irreversible external actions.

## Do Not

- `NO_RAW_KICAD_OR_SHELL`: Do not use raw KiCad text, shell, PowerShell, scripts, or arbitrary file edits when a supported operation exists.
- `NO_GUI_AS_MUTATION_FALLBACK`: Do not use Computer Use or GUI automation as the normal mutation path.
- `NO_ASSERTION_WEAKENING`: Do not weaken a test merely to make the design pass.
- `NO_FALSE_GUI_REFRESH`: A project-file change is not proof that the KiCad GUI refreshed.
- `NO_SILENT_BLOCK_UPGRADES`: Never overwrite or silently upgrade a block; create a reviewed semantic version and preview its exact hash.
- `LLM_REVIEW_IS_NOT_PROOF`: An LLM best-practice review is structured judgment, not proof of function, EMC, safety, or manufacturability.
- `NO_ORDER_OR_PAYMENT`: Never place an order, pay, publish, or approve component substitutions without the user.

## Release Meaning

ERC checks schematic consistency. Design Intent checks the circuit against explicit project requirements and sourced evidence. DRC checks board rules and connectivity. Simulation checks only the modeled behavior. Manufacturing validation checks output consistency. None alone proves that the physical product works. A PCBWay package may be generated only from a gate-passed transaction, but ordering remains a human action.

If PCBHelper lacks a required operation, report the capability gap. Do not silently bypass the transaction boundary.
