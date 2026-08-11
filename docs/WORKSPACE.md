# Workspace map and integration truth

## Builds today

`src/Terminal.Android` is the only product project. It contains the native Android Canvas console, persistent in-process PowerShell runspace, script-backed configuration, OOBE, Android capability bridge, session notification, locally vendored self-ADB protocol implementation, and the stable Surface/hardpoint mechanism.

`hardpoints` contains optional release cargo and `releases` selects cargo for a compiled base. Neither directory is referenced by the product `.csproj`; the additive publisher injects selected files only after the base APK exists.

## Dependencies and integration receipts

- `deps/Remedy` is a Git submodule pinned to the audited authority/lifecycle implementation. Terminal owns the adapter, router, and session semantics above Remedy's stable C boundary.
- `src/Terminal.Router` and `tests/native/integration` prove concurrent routed sessions on Windows and Android. They are not yet linked into the shipping Android application lifecycle.

## Present but not integrated

- `components/TUI-DWM` is the source candidate for movable cell surfaces, focus, touch, and the tabless multiplexer. Its Windows-specific input and presentation boundaries still require Android adapters.
- `components/ShaderUI` is the candidate for a later composited renderer. It is intentionally outside the v1 APK.
- `reference/android-terminal-original` preserves the earlier repository. It is reference material, not a second product project.

Moving code from `components` or `reference` into `src` is an explicit promotion: identify the narrow contract, remove platform assumptions, add a release-gate test, then make the product project depend on it. Merely being present in this workspace never means a component ships.

## Configuration

On first launch, `Assets/settings.ps1` is copied to the app-private PowerShell home as `$HOME/settings.ps1`. The native Settings UI reads and writes that file. `profile.ps1` is separate and is never overwritten by a visual-settings reset.

## Full reset

Clearing Terminal's Android storage is the true factory reset and removes profiles, settings, scripts, and private pairing identity. The in-app **Restore visual defaults** action is deliberately narrower.
