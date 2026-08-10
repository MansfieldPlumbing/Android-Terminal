using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using TuiDwm.Core;

namespace TuiDwm.Engine;

class Program
{
    private static volatile bool _running = true;
    private static Compositor? _compositor;
    private static List<PluginInstance> _pluginInstances = new();
    private static readonly object _shutdownLock = new();
    private static bool _cleanedUp = false;

    private static readonly INPUT_RECORD[] _inputBuffer = new INPUT_RECORD[64];

    // Mouse drag state (windows)
    private static bool _isDragging = false;
    private static int _dragOffsetIndex = -1;
    private static int _dragOffsetX = 0;
    private static int _dragOffsetY = 0;

    // Mouse resize state
    [Flags]
    private enum ResizeEdge { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }
    private static bool _isResizing = false;
    private static int _resizeWindowIndex = -1;
    private static MenuAdapter? _menuAdapter;
    private static ResizeEdge _resizeEdges = ResizeEdge.None;
    private static int _resizeOriginalX;
    private static int _resizeOriginalY;
    private static int _resizeOriginalW;
    private static int _resizeOriginalH;
    private static int _resizeMouseStartX;
    private static int _resizeMouseStartY;

    // Icon drag state
    private static bool _isDraggingIcon = false;
    private static int _draggingIconIndex = -1;
    private static int _iconDragOffsetX = 0;
    private static int _iconDragOffsetY = 0;

    // Double-click tracking
    private static long _lastClickTime = 0;
    private static int _lastClickX = -1;
    private static int _lastClickY = -1;

    // Middle-click pan state
    private static bool _isPanning = false;
    private static int _panLastX = 0;
    private static int _panLastY = 0;

