using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using TuiDwm.Core;

namespace TuiDwm.Engine;

public class MenuAdapter : IDisposable
{
    private readonly Vom _vom;
    private readonly Thread _workerThread;
    private volatile bool _running;
    private EventWaitHandle? _pushEvent;
    private EventWaitHandle? _resultEvent;
    private MemoryMappedFile? _mmf;

    public MenuAdapter(Vom vom)
    {
        _vom = vom;
        _workerThread = new Thread(ListenLoop)
        {
            IsBackground = true,
            Name = "Win32-MenuAdapter"
        };
    }

    public void Start()
    {
        _running = true;
        _workerThread.Start();
    }

    public void Stop()
    {
        _running = false;
        try { _pushEvent?.Set(); } catch { } // Wake up loop if blocked
    }

    private void ListenLoop()
    {
        // Wait for the Explorer Extension to create the named objects.
        // It might not exist immediately on startup.
        while (_running)
        {
            try
            {
#pragma warning disable CA1416
                // Try to open existing handles mapped by the Explorer Hook DLL
                if (EventWaitHandle.TryOpenExisting(@"Global\TuiDwm_MenuPush", out _pushEvent) &&
                    EventWaitHandle.TryOpenExisting(@"Global\TuiDwm_MenuResult", out _resultEvent))
                {
                    try
                    {
                        _mmf = MemoryMappedFile.OpenExisting(@"Global\TuiDwm_MenuMemory", MemoryMappedFileRights.Read);
                    }
                    catch { }
                }
#pragma warning restore CA1416

                if (_pushEvent != null && _resultEvent != null && _mmf != null)
                {
                    break; // All objects mapped successfully
                }
            }
            catch
            {
                // Extension hasn't fired yet or no permissions. Wait and retry.
            }

            if (!_running) return;
            Thread.Sleep(1000);
        }

        while (_running)
        {
            if (_pushEvent != null && _pushEvent.WaitOne())
            {
                if (!_running) break;

                try
                {
                    ProcessMenuPayload();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MenuAdapter] Error parsing payload: {ex.Message}");
                }
                finally
                {
                    // Always signal Explorer to resume so we don't hang the OS UI thread!
                    _resultEvent?.Set();
                }
            }
        }
    }

    private void ProcessMenuPayload()
    {
        if (_mmf == null) return;
        using var accessor = _mmf.CreateViewAccessor(0, Marshal.SizeOf<MenuPayload>(), MemoryMappedFileAccess.Read);
        accessor.Read(0, out MenuPayload payload);

        Console.WriteLine($"[MenuAdapter] Intercepted Explorer Context Menu! Items: {payload.ItemCount} at ({payload.CursorX}, {payload.CursorY})");

        // Clear previous menus
        int oldCount = _vom.Get<int>("\\Windows\\ContextMenus\\Count", 0);
        for (int i = 0; i < oldCount; i++)
        {
            _vom.Delete($"\\Windows\\ContextMenus\\{i}");
        }

        // We map Win32 screen coords roughly to terminal units.
        // Assuming ~10px per col, ~20px per row.
        float startX = payload.CursorX / 10.0f;
        float startY = payload.CursorY / 20.0f;

        int menuItems = Math.Min(payload.ItemCount, 256);
        for (int i = 0; i < menuItems; i++)
        {
            string basePath = $"\\Windows\\ContextMenus\\{i}";
            var el = payload.Elements[i];

            // 1. Start them completely collapsed at the cursor
            _vom.Set($"{basePath}\\X", startX);
            _vom.Set($"{basePath}\\Y", startY);
            _vom.Set($"{basePath}\\Width", 0.1f);
            _vom.Set($"{basePath}\\Height", 0.1f);

            // 2. Animate them expanding and cascading down
            // 3D rotation twist can be added here if we expose TargetRotation to QuadElement later!
            _vom.Set($"{basePath}\\TargetX", startX);
            _vom.Set($"{basePath}\\TargetY", startY + (i * 2.5f)); // Cascade down vertically
            _vom.Set($"{basePath}\\TargetW", 25.0f);
            _vom.Set($"{basePath}\\TargetH", 2.0f);
            
            // 3. Set ElementType = 2 (Context Menu / Frosted Glass)
            _vom.Set($"{basePath}\\ElementType", 2.0f);
            
            // 4. Set Label for text rendering (if handled by a UI plugin later)
            _vom.Set($"{basePath}\\Label", el.Label);
        }

        _vom.Set("\\Windows\\ContextMenus\\Count", menuItems);
    }

    public void Dispose()
    {
        Stop();
        _pushEvent?.Dispose();
        _resultEvent?.Dispose();
        _mmf?.Dispose();
    }
}
