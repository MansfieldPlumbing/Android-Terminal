using System;
using System.Collections.Generic;
using TuiDwm.Core;

namespace TuiDwm.Port;

/// <summary>
/// Handles coordinate mapping and hit target resolution.
/// Properly translates screen-space mouse/touch coords [0.0 - 1.0] to
/// depth-skewed, aspect-ratio corrected local element coordinate spaces.
/// </summary>
public sealed class WindowPhysics
{
    private readonly Vom _vom;
    private readonly List<PluginInstance> _plugins;
    private readonly QuadHitboxResolver _hitboxResolver = new();

    public WindowPhysics(Vom vom, List<PluginInstance> plugins)
    {
        _vom = vom;
        _plugins = plugins;
    }

    /// <summary>
    /// Translates global screen coordinates (x, y) into the local coordinate system of a window element,
    /// accounting for scale, depth (Z-axis perspective shift), viewport translation, and rotation.
    /// </summary>
    public float2 MapLocalCoordinates(string basePath, float x, float y, float screenW, float screenH, float cameraX = 0f, float cameraY = 0f)
    {
        float aspect = screenW / screenH;
        float colNorm = 1.0f / screenW;
        float rowNorm = 1.0f / screenH;

        float wx = _vom.Get<float>($"{basePath}\\X", 0f) * colNorm;
        float wy = _vom.Get<float>($"{basePath}\\Y", 0f) * rowNorm;
        float ww = _vom.Get<float>($"{basePath}\\Width", 0f) * colNorm;
        float wh = _vom.Get<float>($"{basePath}\\Height", 0f) * rowNorm;
        float zDepth = _vom.Get<float>($"{basePath}\\Z", 0f);
        float rotation = _vom.Get<float>($"{basePath}\\Rotation", 0f);

        float worldCx = wx + ww * 0.5f;
        float worldCy = wy + wh * 0.5f;

        float zScale = 1.0f + (zDepth * 2.0f);
        float vpX = 0.5f;
        float vpY = 0.5f;

        // Apply perspective projection & camera viewport translation
        float screenCx = vpX + (worldCx - vpX) / zScale - (cameraX / zScale);
        float screenCy = vpY + (worldCy - vpY) / zScale - (cameraY / zScale);

        float px = x - screenCx;
        float py = y - screenCy;

        px *= zScale;
        py *= zScale;

        // Correct for rotation
        if (rotation != 0.0f)
        {
            px *= aspect;
            float angle = -rotation;
            float s = MathF.Sin(angle);
            float c = MathF.Cos(angle);

            float rotX = px * c - py * s;
            float rotY = px * s + py * c;

            px = rotX / aspect;
            py = rotY;
        }

        return new float2(px, py);
    }

    /// <summary>
    /// Traverses the state fabric descending from closest to furthest, performing
    /// bounds-checks to resolve the exact element and region targeted by a pointer contact.
    /// </summary>
    public HitResult? ResolveTargetElementAndRegion(float x, float y, float screenW, float screenH, bool isTouch, float cameraX = 0f, float cameraY = 0f)
    {
        // 1. Gather all active visual quads
        var quads = new List<(string BasePath, int WinIdx, float ZDepth, int ZIndex, float ElementType)>(32);
        
        for (int i = 0; i < _plugins.Count; i++)
        {
            var p = _plugins[i];
            if (_vom.Get<bool>($"{p.BasePath}\\Visible", true))
            {
                quads.Add((
                    p.BasePath,
                    p.WindowIndex,
                    _vom.Get<float>($"{p.BasePath}\\Z", 0f),
                    _vom.Get<int>($"{p.BasePath}\\ZIndex", p.WindowIndex),
                    2.0f // ElementType Window
                ));
            }
        }

        // Add Context Menus / Overlays (they sit in \Windows\ContextMenus\{idx})
        int contextMenuCount = _vom.Get<int>("\\Windows\\ContextMenus\\Count", 0);
        for (int i = 0; i < contextMenuCount; i++)
        {
            string menuPath = $"\\Windows\\ContextMenus\\{i}";
            quads.Add((
                menuPath,
                -2, // Non-plugin element
                0.05f - (i * 0.001f),
                100000 + i, // High priority
                _vom.Get<float>($"{menuPath}\\ElementType", 1.0f)
            ));
        }

        // 2. Sort by depth (closest first) using Painter's order in reverse
        quads.Sort((a, b) =>
        {
            if (Math.Abs(a.ZDepth - b.ZDepth) > 0.001f)
            {
                return a.ZDepth.CompareTo(b.ZDepth); // smallest Z first (closest)
            }
            return b.ZIndex.CompareTo(a.ZIndex); // largest Z-Index first
        });

        // 3. Evaluate hit testing
        for (int i = 0; i < quads.Count; i++)
        {
            var quad = quads[i];
            
            // System-level overlays ignore viewport pan
            float cx = (quad.WinIdx == -2) ? 0f : cameraX;
            float cy = (quad.WinIdx == -2) ? 0f : cameraY;

            float2 local = MapLocalCoordinates(quad.BasePath, x, y, screenW, screenH, cx, cy);

            float colNorm = 1.0f / screenW;
            float rowNorm = 1.0f / screenH;
            float w = _vom.Get<float>($"{quad.BasePath}\\Width", 0f) * colNorm;
            float h = _vom.Get<float>($"{quad.BasePath}\\Height", 0f) * rowNorm;

            if (Math.Abs(local.x) <= w * 0.5f && Math.Abs(local.y) <= h * 0.5f)
            {
                HitRegion region = _hitboxResolver.ResolveHitRegion(
                    _vom, 
                    quad.BasePath, 
                    quad.ElementType, 
                    local.x, 
                    local.y, 
                    screenW, 
                    screenH, 
                    isTouch
                );

                if (region != HitRegion.None)
                {
                    return new HitResult
                    {
                        WindowIndex = quad.WinIdx,
                        BasePath = quad.BasePath,
                        Region = region
                    };
                }
            }
        }

        return null;
    }
}