    // Win32 Console P/Invokes and constants
    private const int STD_INPUT_HANDLE = -10;
    private const uint ENABLE_MOUSE_INPUT = 0x0010;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleInput, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleInput, uint dwMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNumberOfConsoleInputEvents(IntPtr hConsoleInput, out uint lpcNumberOfEvents);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool ReadConsoleInput(IntPtr hConsoleInput, [Out] INPUT_RECORD[] lpBuffer, uint nLength, out uint lpNumberOfEventsRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleScreenBufferSize(IntPtr hConsoleOutput, COORD dwSize);

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT_RECORD
    {
        [FieldOffset(0)]
        public ushort EventType;
        [FieldOffset(4)] // Win32 pads WORD EventType to 4-byte boundary before the union
        public KEY_EVENT_RECORD KeyEvent;
        [FieldOffset(4)]
        public MOUSE_EVENT_RECORD MouseEvent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEY_EVENT_RECORD
    {
        public int bKeyDown;           // Win32 BOOL = 4 bytes
        public ushort wRepeatCount;
        public ushort wVirtualKeyCode;
        public ushort wVirtualScanCode;
        public ushort UnicodeChar;     // Win32 WCHAR = 2 bytes
        public uint dwControlKeyState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSE_EVENT_RECORD
    {
        public COORD dwMousePosition;
        public uint dwButtonState;
        public uint dwControlKeyState;
        public uint dwEventFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
    }

    static void Main(string[] args)
    {
        Console.WriteLine("Starting TUI-DWM Compositor Engine...");

        // 1. Setup console hygiene
        Console.CursorVisible = false;
        Console.TreatControlCAsInput = true;

        // Enable mouse input and disable quick edit mode
        IntPtr hStdIn = GetStdHandle(STD_INPUT_HANDLE);
        if (GetConsoleMode(hStdIn, out uint mode))
        {
            uint newMode = mode | ENABLE_MOUSE_INPUT | 0x0008; // ENABLE_WINDOW_INPUT
            newMode &= ~0x0040u; // disable QUICK_EDIT
            newMode |= 0x0080u;  // ENABLE_EXTENDED_FLAGS
            SetConsoleMode(hStdIn, newMode);
        }

        // Shrink screen buffer to window size — eliminates scrollback so Windows Terminal
        // shows no scrollbar and all scroll-wheel events reach our app as MOUSE_WHEELED.
        IntPtr hStdOut = GetStdHandle(-11); // STD_OUTPUT_HANDLE
        SetConsoleScreenBufferSize(hStdOut, new COORD
        {
            X = (short)Console.WindowWidth,
            Y = (short)Console.WindowHeight
        });

        var vom = new Vom();
        
        // 0. Initialize Win32 Context Menu IPC
        _menuAdapter = new MenuAdapter(vom);
        _menuAdapter.Start();

        vom.Set("\\Dwm\\Layout", "float");
        vom.Set("\\Dwm\\FocusIndex", 0);
        vom.Set("\\Dwm\\ViewportX", 0);
        vom.Set("\\Dwm\\ViewportY", 0);
        vom.Set("\\Dwm\\BgMode", 0); // 0 = Mica/transparent

        // Determine plugin directory (absolute path)
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string pluginsDir = Path.Combine(baseDir, "plugins");
        
        // Also check up the directory tree if running in IDE (bin/Debug/net10.0/)
        if (!Directory.Exists(pluginsDir))
        {
            string rootDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "plugins"));
            if (Directory.Exists(rootDir))
            {
                pluginsDir = rootDir;
            }
            else
            {
                Directory.CreateDirectory(pluginsDir);
            }
        }

        vom.Set("\\Dwm\\PluginDir", pluginsDir);

        // 2. Discover and load plugins
        var plugins = PluginLoader.LoadPlugins(pluginsDir);
        if (plugins.Count == 0)
        {
            Console.WriteLine($"No plugins loaded from: {pluginsDir}");
            Console.WriteLine("Please compile plugins and place them in the 'plugins' folder.");
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey(true);
            return;
        }

        // Initialize plugin instances — all start hidden, launched via desktop icons
        for (int i = 0; i < plugins.Count; i++)
        {
            var plugin = plugins[i];
            var template = plugin.GetTemplate();

            string basePath = $"\\Windows\\{i}";
            vom.Set($"{basePath}\\PluginName", template.PluginName);
            foreach (var entry in template.Entries)
            {
                vom.Set(basePath + entry.KeySuffix, entry.DefaultValue);
            }

            // Override: start hidden, stagger default float positions
            vom.Set($"{basePath}\\Visible", false);
            vom.Set($"{basePath}\\X", 4 + i * 4);
            vom.Set($"{basePath}\\Y", 2 + i * 2);
            vom.Set($"{basePath}\\Width", 60);
            vom.Set($"{basePath}\\Height", 20);

            var instance = new PluginInstance(plugin, vom, i);
            _pluginInstances.Add(instance);
        }

        // Initialize focus to first plugin
        if (_pluginInstances.Count > 0)
        {
            int initFocus = _pluginInstances[0].WindowIndex;
            vom.Set("\\Dwm\\FocusIndex", initFocus);
            UpdateZIndices(vom, _pluginInstances, initFocus);
        }

        // 3. Create compositor
        _compositor = new Compositor(vom, _pluginInstances);

        // Setup shutdown handlers
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            _running = false;
        };
        AppDomain.CurrentDomain.ProcessExit += (s, e) => Shutdown();

        // 4. Start plugin threads
        foreach (var instance in _pluginInstances)
        {
            instance.Start();
        }

        // Initialize compositor graphics alternate buffer
        _compositor.Initialize();

        // 5. Compositor Loop
        var frameTimer = Stopwatch.StartNew();
        var fpsTimer = Stopwatch.StartNew();
        int frameCount = 0;
        int currentFps = 0;

        const double targetFrameTimeMs = 1000.0 / 120.0; // Decoupled unblocked loop targets 120 FPS
        long lastTime = Stopwatch.GetTimestamp();

        while (_running)
        {
            double frameStart = frameTimer.Elapsed.TotalMilliseconds;
            long now = Stopwatch.GetTimestamp();
            double deltaTime = (now - lastTime) / (double)Stopwatch.Frequency;
            lastTime = now;

            // Handle Input
            ProcessInput(vom, _pluginInstances);

            // Tick Kinematics
            int draggedIndex = _isDragging ? _dragOffsetIndex : (_isResizing ? _resizeWindowIndex : -1);
            Kinematics.Tick(vom, _pluginInstances, draggedIndex, deltaTime);

            // Compose & render frame
            _compositor.Composite(currentFps);

            frameCount++;
            if (fpsTimer.ElapsedMilliseconds >= 1000)
            {
                currentFps = (int)(frameCount * 1000.0 / fpsTimer.ElapsedMilliseconds);
                frameCount = 0;
                fpsTimer.Restart();
            }

            // Sleep remainder of the budget
            double frameElapsed = frameTimer.Elapsed.TotalMilliseconds - frameStart;
            double remainingTime = targetFrameTimeMs - frameElapsed;
            if (remainingTime > 0)
            {
                Thread.Sleep((int)remainingTime);
            }
        }

        Shutdown();
    }

