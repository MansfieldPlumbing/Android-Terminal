# Kestrel Cargo Contract

**Status:** planned vertical receipt; not yet implemented in Terminal
**Role:** optional managed server cargo supervised through BOS and Remedy
**Non-role:** permission justification, resident BOS machinery, or ambient filesystem authority

**Architectural authority:** [Kestrel Design Specification](../KESTREL_SPEC.md)
**Authentication companion:** [Terminal Identity Contract](IDENTITY-CONTRACT.md)

Kestrel is a strong proof of BOS lifecycle semantics because it combines a long-lived managed worker, an externally observable resource and explicit shutdown. It remains optional cargo.

---

## 1. Topology

```text
BOS command / admitted app
    |
    | requests NetworkListenerLease + optional StorageRootLease
    v
BOS policy and release-profile admission
    |
    v
Remedy adapter
    |
    v
Remedy WorkerGeneration
    |
    v
managed server worker
    `-- Kestrel

validated startup result
    -> BackgroundJob(JobId)
    -> Terminal notification/status projection
```

Kestrel does not run inside BOS Core. It does not receive Android `Context`, `Activity`, Binder objects or the complete app filesystem merely because the application process could acquire them.

`BackgroundJob` describes presentation semantics. It does not make the job its own lifecycle authority. Remedy owns worker mortality; BOS owns admission and leases; Android owns the application and foreground-service rules.

CoreCLR is an executor substrate, not another name for PowerShell. The maintained
Kestrel edge and an interactive PowerShell session are separate cargo profiles:

```text
dotnet.kestrel
    CoreCLR
    Terminal Kestrel host
    narrowly admitted ASP.NET Core managed closure
    no PowerShell
    no Roslyn

dotnet.pwsh
    CoreCLR
    PowerShell
    Roslyn only when separately admitted
```

Neither profile belongs in Terminal's cold BOS baseline. A network request reaching
the Kestrel worker may invoke only the narrow BOS capability IPC admitted for that
endpoint; it does not receive a PowerShell runspace as an ambient application API.

---

## 2. Independent Authority

A server must receive two independent grants when it serves files:

```text
NetworkListenerLease
    bind scope: loopback | local-network | explicitly admitted interface
    requested port and actual port
    transport: HTTP | HTTPS
    authentication policy
    owning JobId and WorkerGeneration
    cancellation and expiry

StorageRootLease
    one or more admitted roots
    rights: enumerate | read | create | write | delete
    traversal and link policy
    owning JobId and WorkerGeneration
    cancellation and expiry
```

Possessing one lease conveys no authority from the other. A loopback API with no storage lease cannot read files. A file-capable job with no LAN listener lease cannot expose them to the subnet.

Storage access should use scoped Android storage contracts or brokered descriptors/streams where practical. A release profile approved for broad file management may admit a wider root, but the worker still receives only the root and rights declared for that job.

---

## 3. Exposure Defaults

Defaults fail inward:

```text
address        loopback
port           unprivileged and conflict-checked
LAN exposure   off
authentication required before LAN admission
file roots     none
write/delete   off
directory list off until explicitly requested
```

Binding `0.0.0.0` is an explicit local-network exposure decision, not a convenience fallback. Terminal must show the effective interfaces, port, transport, authentication state and served roots before admission and in the durable job notification.

The Mansfield-maintained authentication mode uses the optional `mansfieldplumbing.dev` Google/Microsoft ceremony. Owners may select an explicitly configured local or owner-managed authentication mode. Kestrel must display which authority protects each listener; it must never fall back from a failed maintained login to anonymous access.

The maintained portal authenticates; it is not a traffic relay. Internet reachability still comes from an owner-chosen network path such as a private overlay, reverse proxy, tunnel or router configuration. Terminal must report authentication authority and network path as separate facts.

Android's cleartext-traffic application setting governs participating platform/client stacks; it is not the authority that permits an inbound Kestrel socket to bind. HTTP versus HTTPS is a server exposure policy and must be tested directly. Do not set global `usesCleartextTraffic=true` merely to make Kestrel listen.

Self-ADB authority is unrelated. The Terminal app and its Kestrel worker do not become uid 2000 because an independently authenticated ADB shell exists.

### Exposure zones

The listener lease names its exposure honestly:

```text
Loopback
    127.0.0.1 / ::1
    reachable only on the device

LocalNetwork
    explicitly selected local interface/address
    authentication on by default

AllInterfaces
    0.0.0.0 and/or ::
    includes every interface admitted by the socket,
    including interfaces that may appear after startup
```

These zones are semantic listener policy, not a claim that Terminal controls Android's kernel firewall. Terminal must report the effective bound addresses and observed interfaces.

`AllInterfaces + Authentication=None` is a deliberately unsafe owner override. It is permitted because the device belongs to the user, but it requires a local, interactive confirmation that cannot be supplied by the remote principal requesting exposure.

The minimum confirmation is intentionally blunt:

```text
Open projection on every reachable interface with no authentication?

Anyone who can reach this port may see Terminal output. New network
interfaces may become exposed while this listener is active.

