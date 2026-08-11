# BOS Design Specification

**Status:** constitutional architectural baseline; v0 not yet implemented
**Target:** Android first
**Role:** lightweight Binder-oriented operating environment beneath Terminal and above Android platform mechanisms
**Primary human interface:** deliberately small DOS-shaped command processor
**Subordinate worker lifecycle substrate:** Remedy
**Heavyweight optional inhabitant:** PowerShell/CoreCLR
**Design priority:** make the phone itself feel like an open-ended computer without pretending Android’s security model does not exist.

**Companion contract:** [BOS Organizational Contract](docs/BOS-ORGANIZATION.md)
**Optional server cargo:** [Kestrel Design Specification](KESTREL_SPEC.md)
**Remote identity:** [Terminal Identity Contract](docs/IDENTITY-CONTRACT.md)

---

## 1. Product Definition

BOS is the **stable operating personality** presented by Terminal on Android.

It is not a Linux distribution, not a shell wrapper, and not another compatibility environment. It takes Android's actual native capabilities—Binder services, framework APIs, application identity, package operations, surfaces, storage, media, haptics, sensors, self-ADB where legitimately available—and turns them into a small, durable capability namespace usable by humans and programs.

Conceptually:

```text
Android OS
    └─ Terminal bootstrap (AOT application process)
       ├─ Terminal native presentation
       │
       ├─ BOS semantic core
       │  ├─ direct capability
       │  │      └─ Android adapter
       │  │             └─ framework / Binder / Bionic / platform
       │  │
       │  └─ admitted execution
       │         └─ Remedy adapter ─────────────┐
       │                                       │
       └─ Remedy Generation Zero ◄─────────────┘
          native C++ executive
              └─ worker generation
                    └─ NativeAOT router / cargo
```

Terminal bootstrap initializes BOS and Remedy Generation Zero as cooperating authorities inside the application boundary. BOS owns semantic policy and admission. Generation Zero owns subordinate worker identity, containment, channels, cancellation, collapse and retirement. An ordinary capability such as torch or haptics executes through its owning Android adapter; it does not traverse Generation Zero.

The user-facing proposition is:

> **If Android can legitimately do it, BOS should make it discoverable, nameable, scriptable and composable.**

---

# 2. Core Invariants

These are architectural laws rather than implementation preferences.

### 2.1 Android remains the platform authority

BOS does not simulate being beneath Android.

Permissions, Binder identity, SELinux, package identity, Activity/Service lifecycle and OEM restrictions remain real.

BOS may normalize them; it may not lie about them.

---

### 2.2 Policy admits execution; Remedy enforces worker lifecycle

BOS does not become another process supervisor, and Remedy does not become a syscall trampoline for ordinary platform capabilities.

```text
BOS/platform policy decides:
    whether execution is admitted
    which declared rights are granted
    which executor adapter receives the launch

Remedy enforces:
    generation
    authority
    lifetime
    fencing
    retirement
```

BOS receives opaque handles/results. Generation Zero does not acquire application policy or runtime-specific vocabulary.

Generation Zero is part of the cold baseline, but remains idle for direct Android capability operations. Its presence does not make every BOS command a Remedy request.

Constitutional invariants:

> **Ordinary BOS capabilities execute through their owning platform adapter. Remedy participates only when execution, containment, revocation or subordinate worker lifecycle requires Remedy authority.**

> **BOS/platform policy admits execution; Remedy enforces generation, authority, lifetime, fencing and retirement.**

---

### 2.3 BOS semantics must not depend on executor implementation

This must remain true:

```text
Device.Torch.SetEnabled(true)
```

whether invoked from:

```text
BOS> TORCH ON
```

PowerShell:

```powershell
Set-DeviceTorch -Enabled $true
```

or a typed application API.

The implementations are peer projections over one semantic capability.

Never:

```text
BOS command
    ↓
construct PowerShell source text
    ↓
invoke pwsh
```

---

### 2.4 Capability crosses boundaries by possession

No ambient discovery of privileged machinery.

```text
bad:
worker scans for terminal-system.sock

good:
worker receives admitted capability endpoint
```

Remedy's inherited-authority principle applies to subordinate workers. BOS capability leases apply the same possession and stale-generation principles to capability use.

---

### 2.5 Control plane small; data plane direct

BOS brokers the authority to acquire a resource.

