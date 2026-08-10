# Architecture

Terminal keeps six boundaries deliberately small:

1. **PowerShell host** owns the persistent CoreCLR runspace, commands, objects, profile, and script configuration.
2. **TerminalEngine** is Android-free and owns VT parsing, primary/alternate screens, cell state, cursor, scroll regions, scrollback, modes, selection state, and semantic terminal events.
3. **Native presenter** owns Android Canvas projection, touch viewport gestures, font metrics, and colors. It draws fixed cells and does not parse VT.
4. **Android capability boundary** owns permissions, intents, notifications, lifecycle, clipboard, and device services.
5. **Optional transports** such as self-ADB and future PSRP expose explicit authority without changing ordinary sandbox behavior.
6. **Surface/hardpoint boundary** owns typed UI documents, origin-scoped resources, serialized semantic events, and additive release cargo. It does not own the runspace, Android compiled resources, package identity, or signing.

Features should enter through cmdlets or narrow Android capability contracts. Adding an editor, server, or TUI must not require surgery in the runspace or renderer.

The shipping `.csproj` contains the stable mechanism only. Optional hardpoints are admitted after compilation by `scripts/Publish-TerminalRelease.ps1`; the publisher proves that existing APK payload bytes did not change before aligning, signing, verifying, and emitting an external receipt. Roslyn is a peer capability of Terminal's PowerShell platform, not a Surface dependency.

The proven ADB protocol sources now use a Terminal-owned long-running read task and logger. They have no VOM or runtime-broker dependency.

The terminal model lives beside the runspace in process authority. Activities are disposable presenters: detaching an Activity removes its render subscription, while the useful runspace and terminal state can remain. There is no render timer, animation clock, or terminal polling loop while the view is absent.
