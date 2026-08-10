# Architecture

Terminal keeps four boundaries deliberately small:

1. **PowerShell host** owns the persistent CoreCLR runspace, commands, objects, profile, and script configuration.
2. **Cell presenter** owns native drawing, ANSI color, scrollback, zoom, and terminal input presentation.
3. **Android capability boundary** owns permissions, intents, notifications, lifecycle, clipboard, and device services.
4. **Optional transports** such as self-ADB and future PSRP expose explicit authority without changing ordinary sandbox behavior.

Features should enter through cmdlets or narrow Android capability contracts. Adding an editor, server, or TUI must not require surgery in the runspace or renderer.

The proven ADB protocol sources now use a Terminal-owned long-running read task and logger. They have no VOM or runtime-broker dependency.
