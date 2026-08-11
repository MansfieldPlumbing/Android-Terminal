# BOS Organizational Contract

**Status:** constitutional companion to `BOS_SPEC.md`
**Scope:** ownership, trust, reporting lines, presentation and reclamation
**Target:** Terminal v0 on Android, with platform-specific executors beneath the same semantic boundary

This document answers one question:

> **When Terminal does something, which component is allowed to decide it, perform it, present it and clean it up?**

It is intentionally an organizational chart rather than an implementation guide. Components may change internally without changing these reporting lines.

---

## 1. Canonical Organization

```text
Android OS
  final application, permission and process authority
  |
  `-- Terminal bootstrap process (trusted resident baseline)
      |
      |-- Terminal presentation
      |     owns the one active presentation root
      |     projects BOS, VT and admitted surfaces
      |
      |-- BOS semantic core
      |     owns commands, policy, admission and semantic results
      |     |
      |     |-- Android capability adapters
      |     |     framework / Binder / Bionic / platform operations
      |     |
      |     `-- Remedy adapter
      |           translates admitted execution into generic lifecycle work
      |
      `-- Remedy Generation Zero
            owns subordinate identity, containment and retirement
            |
            `-- WorkerGeneration N (disposable process boundary)
                  |
                  |-- NativeAOT terminal router
                  |     owns routes, PTY/ConPTY, children and buffers
                  |     `-- pwsh / bash / edit / other console cargo
                  |
                  |-- native cargo host
                  |     `-- admitted packaged .so
                  |
                  `-- other admitted worker cargo
```

The arrows are authority and ownership relationships, not a mandatory data path. Direct Android operations do not detour through Remedy.

```text
BOS> TORCH ON
    -> BOS policy/admission
    -> Android TorchAdapter
    -> CameraManager / Binder

BOS> PWSH
    -> BOS policy/admission
    -> Remedy adapter
    -> Remedy WorkerGeneration
    -> terminal router
    -> pwsh cargo
```

### Constitutional sentence

> **BOS/platform policy admits execution; Remedy enforces subordinate generation, authority, lifetime, fencing and retirement; Terminal presents the admitted outcome; Android remains the final platform authority.**

> **Bootstrap residency is not execution authority.**

Terminal presentation, BOS and Remedy Gen0 coexist in the cold baseline because each has one irreducible responsibility. Co-residency does not permit any one of them to absorb the other two.

---

## 2. Authority Matrix

| Component | Owns | Must not own | Final cleanup boundary |
|---|---|---|---|
| Android OS | App identity, permissions, process and component lifecycle, Binder/SELinux enforcement | BOS command semantics, Terminal session identity | Kernel and framework reclamation |
| Terminal bootstrap | Construction and orderly shutdown of the trusted resident baseline | Cargo policy or platform impersonation | Android process lifetime |
| Terminal presentation | One active root, input projection, BOS/VT/surface transitions, visual state | Capability admission, worker identity, shell execution | Activity/View lifecycle |
| BOS semantic core | Commands, typed capabilities, policy, grants, leases, catalog, provenance, invocation outcomes | Raw Android object graphs, worker containment, PTY/HPCON mechanics | Its resident process; scoped leases before process death |
| Android adapter | One narrow platform capability and its acquired resources | Global BOS policy, arbitrary command parsing, worker lifecycle | Adapter plus `CapabilityLease` |
| Terminal Remedy adapter | Translation from admitted Terminal execution to Remedy's stable ABI | Terminal policy inside Remedy, raw Terminal identities inside Gen0 | Terminal bootstrap |
| Remedy Generation Zero | Worker generations, admitted endpoints, cancellation, collapse, fencing, death and retirement | BOS policy, Terminal sessions, ConPTY, PowerShell, VT, Android feature semantics | Gen0 plus OS process containment |
| Terminal router | Route table, PTY/ConPTY, child processes, bounded buffers, resize and route shutdown | Durable Terminal `SessionId`, BOS admission, Gen0 policy | Router quiesce; Gen0 collapse if it fails |
| Native cargo host | Loading one admitted native package inside a disposable worker boundary | Loading arbitrary native code into BOS, exporting raw pointers | Host exit; Gen0 retirement |
| Cargo | Its own domain behavior | Ambient authority, durable identity derived from PID/HANDLE/fd | Owning route/worker generation |

No row may silently acquire a responsibility from another row merely because doing so is convenient in one implementation.

### Vocabulary firewall

Dependencies are constrained by which layer's nouns a component may know:

```text
Terminal presentation may know
  InvocationOutcome, RouteId, SurfaceLease, presentation state

BOS may know
  semantic capabilities, ExecutorId, CapabilityLease,
  RouteId as an opaque admitted result, Remedy adapter contract

Remedy Gen0 may know
  worker, generation, endpoint, frame, request, completion,
  correlation, cancellation, quiescence, death, retirement

