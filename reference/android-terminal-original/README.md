# Terminal
**A Micro-Frontend Operating Environment for Android, powered by .NET 11 and PowerShell 7.6.1.**

> [!NOTE]
> **Subsystem is the real deal.** While I will continue to polish this Terminal project, the majority of my efforts are now focused on the experimental WebGPU/SDF compositor track known as **[Subsystem](https://github.com/mansfieldplumbing/Subsystem)**. It's launching in a separate repository with no official release yet, but it builds upon the native backend foundations established here.

! This is a high velocity project so please be patient!

> *A self-contained Android APK that hosts the raw Microsoft PowerShell SDK as a live, interactive background engine — no Linux layer, no chroot, no proot, no Termux dependency. Just PowerShell, running natively inside an Android process.*

---

<img width="2340" height="1080" alt="Screen_Recording_20260515_152525~2" src="https://github.com/user-attachments/assets/2e9e9874-0d59-4ac7-85be-872d104fc68b" />


---

## What This Is

Terminal is an experiment in what Android can actually do when you stop asking permission from its intended application model. It embeds the full `Microsoft.PowerShell.SDK` NuGet package directly into an ARM64 APK, boots a persistent PowerShell 7.6.1 Runspace in a background thread, and surfaces it through a React/Vite web frontend hosted inside an Android WebView — communicating bidirectionally via a Base64 JSON bridge.

The result is a real, interactive PowerShell terminal on your Android device. Not a remote session into another machine. Not a shell emulator. The engine is running in-process, on the device, in your pocket.

---

## Architecture

```
┌─────────────────────────────────────────────────┐
│                  Android APK                    │
│                                                 │
│  ┌──────────────────┐   ┌─────────────────────┐ │
│  │  React/Vite UI   │   │  .NET 11 / C# Host  │ │
│  │  (WebView)       │◄──┤                     │ │
│  │                  │   │  PowerShell 7.6.1   │ │
│  │  Terminal Shell  │──►│  Runspace           │ │
│  │  Applet iFrames  │   │  (Background Thread)│ │
│  └──────────────────┘   └─────────────────────┘ │
│         postMessage / AndroidBridge (Base64)     │
└─────────────────────────────────────────────────┘
```

**Backend:** A native .NET 11 Android APK (`net11.0-android`) hosting a persistent PowerShell 7.6.1 `Runspace` on a background thread. The `Activity` acts as the PS host.

**Frontend:** A React/Vite SPA served from `src/runspace/Assets/wwwroot/` and rendered in an Android `WebView` (or a `WebView2` container on Windows). All UI is web-native.

**IPC Bridge:** Two-way communication via `window.AndroidBridge` on Android, or `window.chrome.webview.postMessage` on Windows. A queuing handshake ensures no output is dropped during engine boot.

**Dual-Track Host:** While the primary focus is the Android APK, this repository also includes a fully functional Windows Desktop host (`WinForms-Terminal`). It uses `ConPTY` to spawn host shells (PowerShell, WSL, cmd) behind the exact same shared React frontend!

---

## The Engineering Problems Solved

Getting PowerShell to run inside an Android APK is not straightforward. The SDK was designed for Linux and Windows. Android is Linux-adjacent, but it is not Linux. Three specific failures had to be solved before a single cmdlet could execute.

### 1. The Bionic Libc Intercept

PowerShell's native layer (`libpsl-native.so`) calls `openlog` / `syslog` for telemetry on startup. On a real Linux system, these are provided by glibc. On Android, Bionic implements a different syslog ABI — and PowerShell's precompiled `.so` is linked against glibc conventions. The result at runtime is a fatal `TypeInitializationException` inside `PSSysLogProvider` before any user code runs.

The fix is a two-part approach:

- **`psl_dummy.c`** — A minimal C file that exports all syslog-adjacent symbols PowerShell tries to call (`Native_OpenLog`, `Native_CloseLog`, `Native_LogSysLog`, `Native_SysLog`, and the legacy `SysLogProvider_*` variants) as no-op stub functions. This is compiled as `libpsl-android.so`, aligned to Android 16's strict 16KB memory page boundary requirement.

- **`NativeLibrary.SetDllImportResolver`** — Registered against the PowerShell assembly at boot, this intercepts any P/Invoke request for `libpsl-native` and redirects it to our stub library before the type initializer ever touches the real one.

```csharp
NativeLibrary.SetDllImportResolver(
    typeof(System.Management.Automation.PowerShell).Assembly,
    (libraryName, assembly, searchPath) =>
    {
        if (libraryName.Contains("libpsl-native"))
            return NativeLibrary.Load("libpsl-android.so", assembly, searchPath);
        return IntPtr.Zero;
    });
```

### 2. The IL Trimmer / Assembly Reflection Bypass

The .NET Android build pipeline's IL Trimmer aggressively eliminates types it cannot statically trace. PowerShell's `InitialSessionState.CreateDefault()` discovers and loads cmdlets at runtime via reflection — an access pattern the trimmer cannot follow. Calling it produces `applicationBase` location crashes and missing-provider exceptions.

The solution is to disable the trimmer entirely (`<PublishTrimmed>false</PublishTrimmed>`) and hand-load the cmdlet assemblies into an empty `InitialSessionState`:

```csharp
var iss = InitialSessionState.Create();
iss.LanguageMode = PSLanguageMode.FullLanguage;

LoadFromAssembly(iss, typeof(PSObject).Assembly);
LoadFromAssembly(iss, Assembly.Load("Microsoft.PowerShell.Commands.Utility"));
LoadFromAssembly(iss, Assembly.Load("Microsoft.PowerShell.Commands.Management"));
```

`LoadFromAssembly` reflects over each type, finds `[Cmdlet]` and `[CmdletProvider]` attributes, and registers them explicitly. The engine knows exactly what it has. No guessing, no trimmer casualties.

### 3. The React Boot Handshake

The PowerShell engine boots immediately in a background thread — before the WebView has finished loading and before React has mounted. Any output emitted during that window would be sent to a JavaScript callback that doesn't exist yet and silently dropped.

A simple queue solves this: all `SendToReact` calls are enqueued if `_isReactReady` is false. When React mounts, it calls `AndroidBridge.notifyReady()`, which flips the flag and drains the queue in order. The boot banner and any startup output always arrive intact.

---

## Build Instructions

```bash
# 1. Prerequisites
#    .NET 11 SDK with Android workload
dotnet workload install android

# 2. Build the React frontend
cd src/terminal && npm install && npm run build

# 3. Copy the built frontend into the Android asset tree
cp -r src/terminal/dist/* src/runspace/Assets/wwwroot/

# 4. Publish the APK
cd src/runspace
dotnet build -c Release -t:Run -f net11.0-android
```

The `libpsl-android.so` stub is pre-compiled and checked in under `libs/arm64-v8a/`. To recompile it from source:

```bash
# Requires Android NDK
$NDK/toolchains/llvm/prebuilt/linux-x86_64/bin/aarch64-linux-android24-clang \
    -shared -fPIC -o libs/arm64-v8a/libpsl-android.so psl_dummy.c \
    -Wl,-z,max-page-size=16384
```

---

## Applet System

Tools and utilities are single-file HTML applications dropped into `public/applets/`. They communicate with the terminal shell via `postMessage`, and the shell proxies their PowerShell requests through the C# bridge.

An applet can:
- Request PowerShell command execution and receive output
- Render its own full UI inside an iframe sandbox
- Be added, updated, or replaced without touching any native Android code

Current applets: `dev-explorer.html`, `media-stitcher.html`, `text.html`

Planned applet formats:
- **`.html`** — Standalone web UI (current)
- **`CSIXML + .ps1`** — Declarative UI definition paired with a PowerShell script backend, rendered by the shell into a structured applet frame
- **Remote applets** — Fetched over PSRP from a connected host

---

## Roadmap

### PSRP (PowerShell Remoting Protocol)
The engine runs inside a standard Runspace, which means PSRP is within reach. The goal is to allow the device to act as either a PSRP client (connecting to a remote host and running commands there) or a PSRP server (exposing its local Runspace over WSMan or SSH transport to remote callers). This unlocks distributed scripting scenarios — using the phone as a management node, a remote sensor endpoint, or a mobile automation runner.

### Microserver Support
PowerShell can spin up HTTP listeners natively. The intent is to support applets and scripts that bind to localhost ports and serve HTTP — enabling the device to act as a local API endpoint, a webhook receiver, or a lightweight web server for LAN tools. The notification system and persistent service are prerequisites for this.

### CSIXML + PS1 Applets
A lightweight declarative format pairing a UI layout (CSIXML) with a PowerShell script backend (`.ps1`). The terminal shell parses the layout, renders it as an applet frame, and wires UI events to script invocations. This allows applets to be written entirely in PowerShell without touching HTML or JavaScript.

---

## Project Status

The core engine, IPC bridge, and applet system are working on physical ARM64 Android hardware. PowerShell 7.6.1 initializes, executes commands, returns output, and maintains session state across invocations.

Contributions, issues, and ideas welcome.