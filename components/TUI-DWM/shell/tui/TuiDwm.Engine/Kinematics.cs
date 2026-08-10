using System;
using System.Collections.Generic;
using System.Linq;
using TuiDwm.Core;

namespace TuiDwm.Engine;

public static class Kinematics
{
    private class QuadElement
    {
        private Vom _vom;
        public string BasePath { get; }
        public int WindowIndex { get; }

        public QuadElement(Vom vom, string basePath, int windowIndex)
        {
            _vom = vom;
            BasePath = basePath;
            WindowIndex = windowIndex;
        }

        public float x { get => _vom.Get<float>($"{BasePath}\\X", 0f); set => _vom.Set($"{BasePath}\\X", value); }
        public float y { get => _vom.Get<float>($"{BasePath}\\Y", 0f); set => _vom.Set($"{BasePath}\\Y", value); }
        public float w { get => _vom.Get<float>($"{BasePath}\\Width", 0f); set => _vom.Set($"{BasePath}\\Width", value); }
        public float h { get => _vom.Get<float>($"{BasePath}\\Height", 0f); set => _vom.Set($"{BasePath}\\Height", value); }

        public float z { get => _vom.Get<float>($"{BasePath}\\Z", 0f); set => _vom.Set($"{BasePath}\\Z", value); }
        public int zIndex { get => _vom.Get<int>($"{BasePath}\\ZIndex", 0); set => _vom.Set($"{BasePath}\\ZIndex", value); }
        public int targetZSlot { get => _vom.Get<int>($"{BasePath}\\TargetZSlot", 0); set => _vom.Set($"{BasePath}\\TargetZSlot", value); }
        public int baseZSlot { get => _vom.Get<int>($"{BasePath}\\BaseZSlot", 0); set => _vom.Set($"{BasePath}\\BaseZSlot", value); }
        public bool isZPushable { get => _vom.Get<bool>($"{BasePath}\\IsZPushable", true); set => _vom.Set($"{BasePath}\\IsZPushable", value); }

        public float? targetX { get { return _vom.TryGet<float>($"{BasePath}\\TargetX", out var v) ? v : null; } set { if (value.HasValue) _vom.Set($"{BasePath}\\TargetX", value.Value); else _vom.Delete($"{BasePath}\\TargetX"); } }
        public float? targetY { get { return _vom.TryGet<float>($"{BasePath}\\TargetY", out var v) ? v : null; } set { if (value.HasValue) _vom.Set($"{BasePath}\\TargetY", value.Value); else _vom.Delete($"{BasePath}\\TargetY"); } }
        public float? targetW { get { return _vom.TryGet<float>($"{BasePath}\\TargetW", out var v) ? v : null; } set { if (value.HasValue) _vom.Set($"{BasePath}\\TargetW", value.Value); else _vom.Delete($"{BasePath}\\TargetW"); } }
        public float? targetH { get { return _vom.TryGet<float>($"{BasePath}\\TargetH", out var v) ? v : null; } set { if (value.HasValue) _vom.Set($"{BasePath}\\TargetH", value.Value); else _vom.Delete($"{BasePath}\\TargetH"); } }
    }

