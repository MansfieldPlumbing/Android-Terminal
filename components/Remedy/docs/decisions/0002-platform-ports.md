# ADR 0002: Platform Division via Four Concrete Ports

## Context
Cross-platform native applications often fall into the trap of inventing broad `IPlatform` abstractions for everything (clocks, strings, filesystems, threads, databases), cluttering core logic.

## Decision
Remedy divides platform dependencies strictly by mechanism, limited to exactly four initial ports:
1. **Worker Port**: Lifecycle and monitoring of managed worker processes.
2. **Channel Port**: IPC transport for request envelopes and completions.
3. **Arena Port**: Shared memory mapping and cross-process buffer sharing.
4. **Slot Port**: Execution generation staging, activation, and rollback.

### Rule of Platform Isolation
- Core native code (`src/native/core`) MUST NOT contain any platform macros (`_WIN32`, `__ANDROID__`), OS headers (`windows.h`, `binder.h`), or platform-specific types (`HANDLE`, `int fd`).
- All platform implementations reside exclusively in `src/native/platform/win32` and `src/native/platform/android`.

## Consequences
- Core native logic is 100% platform-agnostic and unit-testable.
- Platform mechanisms are cleanly swapped per OS host build.
