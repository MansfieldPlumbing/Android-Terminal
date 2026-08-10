using System;
using System.Collections.Generic;
using TuiDwm.Core;

namespace TuiDwm.Port;

public struct SnapZoneCache
{
    public int GroupId;
    public float Cx, Cy, Cw, Ch; // Normalized chooser button boundary coordinates
    public float Gx, Gy, Gw, Gh; // Normalized layout destination grid coordinates
    public string BasePath;
}

public struct SnapAssistCandidate
{
    public int WindowIndex;
    public string BasePath;
    public float OrigX, OrigY, OrigW, OrigH;
    public int OrigZIndex;
    public float TargetX, TargetY, TargetW, TargetH;
}

/// <summary>
/// Manages window snap layouts, snap assistance grids, and holographic previews.
/// </summary>
public sealed class SnapManager
{
    private readonly Vom _vom;
    private readonly List<PluginInstance> _plugins;

    public bool WasInZone { get; private set; }
    public List<SnapZoneCache> CachedSnapZones { get; } = new();
    public List<SnapAssistCandidate> SnapAssistCandidates { get; } = new();
    
    public SnapZoneCache? LastHoveredSnapWidget { get; private set; }
    public SnapZoneCache? ActiveSnapSlot { get; private set; }

    public SnapManager(Vom vom, List<PluginInstance> plugins)
    {
        _vom = vom;
        _plugins = plugins;
    }

