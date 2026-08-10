using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using TuiDwm.Core;

namespace TuiDwm.Engine;

public class Compositor
{
    private readonly Vom _vom;
    private readonly List<PluginInstance> _plugins;
    private readonly VtRenderer _renderer;
    private readonly ProjectionServer _projection;

    private CellBuffer _composite;
    private CellBuffer _previous;
    private readonly List<PluginInstance> _activePlugins = new();
    private readonly List<UiElementData> _uiElements = new(32);

    public Compositor(Vom vom, List<PluginInstance> plugins)
    {
        _vom = vom;
        _plugins = plugins;
        _renderer = new VtRenderer();
        _projection = new ProjectionServer();

        string jsonPath = System.IO.Path.Combine(System.AppContext.BaseDirectory, "..", "..", "..", "..", "..", "cascadia-code-metrics (1).json");
        if (System.IO.File.Exists("cascadia-code-metrics (1).json")) jsonPath = "cascadia-code-metrics (1).json";
        
        var atlas = AtlasMetrics.ParseToFloat32Array(jsonPath);
        _projection.SetAtlasMetrics(atlas);

        int width = Console.WindowWidth;
        int height = Console.WindowHeight;
        _vom.Set("\\Dwm\\Cols", width);
        _vom.Set("\\Dwm\\Rows", height);

        _composite = new CellBuffer(width, height);
        _previous = new CellBuffer(width, height);
    }

    public void Initialize()
    {
        _renderer.Initialize();
        _projection.Start();
    }

    public void Shutdown()
    {
        _renderer.Shutdown();
        _projection.Stop();
    }