    private static void ProcessInput(Vom vom, List<PluginInstance> plugins)
    {
        IntPtr hStdIn = GetStdHandle(STD_INPUT_HANDLE);
        if (!GetNumberOfConsoleInputEvents(hStdIn, out uint numEvents) || numEvents == 0)
        {
            return;
        }

        uint toRead = Math.Min(numEvents, (uint)_inputBuffer.Length);
        if (ReadConsoleInput(hStdIn, _inputBuffer, toRead, out uint readCount))
        {
            for (int i = 0; i < readCount; i++)
            {
                var rec = _inputBuffer[i];
                if (rec.EventType == 0x0001) // KEY_EVENT
                {
                    var keyRec = rec.KeyEvent;
                    if (keyRec.bKeyDown != 0)
                    {
                        bool shift = (keyRec.dwControlKeyState & 0x0010) != 0;
                        bool alt = (keyRec.dwControlKeyState & 0x0002) != 0 || (keyRec.dwControlKeyState & 0x0001) != 0;
                        bool control = (keyRec.dwControlKeyState & 0x0008) != 0 || (keyRec.dwControlKeyState & 0x0004) != 0;

                        var keyInfo = new ConsoleKeyInfo(
                            (char)keyRec.UnicodeChar,
                            (ConsoleKey)keyRec.wVirtualKeyCode,
                            shift,
                            alt,
                            control
                        );

                        HandleKeyEvent(vom, plugins, keyInfo);
                    }
                }
                else if (rec.EventType == 0x0002) // MOUSE_EVENT
                {
                    var mouseRec = rec.MouseEvent;
                    HandleMouseEvent(vom, plugins, mouseRec);
                }
            }
        }
    }

