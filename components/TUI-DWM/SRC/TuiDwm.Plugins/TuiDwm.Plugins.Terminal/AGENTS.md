# TuiDwm.Plugins.Terminal — Terminal Plugin

## What this is
Hosts a real shell (pwsh/cmd) via ConPTY. Parses VT output into a Cell[] screen buffer.
Most complex plugin — owns the PTY session, VT state machine, and screen buffer.

## Key state
| Field | What |
|---|---|
| `_pty` | PtySession (ConPTY) — owns the child process |
| `_screen` | Cell[] — flat [y * _width + x] screen buffer, lock-protected |
| `_currentFg/Bg` | Current SGR color state |
| `_inEscape / _escapeBuffer` | VT escape sequence accumulator |
| `_cursorX/Y` | Cursor position |

## Key methods
| Method | What |
|---|---|
| `StartPty()` | Resolves shell path, starts PtySession, launches ReadPtyLoop thread |
| `ReadPtyLoop()` | Background thread: reads PTY OutputStream, decodes UTF-8, feeds ProcessChar |
| `ProcessChar(char)` | VT dispatch: escape vs control vs printable |
| `ParseEscape(string)` | CSI command handler: SGR, CUP, ED, EL, cursor movement |
| `ApplySgr(int)` | SGR attribute handler: 0–107, 38;5;N, 48;5;N (256-color) |
| `Render(CellBuffer)` | Copies _screen → CellBuffer under lock, draws cursor |
| `HandleInput(ConsoleKeyInfo)` | Translates ConsoleKey → VT sequence → PTY InputStream |
| `OnResize(w,h)` | Resizes _screen array AND calls _pty.Resize() |

## Known issues
- `catch { }` in HandleInput swallows all errors silently
- DSR/CPR handshake (line 136): responds to `\x1b[6n` with `\x1b[1;1R` to unblock pwsh init