    public void Composite(int currentFps)
    {
        // 1. Check for terminal resize
        int currentW = Console.WindowWidth;
        int currentH = Console.WindowHeight;
        int lastW = _vom.Get<int>("\\Dwm\\Cols", currentW);
        int lastH = _vom.Get<int>("\\Dwm\\Rows", currentH);

        if (currentW != lastW || currentH != lastH)
        {
            _vom.Set("\\Dwm\\Cols", currentW);
            _vom.Set("\\Dwm\\Rows", currentH);
            _composite.Resize(currentW, currentH);
            _renderer.Invalidate(); // clear stale cells from old dimensions
        }

        // 2. Compute window layouts (evaluates tile/fullscreen/stack/float modes)
        LayoutManager.Apply(_vom);

        // Viewport pan — only active in float mode
        string layoutMode = _vom.Get<string>("\\Dwm\\Layout", "float");
        bool isFloat = layoutMode.Equals("float", StringComparison.OrdinalIgnoreCase);
        int vpX = isFloat ? _vom.Get<int>("\\Dwm\\ViewportX", 0) : 0;
        int vpY = isFloat ? _vom.Get<int>("\\Dwm\\ViewportY", 0) : 0;

        // 3. Decoupled rendering fence checks (non-blocking)
                // Gather active plugins without per-frame LINQ allocations
        _activePlugins.Clear();
        foreach (var p in _plugins)
        {
            if (_vom.Get<bool>($"{p.BasePath}\\Visible", true))
            {
                _activePlugins.Add(p);
            }
        }
        _activePlugins.Sort((a, b) => _vom.Get<int>($"{b.BasePath}\\TargetZSlot", 0).CompareTo(_vom.Get<int>($"{a.BasePath}\\TargetZSlot", 0)));
        var activePlugins = _activePlugins;

        // Double-buffering: active plugins render to back buffer and swap to FrontBuffer under lock.
        // Compositor reads from FrontBuffer under lock, completely avoiding blocking.

        // 3.5 Background — mode 0 = Mica transparent, 1+ = gradients
        int bgMode = _vom.Get<int>("\\Dwm\\BgMode", 0);
        ApplyBackground(_composite, currentW, currentH, bgMode);

        // 4. Render desktop icons for hidden plugins
        RenderDesktop(_composite, _plugins);

        int focusIndex = _vom.Get<int>("\\Dwm\\FocusIndex", 0);

        // 5. Blit client areas and draw borders
        foreach (var p in activePlugins)
        {
            string basePath = p.BasePath;
            int wx = _vom.Get<int>($"{basePath}\\X", 0);
            int wy = _vom.Get<int>($"{basePath}\\Y", 0);
            int w  = _vom.Get<int>($"{basePath}\\Width", 10);
            int h  = _vom.Get<int>($"{basePath}\\Height", 5);
            string title = _vom.Get<string>($"{basePath}\\Title", $"Win {p.WindowIndex}");
            bool isFocused = (p.WindowIndex == focusIndex);

            // Apply viewport — convert world position to screen position
            int sx = wx - vpX;
            int sy = wy - vpY;

            // Skip windows entirely off-screen
            if (sx + w <= 0 || sx >= currentW || sy + h <= 0 || sy >= currentH) continue;

            // Draw window border chrome
            DrawBorder(_composite, sx, sy, w, h, title, isFocused);

            // Blit client area — offset by 2 rows (top border + title bar row)
            lock (p.BufferLock)
            {
                _composite.Blit(p.FrontBuffer, 0, 0, p.FrontBuffer.Width, p.FrontBuffer.Height, sx + 1, sy + 2);
                // Stream per-plugin texture to connected WebView clients (texture-of-textures path)
                int winZSlot = _vom.Get<int>($"{basePath}\\TargetZSlot", 0);
                _projection.PushWindow(p.WindowIndex, p.FrontBuffer, wx, wy, winZSlot, isAlive: true);
            }

            // --- 2.5D Volumetric Drop Shadow Pass ---
            // Projects a kinetic shadow based on window bounds to create spatial depth
            int zSlot = _vom.Get<int>($"{basePath}\\TargetZSlot", 0);
            int shadowOffsetX = 2 + zSlot; // Light from top-left, distance scales with Z
            int shadowOffsetY = 1 + (zSlot / 2);
            
            // Fast perimeter shadow drawing
            // Right shadow block
            for (int r = shadowOffsetY; r < h + shadowOffsetY; r++) {
                for (int c = w; c < w + shadowOffsetX; c++) {
                    ApplyShadowDrop(_composite, sx + c, sy + r, currentW, currentH, zSlot);
                }
            }
            // Bottom shadow block
            for (int r = h; r < h + shadowOffsetY; r++) {
                for (int c = shadowOffsetX; c < w; c++) {
                    ApplyShadowDrop(_composite, sx + c, sy + r, currentW, currentH, zSlot);
                }
            }
        }

        // 6. Double-buffering requires no fence resets

        // 7. Render status bar at the bottom
        StatusBar.Render(_composite, _vom, currentFps);

        // 8. Output VT diffs (local console path)
        _renderer.Render(_composite, _previous);

        // 9. Build and Broadcast Spatial UI Data (FLOAT32 for D3D12/WebGPU)
        _uiElements.Clear();
        
        float colNorm = 1.0f / currentW;
        float rowNorm = 1.0f / currentH;

        // Map status bar at bottom
        int barY = _vom.Get<int>("\\Dwm\\ThemeBtnY", -1);
        if (barY >= 0)
        {
            _uiElements.Add(new UiElementData
            {
                X = 0f, Y = barY * rowNorm,
                Width = 1.0f, Height = 1.0f * rowNorm,
                R = 0.1f, G = 0.1f, B = 0.15f, A = 1.0f,
                Z = 0.99f, ElementType = 3.0f, // Taskbar Top Bevel
                ColorId = 0.0f
            });
        }

        // Map active windows
        for (int i = 0; i < activePlugins.Count; i++)
        {
            var p = activePlugins[i];
            int zSlot = i + 1;
            
            float zNorm = _vom.Get<float>($"{p.BasePath}\\Z", 0f);
            if (zNorm <= 0f) zNorm = Math.Clamp(1.0f - (zSlot * 0.05f), 0.1f, 0.9f);
            
            float wx = _vom.Get<float>($"{p.BasePath}\\X", 0f) - (isFloat ? _vom.Get<float>("\\Dwm\\ViewportX", 0f) : 0f);
            float wy = _vom.Get<float>($"{p.BasePath}\\Y", 0f) - (isFloat ? _vom.Get<float>("\\Dwm\\ViewportY", 0f) : 0f);
            float ww = _vom.Get<float>($"{p.BasePath}\\Width", 10f);
            float wh = _vom.Get<float>($"{p.BasePath}\\Height", 5f);

            _uiElements.Add(new UiElementData
            {
                X = wx * colNorm,
                Y = wy * rowNorm,
                Width = ww * colNorm,
                Height = wh * rowNorm,
                R = 0.1f, G = 0.1f, B = 0.1f, A = 1.0f,
                Z = zNorm,
                Rotation = 0.0f,
                ElementType = 2.0f, // Window 
                ColorId = 1.0f,     // Signal shader to sample appletTexture
                TexBlend = 1.0f
            });
        }

        // Map Context Menus
        int contextMenuCount = _vom.Get<int>("\\Windows\\ContextMenus\\Count", 0);
        for (int i = 0; i < contextMenuCount; i++)
        {
            string basePath = $"\\Windows\\ContextMenus\\{i}";
            float wx = _vom.Get<float>($"{basePath}\\X", 0f);
            float wy = _vom.Get<float>($"{basePath}\\Y", 0f);
            float ww = _vom.Get<float>($"{basePath}\\Width", 0f);
            float wh = _vom.Get<float>($"{basePath}\\Height", 0f);
            float rot = _vom.Get<float>($"{basePath}\\Rotation", 0f);
            float elemType = _vom.Get<float>($"{basePath}\\ElementType", 2.0f); // Default to Mica

            _uiElements.Add(new UiElementData
            {
                X = wx, // We set them as raw spatial values in MenuAdapter
                Y = wy,
                Width = ww,
                Height = wh,
                Z = 1.0f - (i * 0.001f), // Stack them above everything else
                Rotation = rot,
                ElementType = elemType,
                TexBlend = 0.0f, // No texture blending, just pure Mica shader
                A = 0.8f // 80% opacity for the frosted glass
            });
        }

        _projection.BroadcastSpatial(_uiElements);

        // 10. Broadcast composited frame to any WebView clients (simple single-texture path)
        _projection.BroadcastComposite(_composite);

        // 11. Swap references
        var temp = _composite;
        _composite = _previous;
        _previous = temp;
    }