It should not become the hot path for the resource itself.

Example:

```text
BOS
  Acquire Camera Stream
          ↓
    opaque resource
          ↓
producer ───────────── consumer
       direct data path
```

Large media frames, GPU surfaces, audio and bulk file data should not bounce through BOS message serialization.

---

### 2.6 Human command syntax is not the canonical API

`BOS>` is a cockpit.

The typed capability model is canonical.

```text
TORCH ON
```

is merely the human projection of:

```text
Device.Torch.SetEnabled(true)
```

---

### 2.7 The cold baseline is AOT-compatible BOS

The BOS baseline is not a .NET Android application with PowerShell removed.

```text
BOS baseline
    = AOT-compatible semantic core
    + Android capability adapters
    + native Remedy Generation Zero executive
    + one active Terminal presentation root
```

On arm64, the production realization should use NativeAOT. ARM32 may temporarily use Mono while preserving identical BOS semantics; this is a runtime substitution, not a separate product or semantic fork.

BOS may:

```text
use explicit registration
use source generators and static lookup tables
use ordinary C# objects and GC-managed state
call narrow native shims
```

BOS must not:

```text
scan assemblies for commands or adapters
compile code at runtime
load arbitrary managed plugins
depend on PowerShell or Roslyn types
use dynamic proxy or reflection-driven DI machinery
```

PowerShell, Roslyn and other heavyweight runtimes may be bundled, but they remain dormant cargo until an admitted executor launches them in a separate worker generation.

---

### 2.8 Cleanup authority is scoped

Cleanup does not have one universal owner.

```text
Android OS
    owns Terminal application-process lifetime

BOS capability adapter + CapabilityLease
    own direct Android resources such as wake locks, camera streams and listeners

Remedy Generation Zero
    owns subordinate worker identity, containment, channels, fencing and retirement

NativeAOT router
    owns route-local PTY/ConPTY, child-process, pipe and bounded-buffer resources
```

Orderly worker shutdown is:

```text
BOS policy requests retirement
    ↓
Remedy requests quiescence
    ↓
router stops admission and releases route-local resources
    ↓
router acknowledges quiescence and exits
    ↓
Remedy observes death, closes the channel, fences tokens and retires the generation
```

If orderly cleanup fails, Remedy terminates the contained worker and the operating system provides the final process-memory and kernel-resource reclamation boundary. A v0 worker must not survive terminal closure of its inherited executive channel; platform containment must prevent orphan cargo if the Terminal/Generation Zero process dies.

Remedy does not clean up ordinary in-process Android capability objects, and BOS does not kill workers by PID or interpret worker implementation details.

---

# 3. BOS Vocabulary

The first version should have very few fundamental nouns.

```text
Capability
Service
App
Instance
Session
Surface
Resource
Command
```

Anything that does not clearly require another fundamental noun should be represented using these.

### Capability

A semantic operation exposed by the operating environment.

Examples:

```text
Device.Torch
Device.Haptics
Clipboard
Storage
Packages
Display
Power
Network
Surface
Process
```

### Service

A long-lived provider implementing one or more capabilities.

### App

Stable user-facing product identity.

### Instance

One launched occurrence of an App.

### Session

Continuing interactive state.

### Surface

An acquired presentable resource with dimensions, format/capabilities, synchronization and lifetime. Layout, navigation, panes, home screens and visual personality are not Surface semantics.

### Resource

An acquired opaque object such as a file descriptor, surface, shared buffer, camera stream or socket.

### Command

A human-facing invocation exposed through `BOS>`.

---

# 4. Identity Model

Do not collapse these identities:

```text
AppId
InstanceId
SessionId
SurfaceId
CapabilityLeaseId
Remedy WorkerGeneration
PID
fd
Binder handle
```

A typical relationship:

```text
App
 └─ Instance
     ├─ Session
     │   └─ Surface
     └─ Resource leases

Instance
   ↓ implemented by
Remedy WorkerGeneration
   ↓ physically represented by
PID/fd/etc.
```

Fundamental rule:

> **An app's identity must never depend on where it executes.**

---

# 5. Capability Model

Capabilities should be narrow semantic interfaces.

Prefer:

```text
Device.Torch.SetEnabled
Device.Haptics.Vibrate
Clipboard.ReadText
Clipboard.WriteText
Storage.OpenRead
Storage.OpenWrite
Packages.List
Packages.Launch
Display.KeepAwake
Surface.Acquire
```

