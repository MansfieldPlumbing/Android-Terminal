using System;
using System.Collections.Generic;
using System.Linq;
using TuiDwm.Core;

namespace TuiDwm.Engine;

public static class LayoutManager
{
    public static void Apply(Vom vom)
    {
        int cols = vom.Get<int>("\\Dwm\\Cols", 80);
        int rows = vom.Get<int>("\\Dwm\\Rows", 24);

        int usableRows = Math.Max(0, rows - 1); // 1 row reserved for status bar

        // Discover active windows
        var keys = vom.EnumerateKeys("\\Windows\\");
        var indices = keys
            .Select(k => k.Split('\\')[2])
            .Where(s => int.TryParse(s, out _))
            .Select(int.Parse)
            .Distinct()
            .OrderBy(i => i)
            .ToList();

        if (indices.Count == 0)
            return;

        string layout = vom.Get<string>("\\Dwm\\Layout", "tile");
        int focusIndex = vom.Get<int>("\\Dwm\\FocusIndex", 0);

        if (!indices.Contains(focusIndex))
        {
            focusIndex = indices[0];
            vom.Set("\\Dwm\\FocusIndex", focusIndex);
        }

        // Apply layouts
        if (layout.Equals("fullscreen", StringComparison.OrdinalIgnoreCase))
        {
            foreach (int i in indices)
            {
                string basePath = $"\\Windows\\{i}";
                vom.Set(basePath + "\\TargetZSlot", 0);
                if (i == focusIndex)
                {
                    vom.Set(basePath + "\\X", 0);
                    vom.Set(basePath + "\\Y", 0);
                    vom.Set(basePath + "\\Width", cols);
                    vom.Set(basePath + "\\Height", usableRows);
                    vom.Set(basePath + "\\Visible", true);
                }
                else
                {
                    vom.Set(basePath + "\\Visible", false);
                }
            }
        }
        else if (layout.Equals("stack", StringComparison.OrdinalIgnoreCase))
        {
            int count = indices.Count;
            int heightPerWin = usableRows / count;
            int remainder = usableRows % count;
            int currentY = 0;

            for (int j = 0; j < count; j++)
            {
                int i = indices[j];
                string basePath = $"\\Windows\\{i}";
                int winHeight = heightPerWin + (j < remainder ? 1 : 0);

                vom.Set(basePath + "\\TargetZSlot", 0);
                vom.Set(basePath + "\\X", 0);
                vom.Set(basePath + "\\Y", currentY);
                vom.Set(basePath + "\\Width", cols);
                vom.Set(basePath + "\\Height", winHeight);
                vom.Set(basePath + "\\Visible", true);

                currentY += winHeight;
            }
        }
        else if (layout.Equals("float", StringComparison.OrdinalIgnoreCase))
        {
            // Floating layout: Windows keep their coordinates and run kinematics collision evasion
            for (int j = 0; j < indices.Count; j++)
            {
                int i = indices[j];
                string basePath = $"\\Windows\\{i}";

                // Ensure default values are populated if missing or zero
                int w = vom.Get<int>(basePath + "\\Width", 0);
                int h = vom.Get<int>(basePath + "\\Height", 0);
                if (w == 0) vom.Set(basePath + "\\Width", 36);
                if (h == 0) vom.Set(basePath + "\\Height", 10);

                // Check X and Y
                if (!vom.TryGet<int>(basePath + "\\X", out _))
                {
                    // Stagger default placement
                    vom.Set(basePath + "\\X", j * 4);
                }
                if (!vom.TryGet<int>(basePath + "\\Y", out _))
                {
                    vom.Set(basePath + "\\Y", j * 2);
                }

                // Default new windows to visible; respect explicit hide (minimize/close)
                if (!vom.TryGet<bool>(basePath + "\\Visible", out _)) vom.Set(basePath + "\\Visible", true);

                // Ensure helper values exist
                if (!vom.TryGet<bool>(basePath + "\\IsZPushable", out _)) vom.Set(basePath + "\\IsZPushable", true);
                if (!vom.TryGet<int>(basePath + "\\BaseZSlot", out _)) vom.Set(basePath + "\\BaseZSlot", 0);
                if (!vom.TryGet<int>(basePath + "\\ZIndex", out _)) vom.Set(basePath + "\\ZIndex", i);
            }

            // Run Kinematics AABB Collision Evasion System
            TickKinematics(vom, indices);
        }
        else // default "tile"
        {
            if (indices.Count == 1)
            {
                int i = indices[0];
                string basePath = $"\\Windows\\{i}";
                vom.Set(basePath + "\\TargetZSlot", 0);
                vom.Set(basePath + "\\X", 0);
                vom.Set(basePath + "\\Y", 0);
                vom.Set(basePath + "\\Width", cols);
                vom.Set(basePath + "\\Height", usableRows);
                vom.Set(basePath + "\\Visible", true);
            }
            else
            {
                // Master window gets the left 50%
                int masterW = cols / 2;
                int iMaster = indices[0];
                string masterPath = $"\\Windows\\{iMaster}";
                vom.Set(masterPath + "\\TargetZSlot", 0);
                vom.Set(masterPath + "\\X", 0);
                vom.Set(masterPath + "\\Y", 0);
                vom.Set(masterPath + "\\Width", masterW);
                vom.Set(masterPath + "\\Height", usableRows);
                vom.Set(masterPath + "\\Visible", true);

                // Stack remaining windows on the right
                int stackW = cols - masterW;
                int stackCount = indices.Count - 1;
                int heightPerWin = usableRows / stackCount;
                int remainder = usableRows % stackCount;
                int currentY = 0;

                for (int j = 0; j < stackCount; j++)
                {
                    int i = indices[j + 1];
                    string basePath = $"\\Windows\\{i}";
                    int winHeight = heightPerWin + (j < remainder ? 1 : 0);

                    vom.Set(basePath + "\\TargetZSlot", 0);
                    vom.Set(basePath + "\\X", masterW);
                    vom.Set(basePath + "\\Y", currentY);
                    vom.Set(basePath + "\\Width", stackW);
                    vom.Set(basePath + "\\Height", winHeight);
                    vom.Set(basePath + "\\Visible", true);

                    currentY += winHeight;
                }
            }
        }
    }

