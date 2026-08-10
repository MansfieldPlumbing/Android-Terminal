# TuiDwm.Engine — Compositor & Host

## What this is
Main executable. Owns the compositor loop, input polling, plugin hosting, and VT rendering.

## Files
| File | What |
|---|---|
| `Program.cs` | Entry point. Console setup, input polling loop (INPUT_RECORD[]), plugin dispatch. |
| `Compositor.cs` | Frame compositor. Calls plugin Render(), diffs CellBuffers, hands to VtRenderer. |
| `VtRenderer.cs` | Writes VT escape sequences to stdout. Hot path — minimize allocations here. |
| `LayoutManager.cs` | Reads VOM `\Windows\*` keys to compute plugin layout rectangles. |
| `StatusBar.cs` | Renders the bottom status bar from VOM window keys. |
| `PluginHost.cs` | Loads plugin DLLs from plugins/ directory at startup via reflection. |

## Known antipatterns in this directory
- `Compositor.cs:65` — per-frame LINQ allocs (Where+ToList, OrderByDescending+ToList)
- `VtRenderer.cs:62` — string interpolation `$"\x1b[{y+1};{x+1}H"` in hot path
- `Program.cs:226` — `new INPUT_RECORD[numEvents]` allocated per poll
- `LayoutManager.cs:20` — `int.Parse` on VOM keys with no TryParse guard (crash vector)
- `StatusBar.cs:32` — same int.Parse crash vector

## Frame budget
Target: 120 FPS. Per-frame allocations in Compositor + VtRenderer are the primary GC risk.