    private static void ApplyShadowDrop(CellBuffer buf, int tx, int ty, int maxW, int maxH, int zSlot) {
        if (tx >= 0 && tx < maxW && ty >= 0 && ty < maxH) {
            ref var cell = ref buf.At(tx, ty);
            byte dim = (byte)(zSlot >= 2 ? 3 : 5); // Softer shadows when higher up
            
            if (cell.Bg >= 232 && cell.Bg <= 255) {
                cell.Bg = (byte)Math.Max(232, cell.Bg - dim); 
            } else if (cell.Bg == 0 || cell.Bg == 16) {
                cell.Bg = 233; // Darker shadow
            }
            if (cell.Fg >= 232 && cell.Fg <= 255) {
                cell.Fg = (byte)Math.Max(232, cell.Fg - dim);
            }
        }
    }

    private static void DrawBorder(CellBuffer composite, int x, int y, int w, int h, string title, bool focused)
    {
        if (w <= 0 || h < 3) return;

        byte borderFg  = focused ? (byte)51  : (byte)240;
        byte borderBg  = 16;
        byte borderSty = focused ? (byte)1   : (byte)2;

        // Title bar background (row y+1)
        byte tbBg  = focused ? (byte)17  : (byte)233; // dark navy / near-black
        byte tbFg  = focused ? (byte)51  : (byte)244;
        byte tbSty = focused ? (byte)1   : (byte)0;

        // ── Row 0: thin top frame ─────────────────────────────────────────────
        WriteCell(composite, x, y, '╭', borderFg, borderBg, borderSty);
        WriteCell(composite, x + w - 1, y, '╮', borderFg, borderBg, borderSty);
        for (int col = x + 1; col < x + w - 1; col++)
        {
            if (col >= 0 && col < composite.Width && y >= 0 && y < composite.Height)
            { ref var c = ref composite.At(col, y); c.Rune = '─'; c.Fg = borderFg; c.Bg = borderBg; c.Style = borderSty; }
        }

        // ── Row 1: title bar (colored, bigger touch target) ───────────────────
        int ty = y + 1;
        WriteCell(composite, x, ty, '│', borderFg, borderBg, borderSty);
        WriteCell(composite, x + w - 1, ty, '│', borderFg, borderBg, borderSty);
        // Fill background
        for (int col = x + 1; col < x + w - 1; col++)
        {
            if (col >= 0 && col < composite.Width && ty >= 0 && ty < composite.Height)
            { ref var c = ref composite.At(col, ty); c.Rune = ' '; c.Fg = tbFg; c.Bg = tbBg; c.Style = 0; }
        }
        // Decoration buttons on title bar row
        bool hasDecorations = w >= 10;
        if (hasDecorations)
        {
            byte clsFg = focused ? (byte)203 : (byte)238;
            byte minFg = focused ? (byte)226 : (byte)238;
            WriteCell(composite, x + w - 2, ty, '●', clsFg, tbBg, focused ? (byte)1 : (byte)0);
            WriteCell(composite, x + w - 3, ty, ' ', tbFg,  tbBg, 0);
            WriteCell(composite, x + w - 4, ty, '●', minFg, tbBg, focused ? (byte)1 : (byte)0);
            WriteCell(composite, x + w - 5, ty, ' ', tbFg,  tbBg, 0);
        }
        // Title text
        if (!string.IsNullOrEmpty(title))
        {
            int decorReserve = hasDecorations ? 6 : 0;
            string t = $" {title} ";
            int maxLen = w - 2 - decorReserve;
            if (maxLen > 0)
            {
                if (t.Length > maxLen) t = t[..maxLen];
                for (int i = 0; i < t.Length; i++)
                {
                    int tx = x + 1 + i;
                    if (tx >= 0 && tx < composite.Width && ty >= 0 && ty < composite.Height)
                    { ref var c = ref composite.At(tx, ty); c.Rune = t[i]; c.Fg = tbFg; c.Bg = tbBg; c.Style = tbSty; }
                }
            }
        }

        // ── Rows 2 … h-2: side borders (content area) ────────────────────────
        for (int row = y + 2; row < y + h - 1; row++)
        {
            if (row >= 0 && row < composite.Height)
            {
                if (x >= 0 && x < composite.Width)
                { ref var c = ref composite.At(x, row); c.Rune = '│'; c.Fg = borderFg; c.Bg = borderBg; c.Style = borderSty; }
                if (x + w - 1 >= 0 && x + w - 1 < composite.Width)
                { ref var c = ref composite.At(x + w - 1, row); c.Rune = '│'; c.Fg = borderFg; c.Bg = borderBg; c.Style = borderSty; }
            }
        }

        // ── Bottom frame ──────────────────────────────────────────────────────
        int by = y + h - 1;
        WriteCell(composite, x, by, '╰', borderFg, borderBg, borderSty);
        WriteCell(composite, x + w - 1, by, '╯', borderFg, borderBg, borderSty);
        for (int col = x + 1; col < x + w - 1; col++)
        {
            if (col >= 0 && col < composite.Width && by >= 0 && by < composite.Height)
            { ref var c = ref composite.At(col, by); c.Rune = '─'; c.Fg = borderFg; c.Bg = borderBg; c.Style = borderSty; }
        }
    }

