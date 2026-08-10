using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using TuiDwm.Core;

namespace TuiDwm.Port;

public struct PluginPathCache
{
    public string Visible;
    public string ZIndex;
    public string X;
    public string Y;
    public string Width;
    public string Height;
    public string Z;
    public string Rotation;

    public PluginPathCache(string basePath)
    {
        Visible = $"{basePath}\\Visible";
        ZIndex = $"{basePath}\\ZIndex";
        X = $"{basePath}\\X";
        Y = $"{basePath}\\Y";
        Width = $"{basePath}\\Width";
        Height = $"{basePath}\\Height";
        Z = $"{basePath}\\Z";
        Rotation = $"{basePath}\\Rotation";
    }
}

public struct ContextMenuPathCache
{
    public string X;
    public string Y;
    public string Width;
    public string Height;
    public string Rotation;
    public string ElementType;

    public ContextMenuPathCache(string basePath)
    {
        X = $"{basePath}\\X";
        Y = $"{basePath}\\Y";
        Width = $"{basePath}\\Width";
        Height = $"{basePath}\\Height";
        Rotation = $"{basePath}\\Rotation";
        ElementType = $"{basePath}\\ElementType";
    }
}

/// <summary>
/// Core Hardware-Accelerated Frame Compositor.
/// Coordinates rendering of active application windows, backgrounds,
/// and contextual overlays directly into the instanced GPU State Fabric.
/// Optimized for zero per-frame garbage collector allocations.
/// </summary>
public sealed class Compositor
{
    private readonly Vom _vom;
    private readonly List<PluginInstance> _plugins;
    private readonly D3D12Renderer _hardwareRenderer;

    // Cache list to prevent per-frame garbage collector allocations
    private readonly List<UiElementData> _renderQueue = new(256);
    private readonly List<PluginInstance> _activePlugins = new(16);

    // Path caches to completely eliminate per-frame string allocations
    private readonly PluginPathCache[] _pluginPathCaches = new PluginPathCache[128];
    private readonly ContextMenuPathCache[] _contextMenuPathCaches = new ContextMenuPathCache[32];

    public Compositor(Vom vom, List<PluginInstance> plugins, D3D12Renderer d3dRenderer)
    {
        _vom = vom;
        _plugins = plugins;
        _hardwareRenderer = d3dRenderer;

        for (int i = 0; i < 128; i++)
        {
            _pluginPathCaches[i] = new PluginPathCache($"\\Windows\\{i}");
        }

        for (int i = 0; i < 32; i++)
        {
            _contextMenuPathCaches[i] = new ContextMenuPathCache($"\\Windows\\ContextMenus\\{i}");
        }
    }

    private PluginPathCache GetPluginPath(int windowIndex)
    {
        if (windowIndex >= 0 && windowIndex < 128)
        {
            return _pluginPathCaches[windowIndex];
        }
        return new PluginPathCache($"\\Windows\\{windowIndex}");
    }

    private ContextMenuPathCache GetContextMenuPath(int index)
    {
        if (index >= 0 && index < 32)
        {
            return _contextMenuPathCaches[index];
        }
        return new ContextMenuPathCache($"\\Windows\\ContextMenus\\{index}");
    }

    public void Initialize()
    {
        // Hydrate initial layout state into VOM
        int width = _vom.Get<int>("\\Dwm\\Cols", 1280);
        int height = _vom.Get<int>("\\Dwm\\Rows", 800);
        _vom.Set("\\Dwm\\Cols", width);
        _vom.Set("\\Dwm\\Rows", height);
    }

