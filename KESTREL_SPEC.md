# Kestrel Design Specification

**Status:** constitutional architecture; first Android silicon receipt not yet implemented
**Role:** optional managed server cargo admitted by BOS and supervised by Remedy
**Cold-baseline status:** absent
**Execution substrate:** CoreCLR worker profile without PowerShell or Roslyn
**Detailed companion:** [Kestrel Cargo Contract](docs/KESTREL-CONTRACT.md)
**Identity companion:** [Terminal Identity Contract](docs/IDENTITY-CONTRACT.md)

---

## 1. Product Thesis

Kestrel is Terminal's maintained HTTP server edge. It is not part of BOS Core,
not a PowerShell feature and not justification for ambient filesystem or network
authority.

The architectural question is no longer whether Kestrel can be packaged for
Android. A retired Subsystem donor established a viable managed assembly path.
The question Terminal must answer is:

> **Can Kestrel serve on Android while obeying BOS admission and Remedy lifetime semantics?**

---

## 2. Runtime Topology

Terminal's cold baseline remains small:

```text
Terminal bootstrap
├─ Terminal presentation
├─ BOS NativeAOT
└─ Remedy Generation Zero
```

Kestrel appears only after admitted invocation:

```text
BOS:> SERVE ...
        │
        ▼
BOS policy and capability admission
        │
        ▼
Remedy adapter
        │
        ▼
Remedy WorkerGeneration
        │
        ├─ CoreCLR
        ├─ Terminal Kestrel host
        └─ narrowly staged ASP.NET Core managed closure
```

CoreCLR is an executor substrate, not another name for PowerShell:

```text
dotnet.kestrel
    CoreCLR
    Kestrel host
    admitted ASP.NET Core closure
    no PowerShell
    no Roslyn

dotnet.pwsh
    CoreCLR
    PowerShell
    Roslyn only when separately admitted
```

The profiles may share an executor implementation without sharing a worker,
authority set, dependency closure or application API.

---

## 3. Authority and Ownership

The maintained request path is:

```text
Internet / LAN / loopback
        │
        ▼
dedicated Kestrel worker
        │ authenticated, admitted request
        ▼
narrow BOS capability IPC
        │
        ▼
BOS authorization and platform adapter
```

It is not:

```text
network request
    → PowerShell runspace
    → ambient command composition
```

Ownership is explicit:

```text
BOS
    policy, requested capabilities, listener/storage admission

Remedy
    worker generation, containment, cancellation, quiescence,
    death observation, fencing and retirement

Kestrel worker
    HTTP parsing, endpoint execution, bounded server state and
    the exact socket/storage capabilities granted to its generation

Android
    application identity, permissions, process/service lifecycle,
    SELinux and platform network/storage enforcement
```

The Kestrel host never decides that it may widen a loopback listener to
`0.0.0.0`. It receives an already-admitted `NetworkListenerLease` and binds
exactly the address, port, transport and authentication policy that lease
permits. File serving additionally requires an independent `StorageRootLease`.

Possession of one lease conveys no rights from the other.

---

## 4. Donor Evidence

The retired donor is:

```text
S:\reference\New folder\_retired\subsystem-kestrel
```

Its final project configuration records this packaging mechanism:

```text
Microsoft.AspNetCore.App.Runtime.linux-arm64
        │
        ├─ portable managed PE assemblies  → usable donor material
        │
        └─ foreign native cargo             → not imported by association
```

The donor could not use an Android ASP.NET Core runtime pack (`NETSDK1082`).
Windows x64 ReadyToRun shared-framework images were also incompatible with the
Android ARM64 process. The working build staged the Linux ARM64 pack's managed
assemblies as private references and excluded the irrelevant native IIS module.

The archived evidence proves:

- the Android project compiled and produced a signed APK containing Kestrel;
- the Windows donor returned HTTP 200 from `/health` and `/diag`;
- the current donor project records the managed Linux ARM64 staging mechanism.

It does **not** contain the promised physical-Android `/health` result. The log
left that operation as its next step. Terminal must not describe packaging
success as silicon serving success.

The donor is mined, not resurrected:

```text
retain as evidence
    managed assembly path
    endpoint shape
    loopback-first behavior
    packaging failure modes

reuse selectively
    CreateSlimBuilder
    explicit Kestrel Listen configuration
    deterministic health endpoint

do not inherit
    in-process static singleton ownership
    fire-and-forget startup
    swallowed startup failure
    synchronous shutdown waits
    hard-coded ports
    DEV auto-start
    ambient PowerShell/runspace access
    firewall policy embedded in the host
```

---

## 5. Artifact Admission Rule

> **Platform labels on donor runtime packs are not themselves an execution
> boundary. Individual artifacts are admitted by binary format, runtime
> requirements, dependency closure, provenance and executable receipts.
> Portable managed assemblies may be staged for Android; foreign native
> libraries may not be imported merely because they accompanied those
> assemblies in the donor pack.**

Consequently, neither of these shortcuts is permitted:

```text
delete everything under linux-arm64
copy everything under linux-arm64
```

Terminal instead builds and records the exact tested closure:

```text
Kestrel package
├─ manifest
├─ managed/
│  ├─ Terminal Kestrel host
│  └─ exact admitted ASP.NET Core dependency closure
├─ config/
└─ licenses/
```

Exclude foreign native/glibc payloads, Windows IIS/native hosting modules, x64
ReadyToRun images, unrelated RIDs and duplicate runtime hosts. No file is kept
or deleted merely because its directory name contains `linux`.

---

## 6. First Android Silicon Receipt

The first receipt remains deliberately unimpressive:

```text
BOS:> SERVE /ZONE:LOOPBACK /PORT:0
Started: Job K1 on 127.0.0.1:<actual-port>

GET http://127.0.0.1:<actual-port>/health
    → HTTP 200
    → deterministic body

BOS:> STOP K1
```

The executable receipt must assert:

```text
listener lease admitted
Remedy launches dotnet.kestrel generation N
worker READY reports the effective address and port
physical Android request returns the expected response
STOP revokes the lease and requests explicit QUIESCE
Kestrel stops accepting
worker acknowledges the defined shutdown point
worker exits
Remedy observes terminal channel closure and death
generation N retires
the port no longer accepts
fd, thread and memory counts return to warmed baseline
BOS remains alive and usable
```

There are no authentication, PSRP, WebSocket, file-server or public-network
requirements in this first receipt. Those features cannot be used to obscure a
failure of basic admission, serving or reclamation.

---

## 7. Constitutional Non-Goals

Kestrel does not become:

- BOS's command processor;
- a network path directly into a PowerShell runspace;
- a permanent resident of Terminal's idle process;
- its own permission or lifecycle authority;
- a reason for Remedy Generation Zero to understand HTTP, ASP.NET Core or
  PowerShell;
- a generalized remote execution framework;
- an ambient owner of Android storage;
- evidence that Terminal controls Android's kernel firewall;
- permission to expose `0.0.0.0` by default.

The maintained network edge should remain boring, explicit and killable.