Avoid:

```text
Android.CallBinder(...)
Android.DoThing(...)
System.ExecutePlatformCommand(...)
```

Raw Binder transaction numbers, Parcels, Java `Context`, Android `Intent` objects and platform-specific types stay below the BOS semantic boundary.

---

## 5.1 Capability descriptor

Conceptually:

```csharp
CapabilityDescriptor
{
    CapabilityId Id;
    Version Version;
    CapabilityFlags Flags;
}
```

Example IDs:

```text
device.torch
device.haptics
clipboard
storage
packages
display
surface
```

---

## 5.2 Capability request, grant and lease

A declaration is not authority. A caller does not necessarily receive global access merely because a manifest requests it.

```text
CapabilityRequest
    = desired capability + requested rights

CapabilityGrant
    = BOS/platform policy decision

CapabilityLease
    = opaque, generation-qualified evidence of that grant
```

Conceptually:

```text
Request(
    requester,
    capability,
    requestedRights
)
    →
policy admission
    →
CapabilityGrant
    →
CapabilityLease { opaque token, capability generation }
```

Possible rights:

```text
Read
Write
Invoke
Enumerate
Observe
AcquireResource
```

The precise policy engine can remain small initially, but the contract must fail closed and allow narrowing later. Callers cannot manufacture meaning from a lease's numeric representation.

---

# 6. Resource Leases

Anything costly, long-lived or revocable should be represented as a lease.

Example:

```text
Display.KeepAwake
    ↓
Lease #52

lease exists
    → display wake request remains active

lease retired
    → request disappears
```

Likewise:

```text
camera
microphone
GPU surface
wake lock
shared memory
network listener
```

This prevents global booleans from becoming invisible process state. Leases are opaque and generation-qualified: slot reuse plus a stale token must fail, just as stale Remedy generations fail.

---

# 7. The BOS Command Processor

The command processor should be intentionally primitive.

It is closer philosophically to `COMMAND.COM` than Bash or PowerShell.

Prompt:

```text
BOS>
```

Grammar:

```text
COMMAND [ARG ...] [/SWITCH[:VALUE] ...]
```

Minimum lexical features:

```text
quoted strings
%VARIABLE%
case-insensitive command names
command history
basic completion
```

Potential later additions:

```text
>
>>
<
|
&&
```

But none are required for the first usable BOS.

Do not implement a shell language before real usage demands one.

---

# 8. v0 Command Namespace and Roadmap

The v0 command set is limited to commands exercised by the first vertical receipt:

```text
VER
HELP
CLS
TORCH ON
TORCH OFF
VIBRATE 40
PWSH
EXIT
```

The following are roadmap candidates, not v0 promises:

```text
DIR / CD / TYPE
COPY / MOVE / DEL / DELTREE
SET / ECHO
APPS / START / STOP
PKG LIST / PKG INFO / PKG START
CLIP GET / CLIP SET
DISPLAY KEEPON / DISPLAY RELEASE
redirection / pipes / command composition
```

Storage, deletion, redirection and pipes require intentionally designed semantics. Their familiar names do not make those semantics free.

Some names intentionally resemble DOS.

That is UX, not compatibility emulation.

---

# 9. Internal vs External Commands

BOS should distinguish them cleanly.

### Internal

Small commands directly projected over BOS functionality:

```text
CLS
VER
HELP
TORCH
VIBRATE
```

Later capability-backed commands such as `PKG` and `START` use this same projection shape after their semantics enter scope.

Execution:

```text
parse
 ↓
resolve command
 ↓
invoke capability
 ↓
format result
```

### External

Programs/apps resolved from the app catalog:

```text
FFMPEG
PWSH
MYTOOL
```

Execution:

```text
resolve AppId and entrypoint
 ↓
admit requested capabilities
 ↓
dispatch through opaque ExecutorId adapter
 ↓
request Remedy worker generation when required
 ↓
bind session
```

The BOS command processor does not care whether `FFMPEG` is a Bionic executable, NativeAOT program or something else. The selected launch adapter owns that interpretation.

---

# 10. Application Catalog

BOS needs a tiny catalog of explicitly registered applications.

Conceptually:

```text
AppManifest
{
    AppId
    DisplayName

    Origin
    Digest
    SigningIdentity
    InstallDisposition

    Entrypoints[]
    RequestedCapabilities[]
}
```

