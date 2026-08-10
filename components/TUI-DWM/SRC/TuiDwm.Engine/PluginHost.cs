using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using TuiDwm.Core;

namespace TuiDwm.Engine;

public enum LifecycleState { Spawning, Active, Exiting }

public class PluginInstance
{
    public LifecycleState State { get; set; } = LifecycleState.Spawning;
    public ITuiPlugin Plugin { get; }
    public string BasePath { get; }
    public CellBuffer FrontBuffer { get; private set; }
    private CellBuffer _backBuffer;
    public readonly object BufferLock = new();
    public ConcurrentQueue<ConsoleKeyInfo> InputQueue { get; } = new();
    public Thread Thread { get; private set; } = null!;
    public volatile bool IsRunning;
    public Vom Vom { get; }
    public int WindowIndex { get; }

    public PluginInstance(ITuiPlugin plugin, Vom vom, int windowIndex)
    {
        Plugin = plugin;
        Vom = vom;
        WindowIndex = windowIndex;
        BasePath = $"\\Windows\\{windowIndex}";
        
        // Setup initial default buffers
        FrontBuffer = new CellBuffer(10, 5);
        _backBuffer = new CellBuffer(10, 5);
    }

    public void Start()
    {
        IsRunning = true;
        Thread = new Thread(Run)
        {
            Name = $"Plugin-{WindowIndex}-{Plugin.GetType().Name}",
            IsBackground = true
        };
        Thread.Start();
    }

    public void Stop()
    {
        IsRunning = false;
        if (Thread != null && Thread.IsAlive)
        {
            Thread.Join(500);
        }
    }

    private void Run()
    {
        // First initialize in the thread context
        Plugin.Initialize(Vom, BasePath);

        // Fetch initial window coordinates from VOM (3 rows overhead: top border + title bar + bottom border)
        int initW = Vom.Get<int>($"{BasePath}\\Width", 12) - 2;
        int initH = Vom.Get<int>($"{BasePath}\\Height", 7) - 3;
        initW = Math.Max(1, initW);
        initH = Math.Max(1, initH);

        lock (BufferLock)
        {
            FrontBuffer.Resize(initW, initH);
            _backBuffer.Resize(initW, initH);
        }
        Plugin.OnResize(initW, initH);

        while (IsRunning)
        {
            try
            {
                // Dequeue and process key events on the plugin thread
                while (InputQueue.TryDequeue(out var key))
                {
                    Plugin.HandleInput(key);
                }

                // Query current size from VOM (compositor writes to it during layouts)
                int targetW = Vom.Get<int>($"{BasePath}\\Width", 12) - 2;
                int targetH = Vom.Get<int>($"{BasePath}\\Height", 7) - 3;
                targetW = Math.Max(1, targetW);
                targetH = Math.Max(1, targetH);

                if (_backBuffer.Width != targetW || _backBuffer.Height != targetH)
                {
                    _backBuffer.Resize(targetW, targetH);
                    lock (BufferLock)
                    {
                        FrontBuffer.Resize(targetW, targetH);
                    }
                    Plugin.OnResize(targetW, targetH);
                }

                // Render into back buffer
                Plugin.Render(_backBuffer);

                // Swap buffers under lock
                lock (BufferLock)
                {
                    var temp = FrontBuffer;
                    FrontBuffer = _backBuffer;
                    _backBuffer = temp;
                }

                // Throttle to prevent pegging the CPU (up to ~1000 FPS max)
                Thread.Sleep(1);
            }
            catch (Exception)
            {
                Thread.Sleep(100); // Backoff on exception
            }
        }
    }
}

public static class PluginLoader
{
    public static List<ITuiPlugin> LoadPlugins(string pluginsDir)
    {
        var list = new List<ITuiPlugin>();
        if (!Directory.Exists(pluginsDir))
        {
            Directory.CreateDirectory(pluginsDir);
            return list;
        }

        var files = Directory.GetFiles(pluginsDir, "*.dll");
        foreach (var file in files)
        {
            try
            {
                var alc = new AssemblyLoadContext("PluginContext_" + Path.GetFileNameWithoutExtension(file), isCollectible: true);
                var asm = alc.LoadFromAssemblyPath(Path.GetFullPath(file));
                var types = asm.GetTypes()
                    .Where(t => typeof(ITuiPlugin).IsAssignableFrom(t) && !t.IsAbstract);

                foreach (var type in types)
                {
                    if (Activator.CreateInstance(type) is ITuiPlugin plugin)
                    {
                        list.Add(plugin);
                    }
                }
            }
            catch (Exception ex)
            {
                // At startup, print warning, but keep moving
                Console.WriteLine($"[WARN] Could not load plugin assembly {file}: {ex.Message}");
            }
        }

        return list;
    }
}