    private void RenderDesktop(CellBuffer buffer, List<PluginInstance> plugins)
    {
        const int iconW = 10;
        const int iconH = 5;
        const int padX = 3;
        const int padY = 2;
        int col = padX;
        int row = padY;

        foreach (var p in plugins)
        {
            if (_vom.Get<bool>($"{p.BasePath}\\Visible", false)) continue;

            string name = _vom.Get<string>($"{p.BasePath}\\PluginName", "App");
            char icon = GetPluginIcon(name);

            // Preserve position if already placed (supports icon drag)
            bool hasPos = _vom.TryGet<int>($"{p.BasePath}\\IconX", out int iconX)
                        & _vom.TryGet<int>($"{p.BasePath}\\IconY", out int iconY);
            if (!hasPos)
            {
                iconX = col;
                iconY = row;
                _vom.Set($"{p.BasePath}\\IconX", iconX);
                _vom.Set($"{p.BasePath}\\IconY", iconY);
                // Only advance the grid slot for icons that haven't been placed yet
                col += iconW + padX;
                if (col + iconW > buffer.Width - padX) { col = padX; row += iconH + 1; }
            }

                        int vpX = _vom.Get<int>("\\Dwm\\ViewportX", 0);
            int vpY = _vom.Get<int>("\\Dwm\\ViewportY", 0);
            int zoom = _vom.Get<int>("\\Dwm\\ZoomLevel", 1);
            
            int sx = (iconX - vpX) / zoom;
            int sy = (iconY - vpY) / zoom;
            int scaledW = Math.Max(4, iconW / zoom);
            int scaledH = Math.Max(2, iconH / zoom);
            
            if (sx + scaledW >= 0 && sx < buffer.Width && sy + scaledH >= 0 && sy < buffer.Height) {
                DrawIcon(buffer, sx, sy, scaledW, scaledH, icon, zoom > 1 ? name.Substring(0, Math.Min(3, name.Length)) : name);
            }
            _vom.Set($"{p.BasePath}\\IconW", iconW);
            _vom.Set($"{p.BasePath}\\IconH", iconH);
        }
    }