    private static void HandleKeyEvent(Vom vom, List<PluginInstance> plugins, ConsoleKeyInfo key)
    {
        bool alt = (key.Modifiers & ConsoleModifiers.Alt) != 0;
        bool shift = (key.Modifiers & ConsoleModifiers.Shift) != 0;

        int focusIndex = vom.Get<int>("\\Dwm\\FocusIndex", 0);
        var active = plugins.FirstOrDefault(p => p.WindowIndex == focusIndex);

        // Block input to dying windows
        if (active != null && active.State == LifecycleState.Exiting) return;

        // Global Hotkeys background theme
        if (key.Key == ConsoleKey.B && (key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            int cur = vom.Get<int>("\\Dwm\\BgMode", 0);
            vom.Set("\\Dwm\\BgMode", (cur + 1) % Compositor.BgThemeCount);
            return;
        }

        // Viewport pan — only in float mode
        if ((key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            string currentLayout = vom.Get<string>("\\Dwm\\Layout", "float");
            if (currentLayout.Equals("float", StringComparison.OrdinalIgnoreCase))
            {
                if (key.Key == ConsoleKey.LeftArrow)
                    vom.Set("\\Dwm\\ViewportX", vom.Get<int>("\\Dwm\\ViewportX", 0) - 5);
                else if (key.Key == ConsoleKey.RightArrow)
                    vom.Set("\\Dwm\\ViewportX", vom.Get<int>("\\Dwm\\ViewportX", 0) + 5);
                else if (key.Key == ConsoleKey.UpArrow)
                    vom.Set("\\Dwm\\ViewportY", vom.Get<int>("\\Dwm\\ViewportY", 0) - 2);
                else if (key.Key == ConsoleKey.DownArrow)
                    vom.Set("\\Dwm\\ViewportY", vom.Get<int>("\\Dwm\\ViewportY", 0) + 2);
                else if (key.Key == ConsoleKey.Home)
                {
                    vom.Set("\\Dwm\\ViewportX", 0);
                    vom.Set("\\Dwm\\ViewportY", 0);
                }
            }
            return;
        }

        if (alt)
        {
            if (key.Key == ConsoleKey.Q)
            {
                _running = false;
                return;
            }
            else if (key.Key == ConsoleKey.Enter || key.Key == ConsoleKey.T)
            {
                SpawnPlugin(vom, plugins, "TerminalPlugin");
            }
            else if (key.Key == ConsoleKey.F)
            {
                SpawnPlugin(vom, plugins, "FileSystem3DPlugin");
            }
            else if (key.Key == ConsoleKey.C)
            {
                CloseWindow(vom, plugins, vom.Get<int>("\\Dwm\\FocusIndex", 0));
            }
            else if (key.Key == ConsoleKey.Spacebar)
            {
                // Cycle layouts: tile -> fullscreen -> stack -> float -> tile
                string current = vom.Get<string>("\\Dwm\\Layout", "tile");
                string next = current switch
                {
                    "tile" => "fullscreen",
                    "fullscreen" => "stack",
                    "stack" => "float",
                    _ => "tile"
                };
                vom.Set("\\Dwm\\Layout", next);
            }
            else if (key.Key == ConsoleKey.Tab || key.Key == ConsoleKey.J)
            {
                // Next window focus
                CycleFocus(vom, plugins, 1);
            }
            else if (key.Key == ConsoleKey.K || (key.Key == ConsoleKey.Tab && shift))
            {
                // Previous window focus
                CycleFocus(vom, plugins, -1);
            }
            else if (key.Key >= ConsoleKey.D1 && key.Key <= ConsoleKey.D9)
            {
                int index = key.Key - ConsoleKey.D1;
                if (index < plugins.Count)
                {
                    int newFocus = plugins[index].WindowIndex;
                    vom.Set("\\Dwm\\FocusIndex", newFocus);
                    UpdateZIndices(vom, plugins, newFocus);
                }
            }
            else if (key.Key == ConsoleKey.UpArrow || key.Key == ConsoleKey.DownArrow ||
                     key.Key == ConsoleKey.LeftArrow || key.Key == ConsoleKey.RightArrow)
            {
                string layout = vom.Get<string>("\\Dwm\\Layout", "tile");
                if (layout.Equals("float", StringComparison.OrdinalIgnoreCase))
                {
                    int focusIdx = vom.Get<int>("\\Dwm\\FocusIndex", 0);
                    string basePath = $"\\Windows\\{focusIdx}";

                    int x = vom.Get<int>(basePath + "\\X", 0);
                    int y = vom.Get<int>(basePath + "\\Y", 0);
                    int w = vom.Get<int>(basePath + "\\Width", 10);
                    int h = vom.Get<int>(basePath + "\\Height", 5);

                    if (shift)
                    {
                        if (key.Key == ConsoleKey.UpArrow) h = Math.Max(5, h - 1);
                        else if (key.Key == ConsoleKey.DownArrow) h = h + 1;
                        else if (key.Key == ConsoleKey.LeftArrow) w = Math.Max(10, w - 1);
                        else if (key.Key == ConsoleKey.RightArrow) w = w + 1;

                        vom.Set(basePath + "\\Width", w);
                        vom.Set(basePath + "\\Height", h);
                    }
                    else
                    {
                        if (key.Key == ConsoleKey.UpArrow) y = y - 1;
                        else if (key.Key == ConsoleKey.DownArrow) y = y + 1;
                        else if (key.Key == ConsoleKey.LeftArrow) x = x - 2;
                        else if (key.Key == ConsoleKey.RightArrow) x = x + 2;

                        vom.Set(basePath + "\\X", x);
                        vom.Set(basePath + "\\Y", y);
                    }
                }
            }
            else
            {
                // Alt+key not claimed by compositor — pass to focused plugin (Alt+F in vim, etc.)
                plugins.FirstOrDefault(p => p.WindowIndex == focusIndex)?.InputQueue.Enqueue(key);
            }
        }
        else
        {
            // Normal keys are forwarded to the focused plugin's input queue
            plugins.FirstOrDefault(p => p.WindowIndex == focusIndex)?.InputQueue.Enqueue(key);
        }
    }

    private static void HandleMouseEvent(Vom vom, List<PluginInstance> plugins, MOUSE_EVENT_RECORD mouse)
    {
        int mx = mouse.dwMousePosition.X;
        int my = mouse.dwMousePosition.Y;
        bool leftPressed = (mouse.dwButtonState & 1) != 0;
        bool isMove = (mouse.dwEventFlags & 0x0001) != 0;

        string layout = vom.Get<string>("\\Dwm\\Layout", "float");
        bool isFloat = layout.Equals("float", StringComparison.OrdinalIgnoreCase);

        bool middlePressed = (mouse.dwButtonState & 0x0004) != 0; // MOUSE_BUTTON3 = wheel click

        // Middle-click drag to pan canvas
        if (middlePressed && isMove && _isPanning && isFloat)
        {
            int dx = mx - _panLastX;
            int dy = my - _panLastY;
            _panLastX = mx;
            _panLastY = my;
            int vpX = vom.Get<int>("\\Dwm\\ViewportX", 0);
            int vpY = vom.Get<int>("\\Dwm\\ViewportY", 0);
            vom.Set("\\Dwm\\ViewportX", vpX - dx);
            vom.Set("\\Dwm\\ViewportY", vpY - dy);
            return;
        }
        if (middlePressed && !_isPanning && isFloat)
        {
            _isPanning = true;
            _panLastX = mx;
            _panLastY = my;
            return;
        }
        if (!middlePressed && _isPanning)
        {
            _isPanning = false;
        }

        // Scroll wheel — pan viewport in float mode
                bool ctrl = (mouse.dwControlKeyState & 0x0008) != 0 || (mouse.dwControlKeyState & 0x0004) != 0;
        if ((mouse.dwEventFlags & 0x0004) != 0)
        {
            short delta = (short)(mouse.dwButtonState >> 16);
            if (ctrl) {
                int zoom = vom.Get<int>("\\Dwm\\ZoomLevel", 1);
                zoom += (delta > 0 ? -1 : 1); // Wheel up = zoom in, wheel down = zoom out
                vom.Set("\\Dwm\\ZoomLevel", Math.Clamp(zoom, 1, 6));
            } else if (isFloat) {
                int vpY = vom.Get<int>("\\Dwm\\ViewportY", 0);
                vom.Set("\\Dwm\\ViewportY", vpY + (delta > 0 ? -3 : 3));
            }
            return;
        }
        if ((mouse.dwEventFlags & 0x0008) != 0) // MOUSE_HWHEELED (horizontal)
        {
            if (isFloat)
            {
                short delta = (short)(mouse.dwButtonState >> 16);
                int vpX = vom.Get<int>("\\Dwm\\ViewportX", 0);
                vom.Set("\\Dwm\\ViewportX", Math.Max(0, vpX + (delta > 0 ? 3 : -3)));
            }
            return;
        }

        // Viewport offset — world pos = screen pos + viewport
        int viewX = isFloat ? vom.Get<int>("\\Dwm\\ViewportX", 0) : 0;
        int viewY = isFloat ? vom.Get<int>("\\Dwm\\ViewportY", 0) : 0;
        int worldMx = mx + viewX;
        int worldMy = my + viewY;

        // Status bar click — theme button
        if (leftPressed && !isMove)
        {
            int barY  = vom.Get<int>("\\Dwm\\ThemeBtnY", -1);
            int barX  = vom.Get<int>("\\Dwm\\ThemeBtnX", -1);
            int barW  = vom.Get<int>("\\Dwm\\ThemeBtnW", 0);
            if (barY >= 0 && my == barY && mx >= barX && mx < barX + barW)
            {
                int cur = vom.Get<int>("\\Dwm\\BgMode", 0);
                vom.Set("\\Dwm\\BgMode", (cur + 1) % Compositor.BgThemeCount);
                return;
            }
        }

        if (leftPressed && !isMove)
        {
            long now = Stopwatch.GetTimestamp();
            long elapsedMs = (now - _lastClickTime) * 1000 / Stopwatch.Frequency;
            bool isDoubleClick = elapsedMs < 450 && Math.Abs(mx - _lastClickX) < 3 && Math.Abs(my - _lastClickY) < 3;
            _lastClickTime = now;
            _lastClickX = mx;
            _lastClickY = my;

            // Check visible windows — hit test in world space
            bool handled = false;
            var sortedPlugins = plugins
                .Where(p => vom.Get<bool>($"{p.BasePath}\\Visible", false))
                .OrderByDescending(p => vom.Get<int>($"{p.BasePath}\\ZIndex", p.WindowIndex))
                .ToList();

            foreach (var p in sortedPlugins)
            {
                int wx = vom.Get<int>($"{p.BasePath}\\X", 0);
                int wy = vom.Get<int>($"{p.BasePath}\\Y", 0);
                int ww = vom.Get<int>($"{p.BasePath}\\Width", 10);
                int wh = vom.Get<int>($"{p.BasePath}\\Height", 5);

                if (worldMx >= wx && worldMx < wx + ww && worldMy >= wy && worldMy < wy + wh)
                {
                    int currentFocus = vom.Get<int>("\\Dwm\\FocusIndex", 0);
                    if (currentFocus != p.WindowIndex)
                    {
                        vom.Set($"\\Windows\\{currentFocus}\\Focused", false);
                        vom.Set($"\\Windows\\{p.WindowIndex}\\Focused", true);
                        vom.Set("\\Dwm\\FocusIndex", p.WindowIndex);
                        UpdateZIndices(vom, plugins, p.WindowIndex);
                    }

                    // Check for resize on outer borders (if float mode)
                    if (isFloat)
                    {
                        ResizeEdge hitEdges = ResizeEdge.None;
                        if (worldMx == wx) hitEdges |= ResizeEdge.Left;
                        if (worldMx == wx + ww - 1) hitEdges |= ResizeEdge.Right;
                        if (worldMy == wy) hitEdges |= ResizeEdge.Top;
                        if (worldMy == wy + wh - 1) hitEdges |= ResizeEdge.Bottom;

                        if (hitEdges != ResizeEdge.None)
                        {
                            _isResizing = true;
                            _resizeWindowIndex = p.WindowIndex;
                            _resizeEdges = hitEdges;
                            _resizeOriginalX = wx;
                            _resizeOriginalY = wy;
                            _resizeOriginalW = ww;
                            _resizeOriginalH = wh;
                            _resizeMouseStartX = mx;
                            _resizeMouseStartY = my;
                            handled = true;
                            break;
                        }
                    }

                    // Decoration buttons on title bar row (wy+1)
                    if (worldMy == wy + 1 && ww >= 10)
                    {
                        int closeX    = wx + ww - 2;
                        int minimizeX = wx + ww - 4; // Updated from -6 to -4
                        if (worldMx == closeX || worldMx == minimizeX)
                        {
                            vom.Set($"{p.BasePath}\\Visible", false);
                            handled = true;
                            break;
                        }
                    }

                    // Double-click either title row → minimize to icon
                    if (isDoubleClick && (worldMy == wy || worldMy == wy + 1))
                    {
                        vom.Set($"{p.BasePath}\\Visible", false);
                        handled = true;
                        break;
                    }

                    // Title bar drag — both title rows are draggable (bigger touch target)
                    if (isFloat && (worldMy == wy || worldMy == wy + 1))
                    {
                        _isDragging = true;
                        _dragOffsetIndex = p.WindowIndex;
                        _dragOffsetX = mx - (wx - viewX);
                        _dragOffsetY = my - (wy - viewY);
                    }
                    handled = true;
                    break;
                }
            }

            // If nothing was hit, check desktop icons (screen space — no viewport offset)
            if (!handled)
            {
                foreach (var p in plugins)
                {
                    if (vom.Get<bool>($"{p.BasePath}\\Visible", false)) continue;

                                        int zoom = vom.Get<int>("\\Dwm\\ZoomLevel", 1);
                    int ix = vom.Get<int>($"{p.BasePath}\\IconX", -1);
                    int iy = vom.Get<int>($"{p.BasePath}\\IconY", -1);
                    int iw = vom.Get<int>($"{p.BasePath}\\IconW", 0);
                    int ih = vom.Get<int>($"{p.BasePath}\\IconH", 0);
                    
                    int sx = (ix - viewX) / zoom;
                    int sy = (iy - viewY) / zoom;
                    int scaledW = Math.Max(4, iw / zoom);
                    int scaledH = Math.Max(2, ih / zoom);

                    if (ix >= 0 && mx >= sx && mx < sx + scaledW && my >= sy && my < sy + scaledH)
                    {
                        if (isDoubleClick)
                        {
                            vom.Set($"{p.BasePath}\\Visible", true);
                            vom.Set("\\Dwm\\FocusIndex", p.WindowIndex);
                            UpdateZIndices(vom, plugins, p.WindowIndex);
                        }
                        else
                        {
                            // Single click — start icon drag
                            _isDraggingIcon = true;
                            _draggingIconIndex = p.WindowIndex;
                            _iconDragOffsetX = mx - sx;
                            _iconDragOffsetY = my - sy;
                        }
                        break;
                    }
                }
            }
        }
        else if (isMove && _isResizing)
        {
            if (_resizeWindowIndex != -1)
            {
                int dx = mx - _resizeMouseStartX;
                int dy = my - _resizeMouseStartY;

                int newX = _resizeOriginalX;
                int newY = _resizeOriginalY;
                int newW = _resizeOriginalW;
                int newH = _resizeOriginalH;

                if ((_resizeEdges & ResizeEdge.Left) != 0)
                {
                    newX += dx;
                    newW -= dx;
                }
                else if ((_resizeEdges & ResizeEdge.Right) != 0)
                {
                    newW += dx;
                }

                if ((_resizeEdges & ResizeEdge.Top) != 0)
                {
                    newY += dy;
                    newH -= dy;
                }
                else if ((_resizeEdges & ResizeEdge.Bottom) != 0)
                {
                    newH += dy;
                }

                // Enforce minimum bounds
                if (newW < 10)
                {
                    if ((_resizeEdges & ResizeEdge.Left) != 0) newX -= (10 - newW); // Adjust X if left edge was pushed too far
                    newW = 10;
                }
                if (newH < 5)
                {
                    if ((_resizeEdges & ResizeEdge.Top) != 0) newY -= (5 - newH); // Adjust Y if top edge was pushed too far
                    newH = 5;
                }

                // Prevent going completely off-screen negative (optional, but good practice)
                newX = Math.Max(0, newX);
                newY = Math.Max(0, newY);

                string basePath = $"\\Windows\\{_resizeWindowIndex}";
                vom.Set(basePath + "\\TargetX", (float)newX);
                vom.Set(basePath + "\\TargetY", (float)newY);
                vom.Set(basePath + "\\TargetW", (float)newW);
                vom.Set(basePath + "\\TargetH", (float)newH);
            }
        }
        else if (isMove && _isDragging)
        {
            if (_dragOffsetIndex != -1)
            {
                int newX = Math.Max(0, mx - _dragOffsetX + viewX);
                int newY = Math.Max(0, my - _dragOffsetY + viewY);
                string basePath = $"\\Windows\\{_dragOffsetIndex}";
                vom.Set(basePath + "\\TargetX", (float)newX);
                vom.Set(basePath + "\\TargetY", (float)newY);
            }
        }
        else if (isMove && _isDraggingIcon)
        {
            if (_draggingIconIndex != -1)
            {
                string basePath = $"\\Windows\\{_draggingIconIndex}";
                int zoom = vom.Get<int>("\\Dwm\\ZoomLevel", 1);
                vom.Set(basePath + "\\IconX", (mx - _iconDragOffsetX) * zoom + viewX);
                vom.Set(basePath + "\\IconY", Math.Max(0, (my - _iconDragOffsetY) * zoom + viewY));
            }
        }
        else if (!leftPressed)
        {
            _isResizing = false;
            _resizeWindowIndex = -1;
            _resizeEdges = ResizeEdge.None;
            _isDragging = false;
            _dragOffsetIndex = -1;
            _isDraggingIcon = false;
            _draggingIconIndex = -1;
        }
    }

    private static void CycleFocus(Vom vom, List<PluginInstance> plugins, int dir)
    {
        if (plugins.Count == 0) return;
        int focusIndex = vom.Get<int>("\\Dwm\\FocusIndex", 0);
        int currentIdx = plugins.FindIndex(p => p.WindowIndex == focusIndex);
        if (currentIdx == -1) currentIdx = 0;

        int nextIdx = (currentIdx + dir + plugins.Count) % plugins.Count;
        int nextFocus = plugins[nextIdx].WindowIndex;
        
        // Remove focus flag from old, set to new
        vom.Set($"\\Windows\\{plugins[currentIdx].WindowIndex}\\Focused", false);
        vom.Set($"\\Windows\\{nextFocus}\\Focused", true);
        vom.Set("\\Dwm\\FocusIndex", nextFocus);

        UpdateZIndices(vom, plugins, nextFocus);
    }

    private static void UpdateZIndices(Vom vom, List<PluginInstance> plugins, int focusIndex)
    {
        for (int i = 0; i < plugins.Count; i++)
        {
            int winIdx = plugins[i].WindowIndex;
            if (winIdx == focusIndex)
            {
                vom.Set($"\\Windows\\{winIdx}\\ZIndex", 1000); // Focused window on top
            }
            else
            {
                vom.Set($"\\Windows\\{winIdx}\\ZIndex", winIdx);
            }
        }
    }

    private static void SpawnPlugin(Vom vom, List<PluginInstance> plugins, string targetPluginName)
    {
        var templateInstance = plugins.FirstOrDefault(p => p.Plugin.GetType().Name == targetPluginName);
        if (templateInstance == null) return;
        
        var newPlugin = (ITuiPlugin)Activator.CreateInstance(templateInstance.Plugin.GetType())!;
        int newIndex = plugins.Count > 0 ? plugins.Max(p => p.WindowIndex) + 1 : 0;
        
        var instance = new PluginInstance(newPlugin, vom, newIndex);
        
        string basePath = instance.BasePath;
        var template = newPlugin.GetTemplate();
        vom.Set($"{basePath}\\PluginName", template.PluginName);
        foreach (var entry in template.Entries)
            vom.Set(basePath + entry.KeySuffix, entry.DefaultValue);
            
        vom.Set($"{basePath}\\Visible", true);
        
        // SPAWN ANIMATION: Start deep in the abyss
        vom.Set($"{basePath}\\Z", -1000.0f);
        vom.Set($"{basePath}\\TargetZ", 0.1f);
        
        vom.Set($"{basePath}\\X", 10 + (newIndex % 5) * 4);
        vom.Set($"{basePath}\\Y", 5 + (newIndex % 5) * 2);
        
        plugins.Add(instance);
        instance.Start();
        
        vom.Set("\\Dwm\\FocusIndex", newIndex);
        UpdateZIndices(vom, plugins, newIndex);
    }

    private static void CloseWindow(Vom vom, List<PluginInstance> plugins, int windowIndex)
    {
        var instance = plugins.FirstOrDefault(p => p.WindowIndex == windowIndex);
        if (instance != null && instance.State != LifecycleState.Exiting)
        {
            // EXIT ANIMATION: Throw it off a bridge
            instance.State = LifecycleState.Exiting;
            vom.Set($"{instance.BasePath}\\TargetY", -1000.0f); // Gravity
            vom.Set($"{instance.BasePath}\\TargetZ", -2000.0f); // Abyss
            vom.Set($"{instance.BasePath}\\TargetW", 0.0f);     // Shrink
            vom.Set($"{instance.BasePath}\\TargetH", 0.0f);     // Shrink
            
            // Immediately update focus so we don't interact with it
            var remaining = plugins.Where(p => p.State != LifecycleState.Exiting).ToList();
            if (remaining.Count > 0)
            {
                int newFocus = remaining.Last().WindowIndex;
                vom.Set("\\Dwm\\FocusIndex", newFocus);
                UpdateZIndices(vom, remaining, newFocus);
            }
        }
    }
    private static void Shutdown()
    {
        lock (_shutdownLock)
        {
            if (_cleanedUp) return;
            _cleanedUp = true;

            // Stop all plugin threads
            foreach (var instance in _pluginInstances)
            {
                instance.Stop();
            }

            // Restore terminal state
            _compositor?.Shutdown();
            Console.CursorVisible = true;
        }
    }
}
