# Platform boundary contract

Terminal is conservative where code acquires authority and permissive where code only computes or renders.

> Cross an operating-system, process, permission, lifecycle, package, or native ABI boundary only through its documented front door. Once Terminal owns a safe local abstraction, its implementation may be as unusual as the product requires.

This is why an experimental cell compositor or shader pipeline is welcome while pointer reach-through into Android, the launcher, Binder internals, or a foreign process is not.

## Trust zones

### Semantic core

`Terminal.Engine` owns terminal meaning. It may use CoreCLR and the base class library. It must not reference Android, Java, JNI, Vulkan, native handles, Activities, Views, Intents, Binder, or package state.

Inputs and outputs are ordinary managed values, immutable records where practical, and explicit semantic operations.

### PowerShell and hardpoints

PowerShell scripts, dynamically compiled C#, and hydrated hardpoints are guests. They may ask Terminal to perform declared semantic operations and receive serializable results. They do not receive an `Activity`, `Context`, `View`, `Intent`, `IBinder`, `Surface`, JNI object, native handle, or arbitrary platform service.

Authority is represented by a narrow Terminal-owned command or capability object. Possession of a runspace is not possession of Android.

### Android capability basement

The Android product project owns the adapters for permissions, Activities, Services, notifications, clipboard, package shortcuts, Storage Access Framework, Binder, and other platform facilities.

Adapters use public documented Android contracts:

- manifest-declared components and intent filters;
- explicit `Intent` routing;
- `ShortcutManager`, `NotificationManager`, and the relevant system manager;
- lifecycle callbacks supplied by the framework;
- bound services and `IBinder` or AIDL only when a process boundary genuinely exists;
- platform permission and user-consent flows.

Components are unexported by default. Exported components are explicit, narrowly filtered, validate all input, and expose semantic requests rather than executable text.

### Renderer basement

Canvas, TUI-DWM-derived composition, ShaderUI, and a future Vulkan renderer may be unconventional internally. They still enter Android through an app-owned `View`, `SurfaceView`, `SurfaceHolder`, or another documented surface contract.

Vulkan's ABI necessarily uses opaque native handles. Those handles may exist only inside a reviewed renderer adapter. They must not enter `Terminal.Engine`, PowerShell, Surface XML, hardpoints, command objects, persisted state, or general application plumbing.

Rendering code owns pixels, timing, and GPU resources, not Android authority. It stops producing frames when its surface is absent and degrades to the native Canvas presenter when initialization or device recovery fails.

### Transport basement

Self-ADB, PSRP, SSH, USB AOA, and future transports expose explicit, named authority. Transport bytes and protocol state stay behind transport interfaces. Callers exchange PowerShell objects or Terminal command envelopes, not shell strings merely because the remote endpoint happens to support a shell.

Transport authority never silently becomes ordinary app authority.

## Registration, not reach-through

Terminal conducts Android business by registration and message passing:

- launcher and app-drawer entries: manifest `activity` or `activity-alias` declarations;
- long-press app actions and pinned entries: `ShortcutManager`;
- Settings: an explicit Settings Activity destination;
- durable session presence: a foreground Service plus its notification;
- widgets: `AppWidgetProvider`;
- Quick Settings: `TileService`;
- cross-process service APIs: bound Service and Binder or AIDL;
- rendering: an app-owned Android Surface handed to the renderer.

Terminal does not inject code, inspect or mutate another process, discover private symbols, fabricate framework objects, retain stale lifecycle objects, or pass pointers across a semantic boundary.

## The weirdness envelope

Code is free to be novel when all of the following remain true:

1. Its inputs were acquired through an owned or documented contract.
2. Its outputs return through that contract.
3. It cannot acquire additional authority by accident.
4. Its lifetime has a named owner and deterministic release path.
5. Failure can be contained or replaced by a safe fallback.
6. No native handle or platform object escapes its basement adapter.

This permits unusual renderers, parsers, schedulers, spatial interfaces, and object projections. It does not permit an implementation shortcut to redefine a security or lifecycle boundary.

## Enforcement

No single analyzer can prove this contract. Terminal implements it in layers and
records the state of each layer honestly:

| Layer | Current enforcement | Status |
|---|---|---|
| Dynamically compiled C# | `Test-TerminalSource` and `Assert-TerminalSource` reject direct Android or Java imports, JNI, native imports and loading, unsafe code, native pointer and handle types, raw process launch, and undeclared listeners. | Implemented |
| Semantic core | The `Terminal.Engine` project has no Android dependency; build and architecture tests keep it that way. | Implemented |
| Product source | Project boundaries and architecture checks currently keep `Terminal.Engine` platform-free and centralize Surface palette tokens. A dedicated build analyzer for native-handle and basement-path confinement remains required. | Partial |
| Android package | The current manufacturer verifies additive hydration, alignment and signing. A release-profile manifest allowlist for exported components, permissions, aliases, services and providers remains required before a Play release claims this enforcement. | Partial |
| Hardpoints | The release hydrator admits declared assets only and proves that compiled APK payload bytes were not changed. XSD validation and the strict runtime parser constrain document shape. | Implemented |
| Runtime | BOS capability leases will validate origin, arguments, lifecycle owner, permission state and cancellation before crossing the platform boundary. BOS v0 is not yet implemented. | Planned |

An override may permit owner experimentation, but it remains visible in the receipt. An override does not make guest code trusted and does not silently grant Android, network, filesystem, or remote authority.

## Review questions

Every new boundary-crossing feature must answer:

1. What is the official Android, .NET, protocol, or native entry point?
2. Which basement adapter owns it?
3. What semantic operation is exposed upstairs?
4. What authority does that operation grant, and how is consent obtained?
5. Who owns its lifetime, cancellation, and cleanup?
6. What crosses the boundary: objects, serialized values, bytes, or an opaque handle?
7. How does it fail without taking the runspace or terminal state with it?
8. Which analyzer, architecture test, manifest check, or release receipt prevents regression?

If those answers are absent, the feature is not ready to cross the boundary.
