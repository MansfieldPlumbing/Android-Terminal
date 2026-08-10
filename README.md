# Terminal

Native, container-free PowerShell for Android.

Terminal hosts PowerShell directly inside an Android application on CoreCLR. It is not a Linux distribution, compatibility container, WebView terminal, or `proot` environment. PowerShell objects remain PowerShell objects, while Android capabilities are exposed through narrow native cmdlets and deliberate permission boundaries.

> **Release status:** Terminal is preparing for its first public release. The current tree is the canonical development baseline; earlier implementations and repository branches are deprecated.

## What works today

- Persistent in-process PowerShell 7 runspace on Android ARM64
- Native Android Canvas cell presenter with ANSI colors
- Touch scrollback and pinch-to-zoom
- Windows-shaped prompt, paths, `dir`, `cd`, `cd..`, `cd\`, and guarded `deltree`
- Native settings surface, launcher shortcut, themes, font size, and scrollback controls
- App-private `profile.ps1` and script-backed `settings.ps1`
- Foreground-service ownership with a durable Terminal notification and confirmed shutdown
- Self-ADB pairing and loopback on supported devices, including notification PIN entry
- Android commands for vibration, flashlight, networking, permissions, and shell authority
- Roslyn compiler services with conservative source analyzers
- Offline native command discovery and help
- Experimental Surface Contract 1: typed XML structure, native Android Views, multiline text state, and PowerShell behavior
- Additive post-build hardpoints with deterministic hashes and signed XML release receipts
- Additive Edit hardpoint with native multiline input, cursor/dirty state, and open/save behavior

Run `cmds` inside Terminal to see the native command surface.

## Design

Terminal keeps five boundaries deliberately small:

1. The **PowerShell host** owns the runspace, commands, objects, profile, and script configuration.
2. The **cell presenter** owns native drawing, ANSI color, scrollback, zoom, and terminal input.
3. The **Android capability boundary** owns permissions, intents, notifications, lifecycle, and device services.
4. **Optional transports**, including self-ADB and future remoting, expose explicit authority without silently changing ordinary sandbox behavior.
5. **Surface hardpoints** declare typed native UI and attach PowerShell behavior without becoming compile-time dependencies of the base APK.

Android remains the security boundary. Terminal does not treat every manifest capability as consent, and elevated features must be visible, optional, and revocable.

## Configuration

Terminal creates an app-private PowerShell home on first launch:

- `Home:\profile.ps1` customizes the PowerShell session.
- `Home:\settings.ps1` is shared by the native Settings UI and PowerShell.
- `Home:\.System` contains Terminal-managed scripts.

**Restore visual defaults** replaces only `settings.ps1`. It preserves the profile, scripts, files, and self-ADB identity. Android's **App info** page is the deliberate full-reset path.

Roslyn analysis is enabled by default. `Test-TerminalSource <path>` produces a structured audit receipt; `Assert-TerminalSource <path>` enforces the conservative compilation gate. The owner may disable enforcement in **Settings > Configuration > Roslyn analyzers**, but findings remain visible and no Android or remote authority is granted.

## Build

Requirements:

- .NET 11 SDK with the Android workload
- Android SDK/API 36
- A compatible Java SDK
- An Android ARM64 device or emulator

From PowerShell:

```powershell
./scripts/build.ps1
```

The script's workstation defaults point at `S:\bin`; every tool path can be overridden with parameters.

By default it builds one clean base, hydrates `releases/dev.xml`, runs `zipalign`, signs and verifies the result, and emits an external XML receipt under `build/releases/dev`. Use `-SkipHydration` for the clean base only. See `docs/RELEASES.md` for the manufacturing contract.

The product project is `src/Terminal.Android/Terminal.Android.csproj`.

## Repository map

- `src/Terminal.Android` — the only shipping application project
- `docs` — architecture and workspace boundaries
- `scripts` — repeatable developer commands
- `templates` — canonical user-script defaults
- `hardpoints` — optional Surface XML, PowerShell behavior, and origin-scoped assets
- `releases` — additive cargo and signing-profile recipes
- `components/TUI-DWM` — future spatial cell-surface candidate; not included in the APK
- `components/ShaderUI` — future composited-renderer candidate; not included in the APK
- `components/Remedy` — runtime/session-lifetime research; not a Terminal dependency
- `reference/android-terminal-original` — deprecated implementation preserved for archaeology

Code is promoted from `components` or `reference` only through an explicit, reviewed contract. Presence in this repository does not mean a component ships.

## Current limits

- Android ARM64 is the current release target.
- The presenter implements the practical console subset Terminal needs today, not every historical VT behavior.
- Self-ADB depends on Android wireless-debugging support and explicit device-owner pairing.
- Surface Contract 1 is experimental and intentionally small; see `docs/SURFACE-CONTRACT-1.xml` and its device proof.
- PSRP hosting, the spatial TUI multiplexer, ShaderUI composition, and syntax-aware editor machinery remain future work.

## Project

[github.com/mansfieldplumbing/terminal](https://github.com/mansfieldplumbing/terminal)

In loving memory of Billie Dean Mansfield.