Possible entrypoint:

```text
Entrypoint
{
    Name
    ExecutorId
    ExecutorTargetMetadata
}
```

Example opaque executor identifiers:

```text
android.bionic
android.component
terminal.router
dotnet.coreclr
dotnet.nativeaot
```

BOS Core does not enumerate or switch over every runtime type. Executor identifiers are stable and opaque; launch adapters own their meaning.

Do not infer applications by scanning executable files.

An executable can exist without becoming an App.

---

# 11. Execution Adapters

BOS understands that an admitted entrypoint can be launched through an executor identifier. It does not understand the implementation internals of each runtime.

Possible adapters may support:

```text
Native Bionic executable
NativeAOT executable
Managed shared CoreCLR app
Managed isolated CoreCLR app
Android Activity/Service component
Terminal PTY session
future compatibility cargo
```

BOS asks for an admitted app launch. The adapter identified by the opaque `ExecutorId` interprets executor-specific target metadata and translates the request into platform mechanics. Adapters that create contained subordinate workers use Remedy; adapters for ordinary platform capabilities do not.

---

# 12. PowerShell

PowerShell is an optional high-level environment.

It should feel like:

```text
BOS> PWSH
PowerShell 7.x
PS Home:\>
```

rather than:

```text
BOS == PowerShell
```

PowerShell is ideal for:

```text
composition
pipelines
automation
structured objects
complex scripting
administration
```

BOS is ideal for:

```text
instant device control
bootstrapping
recovery
basic navigation
app launch
simple inspection
```

BOS must remain fully usable if CoreCLR and PowerShell are absent.

---

# 13. Terminal Relationship

Terminal owns presentation and multiplexing.

BOS owns operating semantics.

```text
Terminal
    pane
    focus
    split
    surface placement
    input routing
    terminal rendering
        ↓
BOS
    command meaning
    app resolution
    capabilities
       /                 \
      ↓                   ↓
platform adapters     Remedy adapter
      ↓                   ↓
Android services      worker lifecycle
```

Terminal `SessionId` is never a Remedy generation.

---

# 14. Surface Relationship

BOS should understand a Surface only as an acquired presentable resource.

```text
SurfaceId
Size
Format
Capabilities
Synchronization
ResourceLease
```

The actual resource may be:

```text
Android Surface
HardwareBuffer
Vulkan image
GPU shared buffer
software bitmap
terminal cell surface
```

The same producer should potentially be projectable into:

```text
Terminal pane
full-screen view
thumbnail
wallpaper
PiP
assistant UI
```

without the producer learning those destinations.

---

# 15. Client Presentation

Home, orb, pane, navigation and layout are Terminal concerns. They are not BOS Surface semantics and do not belong in the BOS contract.

The factory cold-start experience is the BOS command surface itself:

```text
BOS>
```

No dashboard, automatic PowerShell launch or second shell stands between application start and this prompt. A user may deliberately customize `AUTOEXEC.BAT` later; the shipped default remains BOS.

## 15.1 One active presentation root

BOS home, prompt, pinger, selection, command surfaces and basic shell chrome project through one active Terminal presentation root. They must not casually create overlay windows or additional `ViewRoot` instances with independent buffer chains.

```text
one Android Activity presentation boundary
    ↓
one active native root
    ├─ BOS semantic presenter
    └─ VT terminal presenter
```

Settings or another full-screen Android presentation may replace or pause the Terminal root when semantically appropriate. It must not remain stacked behind it merely for implementation convenience.

BOS may expose a small operational-state vocabulary if real use requires it:

```text
Ready
Busy
Degraded
Fault
```

Terminal decides whether those states appear as an orb, text, color, notification or nothing at all. Another BOS client must not need to understand Terminal's presentation choice.

## 15.2 Typed invocation outcomes

Internal versus external command classification does not determine presentation. The typed outcome of an invocation does.

> **InvocationOutcome describes presentation semantics, not execution mechanism.**

Conceptually:

```csharp
public abstract record InvocationOutcome;

public sealed record InlineResult(
    Result Value
) : InvocationOutcome;

public sealed record InteractiveTerminal(
    RouteId Route
) : InvocationOutcome;

public sealed record AndroidHandoff(
    PlatformComponentRef Component
) : InvocationOutcome;

public sealed record PresentableSurface(
    SurfaceLease Lease
) : InvocationOutcome;

public sealed record BackgroundJob(
    JobId Job
) : InvocationOutcome;
```