    private static void TickKinematics(Vom vom, List<int> indices)
    {
        // 1. Gather window states
        var windows = indices.Select(i => new WindowEvasionData
        {
            Index = i,
            X = vom.Get<int>($"\\Windows\\{i}\\X", 0),
            Y = vom.Get<int>($"\\Windows\\{i}\\Y", 0),
            Width = vom.Get<int>($"\\Windows\\{i}\\Width", 10),
            Height = vom.Get<int>($"\\Windows\\{i}\\Height", 5),
            ZIndex = vom.Get<int>($"\\Windows\\{i}\\ZIndex", i),
            BaseZSlot = vom.Get<int>($"\\Windows\\{i}\\BaseZSlot", 0),
            IsZPushable = vom.Get<bool>($"\\Windows\\{i}\\IsZPushable", true)
        }).ToList();

        var normalWindows = windows.Where(w => w.IsZPushable).ToList();

        // Sort descending: highest ZIndex (focused/front) first
        normalWindows.Sort((a, b) => b.ZIndex.CompareTo(a.ZIndex));

        int focusIndex = vom.Get<int>("\\Dwm\\FocusIndex", 0);

        // 2. Assign evasion target Z-slots
        for (int i = 0; i < normalWindows.Count; i++)
        {
            var current = normalWindows[i];
            int minSlot = 0;

            // Check overlap with windows that are HIGHER priority (rendered on top of us)
            for (int j = 0; j < i; j++)
            {
                var other = normalWindows[j];

                bool isAgainstFocused = (current.Index == focusIndex || other.Index == focusIndex);
                int threshold = isAgainstFocused ? 2 : 1; // require more overlap if it is the focused window

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
                    var other = normalWindows[j];

                    bool isAgainstFocused = (current.Index == focusIndex || other.Index == focusIndex);
                    int threshold = isAgainstFocused ? 2 : 1;

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
            vom.Set($"\\Windows\\{current.Index}\\TargetZSlot", evasionOffset);
        }
    }

    private static bool EvaluateAABBIntersection(WindowEvasionData a, WindowEvasionData b, int threshold)
    {
        int left = Math.Max(a.X, b.X);
        int right = Math.Min(a.X + a.Width, b.X + b.Width);
        int top = Math.Max(a.Y, b.Y);
        int bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);

        if (left < right && top < bottom)
        {
            int overlapW = right - left;
            int overlapH = bottom - top;
            return overlapW > threshold && overlapH > threshold;
        }
        return false;
    }

    private class WindowEvasionData
    {
        public int Index;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int ZIndex;
        public int BaseZSlot;
        public int TargetZSlot;
        public bool IsZPushable;
    }
}
