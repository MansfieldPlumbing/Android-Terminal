# Architecture Contract

1. Generation Zero is native C++.
2. CoreCLR never loads into Generation Zero.
3. PowerShell and browser personalities execute in subordinate worker processes.
4. Generation Zero owns identity, domains, worker containment, channels, cancellation and collapse.
5. Remedy-ServiceRequest is the only MCP tool.
6. Each tool call performs one typed intent and returns one settled result.
7. Domain stores executive handles only.
8. No pointer identity crosses process, generation, durable, provider or MCP boundaries.
9. Process death is the final managed-runtime reclamation boundary.
10. Arena, lease, slot, Android, browser, SQLite and PowerShell functionality remain deferred until separately approved.
