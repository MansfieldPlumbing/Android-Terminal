# Architecture Specification (Architecture-Spike-1)

## 1. Architectural Taxonomies & Distinctions

### Target Architecture
Generation Zero is a native C++ object executive owning domains, workers, channels, arenas, leases, and slot generations through 64-bit opaque handles. Personalities (PowerShell, Browser) execute strictly in subordinate worker processes. Communication uses explicit framed request/completion envelopes across domain control mailboxes. Process death is the final reclamation boundary.

### Milestone-One Compiled Mechanisms
- **Generation Zero Native Core**: 64-bit handle table with segmented slot storage, atomic pin/remove synchronization, and single-shared domain collapse (`src/native/core/object_table.cpp`, `domain.cpp`).
- **Framed Wire Codec**: 36-byte little-endian header codec (`include/remedy/wire_frame.h`) with Adler32 checksums and exact stream I/O (`read_exact`, `write_exact`).
- **Worker Port (Win32)**: Suspended process startup, Job Object containment with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, process tree termination, and exit status distinction (`src/native/platform/win32/worker_win32.cpp`).
- **Channel Port (Win32)**: Synchronous Named Pipes (`\\.\pipe\remedy-worker-<nonce>`), overlapped connect with deadline timeout, pinned token table, and CancelIoEx cleanup (`src/native/platform/win32/channel_win32.cpp`).
- **Native Echo Worker Host**: C++ worker process (`src/host/win32/remedy_echo_worker.cpp`) supporting Ping/Pong, Echo, Quiesce/QuiesceAck, descendant process spawning, and hostile ignore-quiesce test modes.

### Deferred Mechanisms (Return REMEDY_ERR_NOT_SUPPORTED)
- **PowerShell / CoreCLR Integration**: Not compiled in Spike-1.
- **MCP Courier & SQLite Ledger**: Not compiled in Spike-1.
- **Android Port & JNI / Binder**: Not compiled in Spike-1.
- **Browser Provider (WebView2)**: Not compiled in Spike-1.
- **Arena Port**: `arena_port.h` returns `REMEDY_ERR_NOT_SUPPORTED`. Stale `arena_win32.cpp` deleted.
- **Slot Port**: `slot_port.h` returns `REMEDY_ERR_NOT_SUPPORTED`. Stale `slot_win32.cpp` deleted.
- **Lease Object**: Removed from `remedy_object_type_t` enum until authorization is implemented.

### Currently Proven Invariants
- Zero raw pointers, virtual addresses, callbacks, or vtables cross process, generation, or ABI boundaries.
- Object table slots never move during table lifetime; `acquire()` pins active slots; `remove()` cannot retire a pinned slot until all pins release.
- Domain `collapse()` is single-shared (compare-exchange winner teardown) and atomically revokes all child slots before initiating quiescence/termination.
- Windows Job Objects reliably kill complete descendant process trees (including child `cmd.exe` processes).
- Late completions arriving after domain collapse are rejected with `REMEDY_ERR_LATE_COMPLETION`.
- Framing errors, corrupted headers, or bad checksums immediately terminate channel streams.
