# TuiDwm.Core — Shared Contracts

## What this is
Shared library referenced by Engine and all Plugins. Contains only contracts and primitives — no logic.

## Files
| File | What |
|---|---|
| `ITuiPlugin.cs` | Plugin interface: Initialize, Render, HandleInput, OnResize, GetTemplate |
| `Cell.cs` | Single terminal cell: Rune (char), Fg (byte), Bg (byte), Style (byte) |
| `CellBuffer.cs` | 2D array of Cells with Width/Height and At(x,y) ref accessor |
| `Vom.cs` | Virtual Object Manager — ConcurrentDictionary registry, Get<T>/Set/TryGet/EnumerateKeys |
| `Vomt.cs` | VOM template — PluginName + Entries list for plugin self-registration |
| `PtySession.cs` | ConPTY RAII wrapper — Start, Resize, OutputStream, InputStream, IDisposable |

## Color convention
- Fg/Bg byte 0–15: standard xterm 16 colors
- Fg/Bg byte 16: default background (transparent-ish)
- Fg/Bg byte 17–255: xterm 256-color palette