    public void Composite(float deltaTime, Dictionary<int, SkiaSharp.SKBitmap>? windowBitmaps = null)
    {
        _renderQueue.Clear();
        _activePlugins.Clear();

        int screenW = _vom.Get<int>("\\Dwm\\Cols", 1280);
        int screenH = _vom.Get<int>("\\Dwm\\Rows", 800);
        float colNorm = 1.0f / screenW;
        float rowNorm = 1.0f / screenH;

        // 1. Gather visible plugins
        for (int i = 0; i < _plugins.Count; i++)
        {
            var p = _plugins[i];
            var path = GetPluginPath(p.WindowIndex);
            if (_vom.Get<bool>(path.Visible, true))
            {
                _activePlugins.Add(p);
            }
        }

        // Sort active plugins descending by ZIndex (painter's algorithm)
        _activePlugins.Sort((a, b) => {
            var pathA = GetPluginPath(a.WindowIndex);
            var pathB = GetPluginPath(b.WindowIndex);
            int az = _vom.Get<int>(pathA.ZIndex, a.WindowIndex);
            int bz = _vom.Get<int>(pathB.ZIndex, b.WindowIndex);
            return az.CompareTo(bz);
        });

        // 2. Map Wallpaper/Background plate (Layer 0)
        _renderQueue.Add(new UiElementData
        {
            Position = float2.Zero,
            Size = float2.One,
            Color = float4.Zero,
            ZDepth = 1.0f,
            Rotation = 0.0f,
            ElementType = 6.0f, // Wallpaper
            IsActive = 1.0f,
            ColorId = 0.0f,
            TexBlend = 0.0f
        });

        // 3. Map Taskbar/Dock (Layer 3)
        bool hasTaskbar = _vom.Get<bool>("\\Dwm\\Taskbar\\Visible", true);
        if (hasTaskbar)
        {
            float tbY = _vom.Get<float>("\\Dwm\\Taskbar\\Y", 0.92f);
            float tbH = _vom.Get<float>("\\Dwm\\Taskbar\\Height", 0.08f);
            _renderQueue.Add(new UiElementData
            {
                Position = new float2(0.0f, tbY),
                Size = new float2(1.0f, tbH),
                Color = new float4(0.1f, 0.1f, 0.12f, 0.85f),
                ZDepth = 0.0f,
                Rotation = 0.0f,
                ElementType = 3.0f, // Taskbar Top Bevel
                IsActive = 1.0f,
                ColorId = 0.0f,
                TexBlend = 0.0f
            });
        }

        // 4. Map active application windows (Layer 2)
        for (int i = 0; i < _activePlugins.Count; i++)
        {
            var p = _activePlugins[i];
            var path = GetPluginPath(p.WindowIndex);
            float wx = _vom.Get<float>(path.X, 0f);
            float wy = _vom.Get<float>(path.Y, 0f);
            float ww = _vom.Get<float>(path.Width, 300f);
            float wh = _vom.Get<float>(path.Height, 200f);
            float zDepth = _vom.Get<float>(path.Z, 0.1f);
            float rot = _vom.Get<float>(path.Rotation, 0f);

            _renderQueue.Add(new UiElementData
            {
                Position = new float2(wx * colNorm, wy * rowNorm),
                Size = new float2(ww * colNorm, wh * rowNorm),
                Color = new float4(0.08f, 0.082f, 0.09f, 0.95f),
                ZDepth = zDepth,
                Rotation = rot,
                ElementType = 2.0f, // Window
                IsActive = 1.0f,
                ColorId = p.WindowIndex,     // sample appletTexture at slice p.WindowIndex
                TexBlend = 1.0f
            });
        }

        // 5. Map Contextual Menus & Overlays (Layer 1)
        int contextMenuCount = _vom.Get<int>("\\Windows\\ContextMenus\\Count", 0);
        for (int i = 0; i < contextMenuCount; i++)
        {
            var menuPath = GetContextMenuPath(i);
            float mx = _vom.Get<float>(menuPath.X, 0f);
            float my = _vom.Get<float>(menuPath.Y, 0f);
            float mw = _vom.Get<float>(menuPath.Width, 200f);
            float mh = _vom.Get<float>(menuPath.Height, 160f);
            float rot = _vom.Get<float>(menuPath.Rotation, 0f);
            float elemType = _vom.Get<float>(menuPath.ElementType, 1.0f);

            _renderQueue.Add(new UiElementData
            {
                Position = new float2(mx * colNorm, my * rowNorm),
                Size = new float2(mw * colNorm, mh * rowNorm),
                Color = new float4(0.12f, 0.12f, 0.14f, 0.98f),
                ZDepth = 0.05f - (i * 0.001f), // Keep context menu stacked above standard windows
                Rotation = rot,
                ElementType = elemType,
                IsActive = 1.0f,
                ColorId = 0.0f,
                TexBlend = 0.0f
            });
        }

        // 6. Submit render command pass to D3D12 device
        Span<UiElementData> elementSpan = CollectionsMarshal.AsSpan(_renderQueue);
        _hardwareRenderer.Render(elementSpan, deltaTime, windowBitmaps);
    }
}

// Minimal vector primitives to keep types fully self-contained & fast
public struct float2
{
    public float x, y;
    public float2(float val) { x = val; y = val; }
    public float2(float vx, float vy) { x = vx; y = vy; }
    public static readonly float2 Zero = new float2(0f, 0f);
    public static readonly float2 One = new float2(1f, 1f);
}

public struct float4
{
    public float r, g, b, a;
    public float4(float vr, float vg, float vb, float va) { r = vr; g = vg; b = vb; a = va; }
    public static readonly float4 Zero = new float4(0f, 0f, 0f, 0f);
}