Router may know
  route, PTY/ConPTY, child process, bytes, resize, exit, buffer
```

Remedy Gen0 must not learn `PWSH`, Bash, Edit, BOS command names, panes, tiles, Android components, PowerShell or CoreCLR. BOS must not learn `forkpty` mechanics, pid/fd/HANDLE/HPCON identity, Gen0 storage layout or Remedy heap internals.

---

## 3. Trust and Process Zones

```text
TRUSTED RESIDENT BASELINE
  Terminal presentation
  BOS semantic core
  Remedy Generation Zero

NARROW PLATFORM BASEMENT
  typed Android adapters
  stable Remedy C ABI
  platform-specific spawn/PTY/ConPTY shims

DISPOSABLE WORKER ZONE
  router
  PowerShell/CoreCLR/Roslyn
  bash and native console tools
  editor cargo
  servers
  packaged native shared objects
```

The resident baseline must stay small, statically understandable and AOT-compatible. Rich or dynamically extensible code belongs above a disposable worker boundary unless it has earned a narrow trusted adapter.

BOS is **not** an ordinary Remedy worker in v0. It must remain available to report and recover from subordinate worker retirement. Process-isolating BOS later would require a separate bootstrap recovery console and its own executable receipts.

---

## 4. Decision, Execution, Presentation

These are separate decisions:

```text
parse and resolve
      -> BOS

admit capability or execution
      -> BOS / platform policy

perform direct Android operation
      -> owning Android adapter

contain subordinate execution
      -> Remedy Gen0

route interactive byte streams
      -> Terminal router

choose what the user sees
      -> Terminal presentation
```

BOS returns a typed presentation result. Terminal does not infer presentation from executable name, runtime, process identity or the fact that a command is external.

```csharp
public abstract record InvocationOutcome;

public sealed record InlineResult(Result Value) : InvocationOutcome;
public sealed record InteractiveTerminal(RouteId Route) : InvocationOutcome;
public sealed record AndroidHandoff(PlatformComponentRef Component) : InvocationOutcome;
public sealed record PresentableSurface(SurfaceLease Lease) : InvocationOutcome;
public sealed record BackgroundJob(JobId Job) : InvocationOutcome;
```

`InvocationOutcome` describes presentation semantics, not execution mechanism. `PlatformComponentRef` is opaque; Android `Intent`, `Context`, `Activity`, Binder proxies and other platform object graphs do not cross into BOS Core.

Cargo does not mint authoritative presentation outcomes. An admitted executor adapter validates the executor result and constructs the corresponding `InvocationOutcome` inside BOS's semantic path. Terminal accepts that typed outcome, not an untrusted cargo request to change presentation. This prevents a package from manufacturing a `RouteId`, `SurfaceLease` or Android handoff authority.

---

## 5. One Presentation Root

Terminal owns one active presentation root in v0.

```text
BOS semantic presenter
        |
        | InteractiveTerminal(RouteId)
        v
VT presenter attached to route
        |
        | route closes
        v
restored BOS semantic presenter
```

The root is stable; its projection changes. Pinger, prompt, overlays and basic shell chrome share that root. An interactive TUI does not create a second application or UI framework. It switches the existing root into VT presentation and may use primary screen, alternate screen, cursor modes and mouse reporting within that route.

An Android handoff may legitimately leave the Activity because Android owns that component transition. It does not transfer BOS authority to the destination component.

---

## 6. Native Package Reporting Line

Packaged native code is admitted cargo, not a plugin inside the resident core.

> **Package installation may extend the catalog. It may not extend the bootstrap address space.**

```text
package manifest
  ExecutorId = native.shared-object
        |
        v
BOS provenance + capability admission
        |
        v
Remedy WorkerGeneration
        |
        v
disposable native cargo host
        |
        `-- dlopen(admitted package.so)
```

Rules:

1. BOS never `dlopen`s arbitrary packaged `.so` files into its process.
2. A platform adapter may be resident only when it is narrow, reviewed and compiled into the trusted baseline.
3. Raw pointers and native object identity remain inside the cargo host.
4. Only framed semantic messages, opaque handles and explicitly admitted resource endpoints cross the worker boundary.
5. Package failure retires its generation; it does not corrupt BOS or another package.

---

## 7. Identity Ledger

The following identities are intentionally distinct:

```text
Terminal SessionId
    != Router RouteId
    != Remedy WorkerGeneration
    != CapabilityLeaseId
    != Android component identity
    != PID / HANDLE / fd / HPCON / PTY
```

Numeric reuse does not resurrect authority. A valid operation requires possession of the correct opaque, generation-qualified authority at the layer that owns it.

---

## 8. Cleanup Ladders

Route retirement and worker-generation retirement are different operations.

### Route retirement

