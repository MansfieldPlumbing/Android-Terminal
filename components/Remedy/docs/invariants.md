# Proven Invariants (Architecture-Spike-1)

1. **No Raw Pointer Crossings**
   - No raw pointers, virtual addresses, managed references, or callbacks cross process boundaries, generation boundaries, asynchronous ownership boundaries, or ABI interfaces.
   - Pinned token tables (`worker_token_t`, `channel_token_t`) manage platform resources with atomic `op_count` reference tracking.

2. **Stable Segmented Object Table & Pin/Remove Synchronization**
   - Object table slots never move during table growth (segmented chunk arrays).
   - `acquire()` pins active slots atomically.
   - `remove()` transitions state to `REVOKING`, prevents new acquisitions, waits for active pins to drain, destroys the resource, bumps generation, and retires the slot.

3. **Deterministic Single-Shared Domain Collapse**
   - Exactly one thread performs collapse body via `compare_exchange(0, 1)`.
   - Child slots are atomically revoked FIRST at the start of collapse.
   - `REMEDY_DOMAIN_DEAD` is reported ONLY after worker death is verified. If worker termination fails, domain enters `REMEDY_DOMAIN_FAILED`.

4. **Job Object Containment Guarantee**
   - Worker processes start `CREATE_SUSPENDED` inside a Job Object configured with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`.
   - Closing the Job Object handle guarantees death of the complete descendant process tree.

5. **Hardened Little-Endian Wire Codec & Dispatcher Guards**
   - 36-byte little-endian header codec (`remedy_wire_frame_encode` / `remedy_wire_frame_decode`).
   - Adler32 payload checksum verification.
   - Dispatcher rejects completions for collapsed domains with `REMEDY_ERR_LATE_COMPLETION`.