Are you really, really, really sure? [N]
Type Y to continue: _
```

Rules:

- default and empty input are `N`;
- only an explicit uppercase `Y` from the local interactive owner admits the lease;
- cancellation, Activity loss or timeout denies it;
- scripts and remote callers cannot synthesize the confirmation with a generic `--force` switch;
- the active notification reads `Projection · All interfaces · No authentication · <port>` and always exposes Stop;
- adding or changing an interface updates status without silently changing the recorded zone;
- restarting Terminal does not recreate the unsafe lease unless an independently reviewed persistent policy is added later.

This is not a security recommendation. It is a truthful, recoverable escape hatch.

---

## 4. Foreground-Service Projection

An active, user-requested durable server may require an Android foreground-service projection:

```text
BOS BackgroundJob
    -> Android job-presence adapter
    -> foreground service + durable notification
```

The service:

- makes active work user-perceptible;
- exposes Open, Status and Stop controls;
- forwards Stop into BOS cancellation and Remedy quiescence;
- does not own Kestrel, its sockets or its storage authority;
- does not promise immunity from Android process death.

Do not label an indefinite server `dataSync` merely to avoid `specialUse`; current Android versions impose time limits on `dataSync`. Do not label it `connectedDevice` without satisfying that type's platform prerequisites and actual semantics. If `specialUse` is the accurate type, the manifest must include a concrete `PROPERTY_SPECIAL_USE_FGS_SUBTYPE` explanation and the Play declaration must describe the same user-visible behavior.

---

## 5. Android Packaging

The packaging rule is not “remove all Linux assemblies.” Android is a Linux/Bionic platform.

### Known-good Subsystem donor

The retired Subsystem Kestrel branch records a useful Android packaging proof. Its
`net11.0-android` project could not consume `Microsoft.AspNetCore.App` through an
Android runtime pack (`NETSDK1082`), and the Windows shared-framework assemblies
were x64 ReadyToRun images that failed on ARM64. The successful build instead staged
the **managed DLLs** from `Microsoft.AspNetCore.App.Runtime.linux-arm64` and embedded
them as private references. The donor describes these images as portable managed
IL (`PE machine 0xD11D`) and deliberately excludes the irrelevant native IIS module.

That evidence sharpens the rule:

```text
keep
    required ASP.NET Core managed assemblies from the compatible ARM64 pack

exclude
    foreign native/glibc payloads
    Windows IIS/native hosting modules
    x64 ReadyToRun images
    unrelated RIDs and duplicate runtime hosts
```

Platform labels on donor runtime packs are not themselves an execution boundary.
Individual artifacts are admitted by their binary format, runtime requirements,
dependency closure, provenance and executable receipts. Portable managed assemblies
may be staged for Android; foreign native libraries are not admitted merely because
they accompanied those assemblies in the donor pack.

Do not copy the donor's entire assembly set blindly. Produce a resolved inventory
for Terminal's worker, then reduce it only with executable receipts. The donor log
proves a signed Android APK was assembled with Kestrel assets; it does **not** contain
the promised physical-device `/health` receipt. Terminal must supply that receipt.

The rule is:

1. publish the worker for the exact Android RID and ABI;
2. include the managed ASP.NET Core/Kestrel closure actually required by the worker;
3. exclude glibc-specific native assets, irrelevant desktop runtime packs, duplicate hosts and unsupported RIDs;
4. inventory every remaining assembly and native library in the produced artifact;
5. prove startup and request handling on the physical Android target;
6. treat NativeAOT Kestrel as a separate receipt from CoreCLR-hosted Kestrel.

No assembly is deleted merely because its filename looks Linux-shaped. Removal must be justified by the resolved dependency graph and followed by an executable receipt.

For the Play release profile, executable cargo must be delivered through the admitted Play artifact or another explicitly Play-compliant mechanism. The owner/sideload profile may have a broader package-origin policy without changing BOS semantics.

---

## 6. Source Policy Is Not Containment

`TRM004` is a useful governance tripwire: a source contribution that constructs a listener must declare its intended lifecycle owner. It is not a security boundary and does not grant a listener lease.

Actual enforcement is layered:

```text
source policy receipt
    + package provenance
    + BOS capability admission
    + generation-qualified leases
    + Remedy worker containment
    + Android permissions and lifecycle
```

A PowerShell endpoint invokes semantic BOS capabilities. It does not reach directly into Android adapters or inherit every capability held by the interactive user. Remote origin, authentication and the endpoint's own grants remain part of every request.

---

## 7. First Receipt

The first Kestrel receipt should remain deliberately small:

```text
BOS> SERVE START --LOOPBACK --PORT 0
    -> listener lease admitted
    -> managed worker generation READY
    -> BackgroundJob(JobId)
    -> actual loopback port reported

GET /health
    -> 200 with deterministic body

BOS> JOB STOP <JobId>
    -> lease cancellation
    -> Kestrel stops accepting
    -> explicit Remedy QUIESCE
    -> worker exits
    -> generation retires
    -> port no longer accepts
    -> fd/thread/memory counts return to warmed baseline
```

Then extend the receipt, in order:

1. two concurrent servers cannot steal or misattribute each other's ports;
2. a revoked listener lease closes exposure without revoking an unrelated job;
3. an admitted read-only storage root cannot escape by `..`, links or alternate path spelling;
4. LAN exposure requires explicit consent and authentication;
5. stopping the Android notification performs the same bounded cancellation path;
6. Activity destruction stops presentation work but does not terminate an admitted durable job;
7. Terminal process death is recovered according to Android's actual lifecycle evidence, never assumed.

Do not add a file browser, WebDAV, PSRP, dynamic endpoint framework or public-network exposure to the first receipt.