    private static char GetPluginIcon(string name) => name.ToLowerInvariant() switch
    {
        "terminal" => '⌨',
        "clock"    => '⊙',
        "sysinfo"  => '≡',
        "matrixrain" => '▓',
        _ => '◈'
    };

    private static void DrawIcon(CellBuffer buffer, int x, int y, int w, int h, char icon, string label)
    {
        byte bg = 235;
        byte borderFg = 240;
        byte iconFg = 255;
        byte labelFg = 250;

        // Fill interior
        for (int row = y + 1; row < y + h - 1; row++)
            for (int col = x + 1; col < x + w - 1; col++)
                if (col >= 0 && col < buffer.Width && row >= 0 && row < buffer.Height)
                { ref var c = ref buffer.At(col, row); c.Rune = ' '; c.Bg = bg; c.Fg = borderFg; }

        // Border
        for (int col = x + 1; col < x + w - 1; col++)
        {
            if (col >= 0 && col < buffer.Width)
            {
                if (y >= 0 && y < buffer.Height)
                { ref var c = ref buffer.At(col, y); c.Rune = '─'; c.Fg = borderFg; c.Bg = bg; }
                if (y + h - 1 >= 0 && y + h - 1 < buffer.Height)
                { ref var c = ref buffer.At(col, y + h - 1); c.Rune = '─'; c.Fg = borderFg; c.Bg = bg; }
            }
        }
        for (int row = y + 1; row < y + h - 1; row++)
        {
            if (row >= 0 && row < buffer.Height)
            {
                if (x >= 0 && x < buffer.Width)
                { ref var c = ref buffer.At(x, row); c.Rune = '│'; c.Fg = borderFg; c.Bg = bg; }
                if (x + w - 1 >= 0 && x + w - 1 < buffer.Width)
                { ref var c = ref buffer.At(x + w - 1, row); c.Rune = '│'; c.Fg = borderFg; c.Bg = bg; }
            }
        }
        WriteCell(buffer, x,         y,         '╭', borderFg, bg, 0);
        WriteCell(buffer, x + w - 1, y,         '╮', borderFg, bg, 0);
        WriteCell(buffer, x,         y + h - 1, '╰', borderFg, bg, 0);
        WriteCell(buffer, x + w - 1, y + h - 1, '╯', borderFg, bg, 0);

        // Icon glyph centered
        int iconX = x + w / 2;
        int iconY = y + (h - 2) / 2;
        if (iconX >= 0 && iconX < buffer.Width && iconY >= 0 && iconY < buffer.Height)
        { ref var c = ref buffer.At(iconX, iconY); c.Rune = icon; c.Fg = iconFg; c.Bg = bg; c.Style = 1; }

        // Label row centered
        string lbl = label.Length > w - 2 ? label[..(w - 2)] : label;
        int lx = x + (w - lbl.Length) / 2;
        int ly = y + h - 2;
        for (int i = 0; i < lbl.Length; i++)
        {
            int tx = lx + i;
            if (tx >= 0 && tx < buffer.Width && ly >= 0 && ly < buffer.Height)
            { ref var c = ref buffer.At(tx, ly); c.Rune = lbl[i]; c.Fg = labelFg; c.Bg = bg; }
        }
    }