    /// <summary>
    /// Allocates and spawns a dynamic snap layout preview panel on the target edge of the screen.
    /// Supports splitting into halves, thirds, or quadrants.
    /// </summary>
    public void AllocateSnapGroup(string edge, float screenW, float screenH)
    {
        if (WasInZone) return;
        WasInZone = true;
        CachedSnapZones.Clear();

        float aspect = screenW / screenH;
        bool isPortrait = aspect < 1.0f;
        bool isVertical = edge == "left" || edge == "right";

        // Precalculate scale sizes
        float pW = isPortrait ? (isVertical ? 0.07f : 0.42f) : (isVertical ? 0.12f : 0.35f);
        float pH = isPortrait ? (isVertical ? 0.42f : 0.07f) : (isVertical ? 0.35f : 0.12f);

        float pX = 0.5f - pW * 0.5f;
        float pY = 0.015f;
        float spawnX = pX;
        float spawnY = pY;

        if (edge == "bottom") { pY = 1.0f - 0.015f - pH; spawnY = 1.0f; }
        else if (edge == "top") { spawnY = -pH; }
        else if (edge == "left") { pX = 0.015f; pY = 0.5f - pH * 0.5f; spawnX = -pW; }
        else if (edge == "right") { pX = 1.0f - 0.015f - pW; pY = 0.5f - pH * 0.5f; spawnX = 1.0f; }

        // Setup base holographic panel metadata in VOM registry
        string basePath = "\\Windows\\System\\SnapZoneChooser\\Base";
        _vom.Set($"{basePath}\\X", spawnX);
        _vom.Set($"{basePath}\\Y", spawnY);
        _vom.Set($"{basePath}\\Width", pW);
        _vom.Set($"{basePath}\\Height", pH);
        _vom.Set($"{basePath}\\Visible", true);
        _vom.Set($"{basePath}\\TargetX", pX);
        _vom.Set($"{basePath}\\TargetY", pY);
        _vom.Set($"{basePath}\\ElementType", 7.0f); // Chooser Root Dashboard

        float gap = 0.015f;
        var groups = new List<List<LayoutBlock>>();

        if (isPortrait)
        {
            float h3 = (1.0f - gap * 4.0f) / 3.0f;
            groups.Add(new()
            {
                new() { Lw = 0.8f, Lh = 0.26f, Lx = 0.1f, Ly = 0.05f, Gx = gap, Gy = gap, Gw = 1.0f - gap * 2.0f, Gh = h3 },
                new() { Lw = 0.8f, Lh = 0.26f, Lx = 0.1f, Ly = 0.37f, Gx = gap, Gy = gap * 2.0f + h3, Gw = 1.0f - gap * 2.0f, Gh = h3 },
                new() { Lw = 0.8f, Lh = 0.26f, Lx = 0.1f, Ly = 0.69f, Gx = gap, Gy = gap * 3.0f + h3 * 2.0f, Gw = 1.0f - gap * 2.0f, Gh = h3 }
            });
            groups.Add(new()
            {
                new() { Lw = 0.8f, Lh = 0.45f, Lx = 0.1f, Ly = 0.05f, Gx = gap, Gy = gap, Gw = 1.0f - gap * 2.0f, Gh = 0.5f - gap * 1.5f },
                new() { Lw = 0.8f, Lh = 0.45f, Lx = 0.1f, Ly = 0.55f, Gx = gap, Gy = 0.5f + gap * 0.5f, Gw = 1.0f - gap * 2.0f, Gh = 0.5f - gap * 1.5f }
            });
        }
        else
        {
            // Landscape layout blocks
            groups.Add(new()
            {
                new() { Lw = 0.45f, Lh = 0.9f, Lx = 0.05f, Ly = 0.05f, Gx = gap, Gy = gap, Gw = 0.5f - gap * 1.5f, Gh = 1.0f - gap * 2.0f },
                new() { Lw = 0.45f, Lh = 0.9f, Lx = 0.55f, Ly = 0.05f, Gx = 0.5f + gap * 0.5f, Gy = gap, Gw = 0.5f - gap * 1.5f, Gh = 1.0f - gap * 2.0f }
            });
            float w3 = (1.0f - gap * 4.0f) / 3.0f;
            groups.Add(new()
            {
                new() { Lw = 0.28f, Lh = 0.9f, Lx = 0.05f, Ly = 0.05f, Gx = gap, Gy = gap, Gw = w3, Gh = 1.0f - gap * 2.0f },
                new() { Lw = 0.28f, Lh = 0.9f, Lx = 0.38f, Ly = 0.05f, Gx = gap * 2.0f + w3, Gy = gap, Gw = w3, Gh = 1.0f - gap * 2.0f },
                new() { Lw = 0.28f, Lh = 0.9f, Lx = 0.71f, Ly = 0.05f, Gx = gap * 3.0f + w3 * 2.0f, Gy = gap, Gw = w3, Gh = 1.0f - gap * 2.0f }
            });
        }

        float iconScaleX = isPortrait ? (isVertical ? 0.05f : 0.10f) : (isVertical ? 0.08f : 0.06f);
        float iconScaleY = isPortrait ? (isVertical ? 0.10f : 0.05f) : (isVertical ? 0.06f : 0.08f);
        float spacingX = 0.03f;
        float spacingY = 0.03f;

        float iconOffsetX = pX + (pW - (isVertical ? iconScaleX : (groups.Count * iconScaleX + (groups.Count - 1) * spacingX))) * 0.5f;
        float iconOffsetY = pY + (pH - (isVertical ? (groups.Count * iconScaleY + (groups.Count - 1) * spacingY) : iconScaleY)) * 0.5f;

        int partIdx = 900;
        for (int i = 0; i < groups.Count; i++)
        {
            float cgX = isVertical ? iconOffsetX : iconOffsetX + i * (iconScaleX + spacingX);
            float cgY = isVertical ? iconOffsetY + i * (iconScaleY + spacingY) : iconOffsetY;

            for (int b = 0; b < groups[i].Count; b++)
            {
                var block = groups[i][b];
                float cx = cgX + block.Lx * iconScaleX;
                float cy = cgY + block.Ly * iconScaleY;
                float cw = block.Lw * iconScaleX;
                float ch = block.Lh * iconScaleY;

                string partPath = $"\\Windows\\System\\SnapZoneChooser\\Parts\\{partIdx}";
                _vom.Set($"{partPath}\\X", cx + (spawnX - pX));
                _vom.Set($"{partPath}\\Y", cy + (spawnY - pY));
                _vom.Set($"{partPath}\\Width", cw);
                _vom.Set($"{partPath}\\Height", ch);
                _vom.Set($"{partPath}\\Visible", true);
                _vom.Set($"{partPath}\\TargetX", cx);
                _vom.Set($"{partPath}\\TargetY", cy);
                _vom.Set($"{partPath}\\ElementType", 4.0f); // Inactive Snap Zone part

                CachedSnapZones.Add(new SnapZoneCache
                {
                    GroupId = i,
                    Cx = cx, Cy = cy, Cw = cw, Ch = ch,
                    Gx = block.Gx, Gy = block.Gy, Gw = block.Gw, Gh = block.Gh,
                    BasePath = partPath
                });
                partIdx++;
            }
        }

        // Setup the holographic snap guide target (Mica/Glass preview element)
        string slotPath = "\\Windows\\System\\SnapZoneChooser\\SnapSlot";
        _vom.Set($"{slotPath}\\X", 0f);
        _vom.Set($"{slotPath}\\Y", 0f);
        _vom.Set($"{slotPath}\\Width", 0f);
        _vom.Set($"{slotPath}\\Height", 0f);
        _vom.Set($"{slotPath}\\ElementType", 5.0f); // Active Slot Preview
        _vom.Set($"{slotPath}\\Visible", true);
    }