`PlatformComponentRef` is an opaque semantic reference resolved by the Android adapter. It is not an `Intent`, `Context`, `Activity`, Binder object or other framework type exposed through BOS Core.

```text
InvocationOutcome
    ├─ InlineResult
    ├─ InteractiveTerminal
    ├─ AndroidHandoff
    ├─ Surface
    └─ BackgroundJob
```

Terminal interprets those outcomes:

```text
InlineResult
    → remain at BOS>

InteractiveTerminal(RouteId)
    → attach a TerminalEngine and switch the same root to VT presentation

AndroidHandoff
    → yield to the admitted Android component

Surface
    → present the acquired Surface according to Terminal policy

BackgroundJob
    → remain at BOS> and report the admitted job identity
```

Execution remains mechanically simple:

```text
parse
  ↓
resolve command or app
  ↓
admit capability or execution
  ↓
invoke
  ↓
return InvocationOutcome
```

Examples:

```text
VER / TORCH / VIBRATE
    → InlineResult

PWSH / BASH / EDIT / TOP
    → InteractiveTerminal

START SETTINGS
    → AndroidHandoff

START SERVER
    → BackgroundJob
```

An external one-shot utility may return an inline result. An internal command may cause an Android handoff. Terminal must not switch to VT merely because a command is external, inspect process names or infer a TUI by examining output bytes.

A NativeAOT app may return `InlineResult`; managed cargo may return `PresentableSurface`; a Bionic executable may return `BackgroundJob`; PowerShell may return `InteractiveTerminal`. Executor identity and presentation outcome remain orthogonal.

## 15.3 VT presentation transition

When an admitted executor returns an interactive PTY/ConPTY route:

```text
BOS semantic state remains parked
    ↓
Terminal attaches the route to its own TerminalEngine
    ↓
the existing native root switches to VT presentation
    ↓
route closes
    ↓
Terminal restores the same BOS semantic state
    ↓
BOS>
```

Once a PowerShell or Bash route is active, a nested TUI such as Edit remains inside that same VT session. Alternate-screen, mouse-reporting, bracketed-paste, application-cursor and focus modes change terminal behavior; they do not create another Android window or presentation stack.

---

# 16. Input Semantics

For BOS's own command surface, Terminal does not need to pretend it is a VT terminal.

Keep semantic editing state:

```text
PromptSpan   immutable
InputSpan    editable
Caret
Selection
History
Completion
```

Therefore:

```text
BOS> TORCH ON
     ^ editable region begins here
```

Backspace cannot vandalize the prompt because the prompt is not terminal history.

Hosted cargo such as:

```text
pwsh
vim
ssh
edit
```

continues to receive proper PTY/VT behavior.

Touch remains Terminal-owned and follows negotiated terminal state.

```text
no application mouse mode:
    swipe       → scrollback
    long press  → selection
    tap pinger  → text input
    pinch       → font size

application mouse mode active:
    tap         → SGR mouse press/release
    drag        → SGR mouse motion
    two fingers → wheel events
```

Terminal retains an explicit escape gesture that hosted cargo cannot capture. Mouse mode, alternate screen and application cursor state are VT protocol state inside the current route, not new Android presentation modes.

---

# 17. Android Platform Basement

Android implementation belongs behind adapters.

Examples:

```text
Device.Torch
    ↓
Android CameraManager adapter

Device.Haptics
    ↓
VibratorManager adapter

Clipboard
    ↓
ClipboardManager adapter

Packages
    ↓
PackageManager adapter

Surface
    ↓
Surface / HardwareBuffer adapter
```

Upstairs code must not receive:

```text
Activity
Context
Intent
View
IBinder
Parcel
Java.Lang.Object
```

as public BOS semantic types.

---

# 18. Binder

Binder is an implementation substrate, not the public BOS API.

Correct:

```text
BOS semantic request
     ↓
Android adapter
     ↓
Binder/framework call
```

Wrong:

```text
BOS application
     ↓
transaction id 37
     ↓
Parcel blob
```

There may eventually be advanced APIs exposing lower-level Android mechanisms, but they should be explicitly low-level capabilities rather than the default BOS contract.

---

# 19. Self-ADB

