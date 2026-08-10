using System;
using System.Diagnostics;
using TuiDwm.Canvas;
using TuiDwm.Core;

// ── TuiDwm.Canvas — AOT D3D12 terminal canvas ─────────────────────────────────
//
// No WebView. No plugin system. No tabs.
// Sessions navigated via kinematic touch swipe. Snap zones on drag edges.
// Renders via D3D12: cell buffer → RGBA8 texture → instanced quads → HLSL shaders.

var window   = new Win32Window();
var renderer = new SkiaRenderer();
var switcher = new KinematicSwitcher();

// ── Seed one session ──────────────────────────────────────────────────────────
int initCols = 120, initRows = 36;
var session = new TerminalSession(initCols, initRows) { Title = "pwsh" };
switcher.AddSession(session);
session.Start();

// ── Wire window events ────────────────────────────────────────────────────────
window.Resized += (w, h) =>
{
    renderer.Resize(w, h);
    int cols = Math.Max(1, w / 9);
    int rows = Math.Max(1, h / 18);
    switcher.GetVisibleSessions((s, _) => s.Resize(cols, rows));
};

window.PointerDown   += e => switcher.OnPointerDown(e);
window.PointerUpdate += e => switcher.OnPointerUpdate(e, window.ClientWidth);
window.PointerUp     += e =>
{
    // Check snap zone on pointer up
    var zone = SnapZones.Detect(e.X, e.Y, window.ClientWidth, window.ClientHeight);
    if (zone != SnapZone.None)
    {
        // For now: snap means resize the active session to the zone rect
        var (zx, zy, zw, zh) = SnapZones.GetRect(zone, window.ClientWidth, window.ClientHeight);
        int cols = Math.Max(1, zw / 9);
        int rows = Math.Max(1, zh / 18);
        switcher.Active?.Resize(cols, rows);
    }
    switcher.OnPointerUp(e, window.ClientWidth);
};

window.KeyDown += key =>
{
    // Forward printable keys to active session
    if (key.VKey >= 0x20 && key.VKey < 0x7F)
        switcher.Active?.SendInput(((char)key.VKey).ToString());
    if (key.VKey == 0x0D) switcher.Active?.SendInput("\r");
    if (key.VKey == 0x08) switcher.Active?.SendInput("\x7f");
};

window.CloseRequested += () =>
{
    switcher.GetVisibleSessions((s, _) => s.Dispose());
    renderer.Dispose();
};

// ── Create window + D3D12 device ──────────────────────────────────────────────
window.Create("TuiCanvas", 1280, 800);
renderer.Initialize(window.Hwnd, window.ClientWidth, window.ClientHeight);

// ── Render loop (dedicated thread, compositor owns it) ────────────────────────
var renderThread = new System.Threading.Thread(() =>
{
    var sw = Stopwatch.StartNew();
    double last = sw.Elapsed.TotalSeconds;

    while (true)
    {
        double now = sw.Elapsed.TotalSeconds;
        float  dt  = (float)(now - last);
        last = now;

        switcher.Tick(dt);

        Span<WindowEntry> windows = stackalloc WindowEntry[switcher.SessionCount];
        int winCount = 0;
        switcher.GetVisibleSessions((session, offset) => {
            float viewWidth = window.ClientWidth;
            float cx = (viewWidth * 0.5f) + (offset * viewWidth);
            float w = viewWidth * 0.85f;
            float h = window.ClientHeight * 0.8f;
            windows[winCount++] = new WindowEntry
            {
                X = cx - (w * 0.5f),
                Y = (window.ClientHeight - h) * 0.5f,
                W = w,
                H = h,
                Z = Math.Abs(offset),
                Active = (session == switcher.Active),
                Title = session.Title,
                Content = session
            };
        });

        renderer.Render(windows.Slice(0, winCount), (float)now);

        // ~120 FPS cap
        double elapsed = sw.Elapsed.TotalSeconds - now;
        int sleep = Math.Max(0, (int)((1.0 / 120.0 - elapsed) * 1000.0));
        if (sleep > 0) System.Threading.Thread.Sleep(sleep);
    }
})
{ IsBackground = true, Name = "TuiCanvas-Render" };
renderThread.Start();

// ── Message loop (main thread) ────────────────────────────────────────────────
window.RunMessageLoop();
window.Dispose();