    /// <summary>
    /// Tracks pointer drags within active snap choosers to update active/inactive states
    /// and project the holographic layout helper.
    /// </summary>
    public void TrackSnapProgress(float mouseX, float mouseY)
    {
        if (!WasInZone) return;

        bool foundHover = false;
        string slotPath = "\\Windows\\System\\SnapZoneChooser\\SnapSlot";

        for (int i = 0; i < CachedSnapZones.Count; i++)
        {
            var zone = CachedSnapZones[i];
            bool hover = mouseX >= zone.Cx && mouseX < zone.Cx + zone.Cw &&
                         mouseY >= zone.Cy && mouseY < zone.Cy + zone.Ch;

            _vom.Set($"{zone.BasePath}\\ElementType", hover ? 5.0f : 4.0f);

            if (hover)
            {
                _vom.Set($"{slotPath}\\TargetX", zone.Gx);
                _vom.Set($"{slotPath}\\TargetY", zone.Gy);
                _vom.Set($"{slotPath}\\TargetW", zone.Gw);
                _vom.Set($"{slotPath}\\TargetH", zone.Gh);

                ActiveSnapSlot = zone;
                LastHoveredSnapWidget = zone;
                foundHover = true;
            }
        }

        if (!foundHover)
        {
            if (LastHoveredSnapWidget.HasValue)
            {
                var last = LastHoveredSnapWidget.Value;
                _vom.Set($"{slotPath}\\TargetX", last.Cx);
                _vom.Set($"{slotPath}\\TargetY", last.Cy);
                _vom.Set($"{slotPath}\\TargetW", last.Cw);
                _vom.Set($"{slotPath}\\TargetH", last.Ch);
                LastHoveredSnapWidget = null;
            }
            else
            {
                _vom.Set($"{slotPath}\\TargetW", 0f); // Collapse if not hovering
            }
            ActiveSnapSlot = null;
        }
    }

    /// <summary>
    /// Cleans up and frees all temporary snap layout resources from the registry.
    /// </summary>
    public void FreeSnapGroup()
    {
        if (!WasInZone) return;

        _vom.Delete("\\Windows\\System\\SnapZoneChooser\\Base");
        _vom.Delete("\\Windows\\System\\SnapZoneChooser\\SnapSlot");

        for (int i = 0; i < CachedSnapZones.Count; i++)
        {
            _vom.Delete(CachedSnapZones[i].BasePath);
        }

        ActiveSnapSlot = null;
        LastHoveredSnapWidget = null;
        CachedSnapZones.Clear();
        WasInZone = false;
    }

    private struct LayoutBlock
    {
        public float Lw, Lh, Lx, Ly; // button proportions
        public float Gx, Gy, Gw, Gh; // final grid dimensions
    }
}
