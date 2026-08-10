# ADR 0001: Fresh Repository Architecture

## Context
Existing projects in the ecosystem (`subsystem`, `terminal`, `agent-browser`, `TUI-DWM`) contain valuable proof-of-concept code and mechanisms. However, they carry legacy assumptions:
- CoreCLR loaded directly into root.
- Proliferation of uncoordinated MCP tools.
- Complex VOM path routing and universal state buckets (Cm).
- Platform-dependent macros scattered throughout common code.

## Decision
Remedy is created as a completely fresh repository with a clean Git history.
- Existing projects are designated as **donor repositories** and reference implementations.
- No code will be copied wholesale.
- Mechanisms are transplanted only when accompanied by characterization tests and pinned commit references.

## Consequences
- Clean architecture without legacy technical debt.
- Generation Zero remains strictly native C++ with no CoreCLR dependency in root.
- Strict isolation of platform specifics behind four C ABI ports.
