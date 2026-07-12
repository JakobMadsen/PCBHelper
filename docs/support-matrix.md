# Support Matrix

| Component | v0.1-alpha status | Notes |
| --- | --- | --- |
| Windows 11 x64 | Supported | Official user platform. |
| KiCad 10.0.x | Supported | `kicad-cli` is required; 10.0.4 is the primary tested version. |
| KiCad 9.x | Best effort | Core formats may work, but alpha release validation targets KiCad 10. |
| VS Code/Copilot MCP | Supported | Stdio workflow profile is the primary client path. |
| Codex MCP | Supported | Uses the same client-neutral workflow contract. |
| .NET 10 | Supported | Required for source builds; release ZIP is self-contained. |
| ngspice | Optional | Required for deterministic simulation assertions. |
| Java 21+ | Optional | Required only for FreeRouting. |
| Linux container | CI/test only | Not an official v0.1 user platform. |
| KiCad GUI refresh | Capability-gated | File mutation does not imply live GUI refresh. |

PCBHelper does not support mains, medical, safety-critical, RF, high-speed, or high-current design in v0.1.
