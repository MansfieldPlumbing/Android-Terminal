using System;
using System.Collections.Generic;
using TuiDwm.Core;

namespace TuiDwm.Port;

/// <summary>
/// Cinematic Kinematics & Depth Physics Engine.
/// Handles AABB window boundary collisions, depth-based Z-evasion
/// pushing, and inertial animation interpolation.
/// </summary>
public static class Kinematics
{
    private sealed class PhysicalElement
    {
        private readonly Vom _vom;
        public string BasePath { get; }
        public int WindowIndex { get; }

        public PhysicalElement(Vom vom, string basePath, int windowIndex)
        {
            _vom = vom;
            BasePath = basePath;
            WindowIndex = windowIndex;
        }

        public float X { get => _vom.Get<float>($"{BasePath}\\X", 0f); set => _vom.Set($"{BasePath}\\X", value); }
        public float Y { get => _vom.Get<float>($"{BasePath}\\Y", 0f); set => _vom.Set($"{BasePath}\\Y", value); }
        public float W { get => _vom.Get<float>($"{BasePath}\\Width", 0f); set => _vom.Set($"{BasePath}\\Width", value); }
        public float H { get => _vom.Get<float>($"{BasePath}\\Height", 0f); set => _vom.Set($"{BasePath}\\Height", value); }

        public float Z { get => _vom.Get<float>($"{BasePath}\\Z", 0f); set => _vom.Set($"{BasePath}\\Z", value); }
        public int ZIndex { get => _vom.Get<int>($"{BasePath}\\ZIndex", 0); set => _vom.Set($"{BasePath}\\ZIndex", value); }
        public int TargetZSlot { get => _vom.Get<int>($"{BasePath}\\TargetZSlot", 0); set => _vom.Set($"{BasePath}\\TargetZSlot", value); }
        public int BaseZSlot { get => _vom.Get<int>($"{BasePath}\\BaseZSlot", 0); set => _vom.Set($"{BasePath}\\BaseZSlot", value); }
        public bool IsZPushable { get => _vom.Get<bool>($"{BasePath}\\IsZPushable", true); set => _vom.Set($"{BasePath}\\IsZPushable", value); }

        public float? TargetX { get { return _vom.TryGet<float>($"{BasePath}\\TargetX", out var v) ? v : null; } set { if (value.HasValue) _vom.Set($"{BasePath}\\TargetX", value.Value); else _vom.Delete($"{BasePath}\\TargetX"); } }
        public float? TargetY { get { return _vom.TryGet<float>($"{BasePath}\\TargetY", out var v) ? v : null; } set { if (value.HasValue) _vom.Set($"{BasePath}\\TargetY", value.Value); else _vom.Delete($"{BasePath}\\TargetY"); } }
        public float? TargetW { get { return _vom.TryGet<float>($"{BasePath}\\TargetW", out var v) ? v : null; } set { if (value.HasValue) _vom.Set($"{BasePath}\\TargetW", value.Value); else _vom.Delete($"{BasePath}\\TargetW"); } }
        public float? TargetH { get { return _vom.TryGet<float>($"{BasePath}\\TargetH", out var v) ? v : null; } set { if (value.HasValue) _vom.Set($"{BasePath}\\TargetH", value.Value); else _vom.Delete($"{BasePath}\\TargetH"); } }
    }

    private static bool EvaluateAABBIntersection(PhysicalElement a, PhysicalElement b, float threshold)
    {
        float left = Math.Max(a.X, b.X);
        float right = Math.Min(a.X + a.W, b.X + b.W);
        float top = Math.Max(a.Y, b.Y);
        float bottom = Math.Min(a.Y + a.H, b.Y + b.H);

        if (left < right && top < bottom)
        {
            float overlapW = right - left;
            float overlapH = bottom - top;
            return overlapW > threshold && overlapH > threshold;
        }
        return false;
    }