Self-ADB is a distinct authority-bearing capability because it can cross into a different Android authority domain.

Do not blur:

```text
Terminal app UID
```

with:

```text
ADB shell UID
```

BOS may expose something like:

```text
Shell.Acquire
```

or:

```text
ADB EXEC ...
```

later.

It must clearly represent that the authority changed.

Generalized ADB remains outside BOS v1.

---

# 20. State and Persistence

BOS itself should own very little persistent mutable state.

Persistent candidates:

```text
user preferences
registered app manifests
capability configuration
command history
```

Terminal owns presentation and surface preferences.

Not:

```text
live worker objects
PID mappings
raw Binder handles
Remedy generations
open descriptors
```

Those are runtime state.

After process restart, BOS should reconstruct semantic state rather than deserialize OS objects.

---

# 21. Error Model

Errors need to remain machine-readable even when displayed like DOS.

Typed result:

```text
Result
{
    Code
    Domain
    Message
    Detail
}
```

Human projection:

```text
BOS> TORCH ON
Access denied.
```

or:

```text
BOS> START CAMERA
Capability unavailable.
```

PowerShell/API projection receives the structured error instead.

Do not make callers parse human strings.

---

# 22. Command Discovery

`HELP` should derive from the same command registry used for dispatch.

```text
BOS> HELP TORCH

TORCH

Controls the device torch.

TORCH ON
TORCH OFF
TORCH STATUS
```

Likewise:

```text
BOS> HELP
```

should enumerate current admitted commands, including dynamically contributed ones.

---

# 23. Extensibility

Subsystem eventually grows as detachable capability moss.

A module may contribute:

```text
Capabilities
Commands
Apps
Services
```

Client-specific Surface recipes and Settings contributions belong to the client or package integration seam that presents them, not to BOS Core.

A module must not mutate BOS's fundamental ontology.

Example:

```text
FFmpeg package
    ├─ App: ffmpeg
    ├─ Command: FFMPEG
    └─ optional media capabilities
```

not:

```text
FFmpeg modifies BOS parser,
session manager,
surface compositor
and lifecycle executive
```

Dependencies point toward BOS contracts.

---

# 24. Versioning

Every machine-facing BOS capability should have a stable identity and version.

Prefer:

```text
device.torch/1
clipboard/1
surface/1
packages/1
```

A capability revision should mean semantic contract evolution, not platform implementation revision.

Android 16 changing an internal Binder implementation must not change:

```text
device.torch/1
```

if BOS semantics remain unchanged.

---

# 25. Security Model

BOS should follow explicit authority.

A worker sees only capabilities admitted to it.

Example:

```text
ffmpeg
    receives:
        storage.read:/video
        storage.write:/output
        media.decode

    does not receive:
        contacts
        microphone
        package.install
```

The first implementation does not need a giant ACL framework. It does need explicit admission and enforceable endpoints for every authority it claims to narrow. Until those exist for external cargo, narrowing is a design requirement rather than a security claim.

The API must avoid assuming universal ambient access so enforcement can be added without redesign.

---

# 26. What BOS Must Not Become

BOS v1 is explicitly **not**:

```text
a Linux distro
a Unix compatibility environment
a PowerShell wrapper
an Android framework replacement
a Binder RPC playground
a package-manager-first distro
a process supervisor
a persistence engine
a giant service locator
a game engine
a UI widget framework
a monolithic "Subsystem"
```

It is the stable semantic trunk.

---

# 27. Recommended Source Shape

Something approximately this small:

```text
BOS/
├─ Core/
│  ├─ Capability.cs
│  ├─ CapabilityId.cs
│  ├─ CapabilityLease.cs
│  ├─ AppManifest.cs
│  ├─ AppId.cs
│  ├─ SessionId.cs
│  ├─ SurfaceId.cs
│  ├─ InvocationOutcome.cs
│  └─ Result.cs
│
├─ Commands/
│  ├─ CommandParser.cs
│  ├─ CommandRegistry.cs
│  └─ Builtins/
│
├─ Apps/
│  ├─ AppCatalog.cs
│  └─ AppLauncher.cs
│
├─ Services/
│  └─ CapabilityRegistry.cs
│
├─ Admission/
│  ├─ CapabilityRequest.cs
│  ├─ CapabilityGrant.cs
│  └─ CapabilityPolicy.cs
│
├─ Executors/
│  ├─ ExecutorId.cs
│  └─ ExecutorRegistry.cs
│
├─ Platform/
│  └─ Android/
│     ├─ TorchAdapter.cs
│     ├─ HapticsAdapter.cs
│     └─ SurfaceAdapter.cs
│
└─ Integration/
   └─ RemedyAdapter.cs
```

