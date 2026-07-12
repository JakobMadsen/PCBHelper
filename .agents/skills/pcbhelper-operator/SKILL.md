---
name: pcbhelper-operator
description: Operate small PCB projects through PCBHelper's transactional MCP workflow.
---

# PCBHelper Operator

Use this skill when creating, changing, checking, simulating, reviewing, or packaging a small PCB with PCBHelper.

1. Use the `workflow` MCP profile.
2. Read `pcbhelper://agent-guide/v1`; if resources are unsupported, call `get_agent_guide`.
3. Call `get_capabilities` every session. Never assume an operation shape from memory.
4. Call `get_project_context`, then express related reversible changes as one Design Plan.
5. Validate, preview, and apply only the exact returned `planHash`.
6. Run engineering gates and correct ordinary findings autonomously.
7. Regenerate review/manufacturing outputs after the last mutation.

Do not bypass PCBHelper with raw KiCad edits, shell commands, or GUI automation when an advertised operation exists. Never place an order, pay, publish, or approve substitutions. The MCP guide is canonical if this adapter and the server differ.
