# ADR 0003: Single MCP Tool Interface (`Remedy-ServiceRequest`)

## Context
Multi-tool MCP schemas lead to tool proliferation, brittle schema synchronization, and agents attempting uncoordinated multi-step execution.

## Decision
Remedy exposes exactly one MCP tool: **`Remedy-ServiceRequest`**.

### Input Schema
```json
{
  "intent": "string",
  "state": "optional opaque state handle string"
}
```

### Output Schema
```json
{
  "state": "S-0007:18",
  "serviceRequest": "SR000000000184",
  "phase": "Working",
  "display": "...human-readable settled HUD...",
  "intents": [
    {
      "command": "Invoke-PowerShell <script>",
      "effect": "Executes script inside domain runspace",
      "risk": "mutating"
    },
    {
      "command": "End-Service -Disposition Completed -Summary <text>",
      "effect": "Closes service request and collapses transient domain",
      "risk": "destructive"
    }
  ],
  "receipt": "ACT000000003912"
}
```

### Rules
1. **One-action per turn**: Each tool call executes one intent, waits until settled, and returns the updated state + HUD.
2. **Grammar outside, closed typed intent inside**: The intent string is parsed into a typed internal enum structure before execution. Arbitrary string execution in root is strictly forbidden.
3. **Closed Dispositions**: End of service uses closed set (`Resolved`, `Completed`, `Deferred`, `Transferred`, `Cancelled`, `Failed`). Free-form text is placed only in the summary parameter.
