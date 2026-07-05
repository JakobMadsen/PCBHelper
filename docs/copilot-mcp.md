# VS Code / Copilot MCP Setup

PCBHelper exposes its first agent integration as a local MCP stdio server.

Use this with VS Code Copilot Agent Mode by adding an MCP server entry similar to this in `.vscode/mcp.json`:

```json
{
  "servers": {
    "pcbhelper": {
      "type": "stdio",
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "src/PCBHelper.Mcp"
      ]
    }
  }
}
```

Then start the server from VS Code's MCP UI and use Copilot Agent Mode.

First exposed tools:

- `doctor`
- `get_project_summary`
- `run_erc`
- `run_drc`
- `run_checks`

No AI provider keys are required by PCBHelper itself. Copilot, Codex, Ollama, or any future client is responsible for its own model configuration.