    public static void Tick(Vom vom, List<PluginInstance> plugins, int draggedWindowIndex, double deltaTime)
    {
        // 1. Gather all active window state elements
        var elements = new List<PhysicalElement>(32);
        for (int i = 0; i < plugins.Count; i++)
        {
            var p = plugins[i];
            if (vom.Get<bool>($"{p.BasePath}\\Visible", true))
            {
                elements.Add(new PhysicalElement(vom, p.BasePath, p.WindowIndex));
            }
        }

        PhysicalElement? draggedElement = null;
        for (int i = 0; i < elements.Count; i++)
        {
            if (elements[i].WindowIndex == draggedWindowIndex)
            {
                draggedElement = elements[i];
                break;
            }
        }

        // 2. Compute Depth Evasion Physics
        TickKinematics(elements, draggedElement);

        // 3. Integrate motion vectors
        IntegrateMotion(elements, deltaTime, draggedElement);
    }

    private static void TickKinematics(List<PhysicalElement> elements, PhysicalElement? draggedElement)
    {
        // Gather Z-Pushable windows
        var normalQuads = new List<PhysicalElement>(elements.Count);
        for (int i = 0; i < elements.Count; i++)
        {
            if (elements[i].IsZPushable)
            {
                normalQuads.Add(elements[i]);
            }
        }

        // Sort descending: closest Z-Index (Focused window) first
        normalQuads.Sort((a, b) => b.ZIndex.CompareTo(a.ZIndex));

        for (int i = 0; i < normalQuads.Count; i++)
        {
            var current = normalQuads[i];
            int minSlot = 0;

            // First determine slot based on overlapping higher-priority elements
            for (int j = 0; j < i; j++)
            {
                var other = normalQuads[j];
                bool isAgainstDragged = (other == draggedElement || current == draggedElement);
                float threshold = isAgainstDragged ? 0.2f : 0.05f;

                if (current.BaseZSlot == other.BaseZSlot && EvaluateAABBIntersection(current, other, threshold))
                {
                    minSlot = Math.Max(minSlot, other.TargetZSlot + 1);
                }
            }

            int evasionOffset = minSlot;
            bool conflict = true;
            const int MAX_SLOT = 20;

            while (conflict && evasionOffset < MAX_SLOT)
            {
                conflict = false;
                for (int j = 0; j < i; j++)
                {
                    var other = normalQuads[j];
                    bool isAgainstDragged = (other == draggedElement || current == draggedElement);
                    float threshold = isAgainstDragged ? 0.2f : 0.05f;

                    if (other.BaseZSlot == current.BaseZSlot && other.TargetZSlot == evasionOffset && EvaluateAABBIntersection(current, other, threshold))
                    {
                        conflict = true;
                        break;
                    }
                }
                if (conflict)
                {
                    evasionOffset++;
                }
            }
            current.TargetZSlot = evasionOffset;
        }
    }

    private static void IntegrateMotion(List<PhysicalElement> elements, double deltaTime, PhysicalElement? zLockedElement)
    {
        for (int i = 0; i < elements.Count; i++)
        {
            var el = elements[i];
            if (!el.IsZPushable || el == zLockedElement) continue;

            float targetZ = Math.Min(1.0f, (el.BaseZSlot * 0.1f) + (el.TargetZSlot * 0.02f));
            float diff = targetZ - el.Z;

            if (Math.Abs(diff) > 0.001f)
            {
                el.Z += diff * (float)Math.Min(15.0 * deltaTime, 1.0);
            }
            else
            {
                el.Z = targetZ;
            }
        }

        // Interpolate 2D boundary kinematics
        for (int i = 0; i < elements.Count; i++)
        {
            var el = elements[i];
            if (el.TargetX.HasValue)
            {
                float tx = el.TargetX.Value;
                el.X += (tx - el.X) * 0.3f;
                if (Math.Abs(el.X - tx) < 0.001f) { el.X = tx; el.TargetX = null; }
            }
            if (el.TargetY.HasValue)
            {
                float ty = el.TargetY.Value;
                el.Y += (ty - el.Y) * 0.3f;
                if (Math.Abs(el.Y - ty) < 0.001f) { el.Y = ty; el.TargetY = null; }
            }
            if (el.TargetW.HasValue)
            {
                float tw = el.TargetW.Value;
                el.W += (tw - el.W) * 0.3f;
                if (Math.Abs(el.W - tw) < 0.001f) { el.W = tw; el.TargetW = null; }
            }
            if (el.TargetH.HasValue)
            {
                float th = el.TargetH.Value;
                el.H += (th - el.H) * 0.3f;
                if (Math.Abs(el.H - th) < 0.001f) { el.H = th; el.TargetH = null; }
            }
        }
    }
}