```text
shell/TUI route exits or is closed
  -> router releases that route's child, PTY/ConPTY, pipes and buffers
  -> router emits CLOSED(RouteId)
  -> Terminal retires the route mapping
  -> Terminal restores BOS if that route owned the active VT projection
  -> router worker generation and unrelated routes remain alive
```

Gen0 does not observe ordinary route-local shell semantics and does not retire a healthy multiplexer merely because one route closed.

### Worker-generation retirement

```text
admitted shutdown, policy revocation or bootstrap teardown
  -> Terminal Remedy adapter requests worker quiescence
  -> Gen0 transmits explicit QUIESCE
  -> router / cargo host closes all locally-owned routes and resources
  -> worker acknowledges the defined quiescent point and exits
  -> Gen0 observes terminal channel closure and worker death
  -> Gen0 fences stale authority and retires WorkerGeneration
  -> BOS releases associated grants/leases and reports the result
```

Worker crash, channel failure or failed quiescence enters the same Gen0 fencing and retirement path without pretending that orderly acknowledgement occurred.

For direct platform capabilities:

```text
BOS operation completes or lease is revoked
  -> owning Android adapter unregisters listeners/releases resources
  -> CapabilityLease retires
```

If orderly worker cleanup fails, Gen0 collapses the contained generation. Android remains the final process-reclamation authority.

### Evidence boundary

Windows Job Object kill-on-close containment is proven. Android orderly lifecycle, explicit quiesce and worker retirement are proven. Catastrophic Android Gen0/parent death preventing orphan descendants is a required receipt, not a completed claim. The organization must not rely on that stronger claim until it is mechanically demonstrated.

---

## 9. Failure and Recovery Ownership

| Failure | Responsible response |
|---|---|
| Android permission denied | Adapter returns a typed denial; BOS explains/remediates; Remedy is uninvolved |
| Direct capability fails | Adapter releases partial resources; BOS reports failure |
| VT route exits normally | Router closes only that route; Terminal restores parked BOS state when it was active; other routes and the router remain valid |
| Routed shell cargo crashes | Router closes and reports that route; Gen0 remains uninvolved unless the router worker itself fails |
| Native cargo host or router worker crashes | Gen0 observes worker/channel death, retires and fences the generation; BOS remains available |
| Router ignores quiesce | Gen0 collapses worker generation; Terminal reports loss, without inventing recovery |
| BOS command or policy rejects launch | No worker generation is created |
| Terminal presentation is recreated | Reattach only through durable semantic state and valid route/lease identity |
| Whole Terminal process dies | Android performs final process cleanup; stronger descendant-containment claims require the Android parent-death receipt |

Automatic restart, transparent shell replacement and persistent sessions after router death are not implied by this chart.

---

## 10. Prohibited Organizations

The following shapes violate the contract:

```text
Remedy -> decides whether torch access is allowed
Remedy -> learns PowerShell, ConPTY, VT or Terminal SessionId
BOS -> owns HANDLE/fd/HPCON values as durable identity
BOS -> loads arbitrary packaged native code in-process
Terminal UI -> directly manipulates worker generations
Android adapter -> parses global BOS commands or defines product policy
router -> becomes the durable Terminal session authority
every output byte -> becomes a Remedy lifecycle frame
external command -> automatically implies VT mode
second overlay window -> added for convenience despite one-root semantics
cargo -> mints an authoritative InvocationOutcome or presentation lease
one route closes -> entire healthy router generation is retired
```

The compact review question is:

> **Is this code deciding meaning, performing a platform action, supervising mortality, routing bytes or presenting an outcome?**

If it performs more than one of those jobs, its boundary deserves scrutiny.

Every new component must answer five questions without inventing a new reporting line:

1. Who admits or authorizes it?
2. What exactly does it own?
3. Which vocabulary may it know?
4. Who presents its result?
5. Who cleans it up on both success and failure?

---

## 11. v0 Receipt Shape

The first organizational receipt is deliberately small:

```text
BOS> VER
  -> InlineResult
  -> no Remedy worker

BOS> VIBRATE 40
  -> Android adapter
  -> no Remedy worker

BOS> TORCH ON
  -> Android adapter
  -> no Remedy worker

BOS> PWSH
  -> BOS admits execution
  -> Remedy launches an admitted router generation if none is active
  -> router returns InteractiveTerminal(RouteId)
  -> Terminal switches the existing root to VT
  -> PowerShell exits
  -> route closes
  -> because it is the final route, router quiesces and generation retires
  -> Terminal restores BOS>
```

The receipt succeeds only if direct capabilities never touch Remedy, interactive cargo cannot outlive its admitted generation, the presentation root remains singular and the idle state returns to BOS plus an idle Gen0 with zero subordinate workers. In a multi-route receipt, closing one route must leave the router generation and every other admitted route alive.