    private static bool EvaluateAABBIntersection(QuadElement a, QuadElement b, float threshold = 0.0f)
    {
        float left = Math.Max(a.x, b.x);
        float right = Math.Min(a.x + a.w, b.x + b.w);
        float top = Math.Max(a.y, b.y);
        float bottom = Math.Min(a.y + a.h, b.y + b.h);

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
        // Lifecycle Evaluation
        for (int i = plugins.Count - 1; i >= 0; i--)
        {
            var p = plugins[i];
            float z = vom.Get<float>($"{p.BasePath}\\Z", 0f);
            float y = vom.Get<float>($"{p.BasePath}\\Y", 0f);

            if (p.State == LifecycleState.Spawning)
            {
                if (z > -0.5f) p.State = LifecycleState.Active;
            }
            else if (p.State == LifecycleState.Exiting)
            {
                if (y <= -900f || z <= -1900f) // The Splash Point
                {
                    p.Stop();
                    if (p.Plugin is IDisposable disp) disp.Dispose();
                    plugins.RemoveAt(i);
                    foreach (var key in vom.EnumerateKeys(p.BasePath).ToList())
                    {
                        vom.Delete(key);
                    }
                    continue;
                }
            }
        }

        var elements = plugins
            .Where(p => vom.Get<bool>($"{p.BasePath}\\Visible", false))
            .Select(p => new QuadElement(vom, p.BasePath, p.WindowIndex))
            .ToList();

        // Inject Context Menus into the physics engine
        int contextMenuCount = vom.Get<int>("\\Windows\\ContextMenus\\Count", 0);
        for (int i = 0; i < contextMenuCount; i++)
        {
            elements.Add(new QuadElement(vom, $"\\Windows\\ContextMenus\\{i}", -2)); // -2 to ignore drag logic
        }

        QuadElement? draggedElement = elements.FirstOrDefault(e => e.WindowIndex == draggedWindowIndex);

        TickKinematics(elements, draggedElement);
        IntegrateMotion(elements, deltaTime, draggedElement);
    }

    private static void TickKinematics(List<QuadElement> elements, QuadElement? draggedElement)
    {
        var normalQuads = elements.Where(e => e.isZPushable).ToList();
        normalQuads.Sort((a, b) => b.zIndex.CompareTo(a.zIndex)); // Highest zIndex first

        foreach (var current in normalQuads)
        {
            int minSlot = 0;

            foreach (var other in normalQuads)
            {
                if (other == current) break;

                bool isAgainstDragged = (other == draggedElement || current == draggedElement);
                float thresh = isAgainstDragged ? 0.2f : 0.05f;

                if (other.baseZSlot == current.baseZSlot && EvaluateAABBIntersection(current, other, thresh))
                {
                    minSlot = Math.Max(minSlot, other.targetZSlot);
                }
            }

            int evasionOffset = minSlot;
            bool conflict = true;
            const int MAX_SLOT = 20;

            while (conflict && evasionOffset < MAX_SLOT)
            {
                conflict = false;
                foreach (var other in normalQuads)
                {
                    if (other == current) break;

                    bool isAgainstDragged = (other == draggedElement || current == draggedElement);
                    float thresh = isAgainstDragged ? 0.2f : 0.05f;

                    if (other.baseZSlot == current.baseZSlot && other.targetZSlot == evasionOffset && EvaluateAABBIntersection(current, other, thresh))
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
            current.targetZSlot = evasionOffset;
        }
    }

    private static void IntegrateMotion(List<QuadElement> elements, double deltaTime, QuadElement? zingElement)
    {
        foreach (var el in elements)
        {
            if (!el.isZPushable) continue;
            if (el == zingElement) continue;

            float targetZ = Math.Min(1.0f, (el.baseZSlot * 0.1f) + (el.targetZSlot * 0.02f));
            float diff = targetZ - el.z;

            if (Math.Abs(diff) > 0.001f)
            {
                el.z += diff * (float)Math.Min(15.0 * deltaTime, 1.0);
            }
            else
            {
                el.z = targetZ;
            }
        }

        foreach (var el in elements)
        {
            if (el.targetX.HasValue)
            {
                float tx = el.targetX.Value;
                el.x += (tx - el.x) * 0.3f;
                if (Math.Abs(el.x - tx) < 0.001f) { el.x = tx; el.targetX = null; }
            }
            if (el.targetY.HasValue)
            {
                float ty = el.targetY.Value;
                el.y += (ty - el.y) * 0.3f;
                if (Math.Abs(el.y - ty) < 0.001f) { el.y = ty; el.targetY = null; }
            }
            if (el.targetW.HasValue)
            {
                float tw = el.targetW.Value;
                el.w += (tw - el.w) * 0.3f;
                if (Math.Abs(el.w - tw) < 0.001f) { el.w = tw; el.targetW = null; }
            }
            if (el.targetH.HasValue)
            {
                float th = el.targetH.Value;
                el.h += (th - el.h) * 0.3f;
                if (Math.Abs(el.h - th) < 0.001f) { el.h = th; el.targetH = null; }
            }
        }
    }
}