    private static void WriteCell(CellBuffer buffer, int x, int y, char rune, byte fg, byte bg, byte style)
    {
        if (x >= 0 && x < buffer.Width && y >= 0 && y < buffer.Height)
        {
            ref var cell = ref buffer.At(x, y);
            cell.Rune = rune;
            cell.Fg = fg;
            cell.Bg = bg;
            cell.Style = style;
        }
    }

    // Background modes:
    //   0 = Mica/transparent (Bg=0 → \x1b[49m → Windows Terminal Mica shows through)
    //   1 = Noir  (solid near-black)
    //   2 = Slate (dark grey gradient)
    //   3 = Ocean (dark navy gradient)
    //   4 = Dusk  (purple→navy→teal)
    //   5 = Ember (dark ember/red)
    private static readonly (string Name, byte[] Stops)[] BgThemes =
    {
        ("Mica",  Array.Empty<byte>()),
        ("Noir",  new byte[]{ 232 }),
        ("Slate", new byte[]{ 232, 234, 235, 234, 232 }),
        ("Ocean", new byte[]{ 17,  18,  24,  18,  17  }),
        ("Dusk",  new byte[]{ 54,  17,  18,  22,  17  }),
        ("Ember", new byte[]{ 52,  88,  52,  16,  16  }),
    };

    public static string BgThemeName(int mode)
    {
        int i = Math.Clamp(mode, 0, BgThemes.Length - 1);
        return BgThemes[i].Name;
    }

    public static int BgThemeCount => BgThemes.Length;

    private static void ApplyBackground(CellBuffer buf, int w, int h, int mode)
    {
        int i = Math.Clamp(mode, 0, BgThemes.Length - 1);
        byte[] stops = BgThemes[i].Stops;
        int rows = h - 1; // leave status bar row to compositor later

        for (int y = 0; y < rows; y++)
        {
            byte bg;
            if (stops.Length == 0)
            {
                bg = 0; // transparent → Mica
            }
            else if (stops.Length == 1)
            {
                bg = stops[0];
            }
            else
            {
                float t = rows > 1 ? (float)y / (rows - 1) : 0f;
                float f = t * (stops.Length - 1);
                int si = Math.Clamp((int)f, 0, stops.Length - 2);
                // blend between two nearest stops using a simple lerp of the stop index
                float blend = f - si;
                bg = blend < 0.5f ? stops[si] : stops[si + 1];
            }

            for (int x = 0; x < w; x++)
            {
                ref var cell = ref buf.At(x, y);
                cell.Rune  = ' ';
                cell.Fg    = 7;
                cell.Bg    = bg;
                cell.Style = 0;
            }
        }
    }
}