Do not create all of these merely because they appear in the diagram. The shape matters more than matching directories.

---

# 28. BOS v0 Receipt

I would call BOS real when this exact scenario works on the phone:

```text
BOS> VER
BOS 0.x

BOS> VIBRATE 40
device vibrates

BOS> TORCH ON
torch activates

BOS> TORCH OFF
torch deactivates

BOS> PWSH
policy admits the declared launch and capabilities
the selected executor adapter ensures an admitted Remedy router generation exists
Terminal mux binds new session

PowerShell 7.x
PS Home:\> exit
session retires
the now-idle router quiesces and its worker generation retires

BOS>
```

Then prove:

```text
no PowerShell required for BOS commands
no raw Android types above platform adapter
torch and haptics never touch Remedy
PWSH does use Remedy
no leaked worker generations
no leaked fds/resources
stale leases rejected
reused capability slots do not resurrect stale leases
capabilities fail closed
Terminal SessionId remains independent of Remedy generation
BOS survives pwsh death
```

That is enough to establish the architecture.

## 28.1 BOS idle and presentation receipt

The first silicon receipt runs on the physical CPH2451 and proves the cold baseline independently of PowerShell.

Required state:

```text
application cold-launched
BOS> visible
one active presentation root
Remedy Generation Zero initialized and idle
zero subordinate worker generations
no PowerShell worker
no CoreCLR worker
no Roslyn
no secondary window or overlay ViewRoot
pinger drawn in the same surface
```

Measure after 30 seconds, 60 seconds and 5 minutes idle:

```text
total PSS and RSS
graphics PSS
Java heap
native heap
NativeAOT GC heap
thread count
fd count
CPU utilization
```

Initial PSS production bands:

```text
excellent     40–55 MB
target        50–70 MB
acceptable    70–85 MB
investigate   >85 MB
```

Presentation/lifecycle assertions:

```text
BOS> VER
    → InlineResult
    → remain in semantic BOS presentation

BOS> VIBRATE 40
    → direct Android adapter
    → no Remedy lifecycle operation

BOS> TORCH ON
    → direct Android adapter
    → no Remedy lifecycle operation

BOS> PWSH
    → InteractiveTerminal(RouteId)
    → admitted Remedy router generation created if none is active
    → same native root switches to VT presentation

PS Home:\> exit
    → route closes
    → because this is the final route, router explicitly quiesces
    → worker generation retires
    → same root restores the parked BOS state
    → memory returns near the measured BOS baseline

BOS>
```

This receipt must assert that PowerShell/CoreCLR and Roslyn are not resident before `PWSH`, that Terminal never infers presentation from executable identity, and that no second presentation root appears during the transition. A multiplexer receipt must additionally prove that closing one route does not retire a router generation while another admitted route remains active.

---

# 29. First Implementation Slice

I would build BOS in this order:

```text
NativeAOT Android BOS probe
    ↓
one active Terminal presentation root
    ↓
Command parser
    ↓
Command registry
    ↓
VER / HELP / CLS
    ↓
Capability registry
    ↓
Haptics
    ↓
Torch
    ↓
app catalog + provenance
    ↓
capability admission
    ↓
opaque executor dispatch
    ↓
Remedy-backed worker launch
    ↓
PWSH
```

Do **not** start with storage, scripting syntax, generalized Binder reflection or package management. Torch and vibration are nearly perfect BOS receipts because they prove the central idea with almost no semantic ambiguity:

```text
BOS command
      ↓
stable capability
      ↓
Android implementation
```

---

# 30. One-Sentence Architecture

If you need the design reduced to the line everyone works from:

> **BOS is a tiny semantic operating environment that turns Android's legitimate capabilities into stable, possession-based services and commands while delegating subordinate worker lifecycle to Remedy and presentation to Terminal.**

And the companion implementation rule:

> **If BOS has to know whether a capability is implemented by Binder, JNI, Bionic, CoreCLR, NativeAOT, ADB or a GPU surface above the platform adapter, the boundary is wrong.**
