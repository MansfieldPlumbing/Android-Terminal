using System.Collections.Generic;
using System.Linq;
using TuiDwm.Core;

namespace TuiDwm.Engine;

public static class StatusBar
{
    public static void Render(CellBuffer composite, Vom vom, int currentFps)
    {
        int y = composite.Height - 1;
        if (y < 0) return;

        // Clear the status bar row with a sleek dark gray style
        byte barBg = 236; // Dark gray
        byte barFg = 250; // Off-white
        
        for (int x = 0; x < composite.Width; x++)
        {
            ref var cell = ref composite.At(x, y);
            cell.Rune = ' ';
            cell.Fg = barFg;
            cell.Bg = barBg;
            cell.Style = 0;
        }

        string layout = vom.Get<string>("\\Dwm\\Layout", "tile").ToUpper();
        int focusIndex = vom.Get<int>("\\Dwm\\FocusIndex", 0);
        int vpX = vom.Get<int>("\\Dwm\\ViewportX", 0);
        int vpY = vom.Get<int>("\\Dwm\\ViewportY", 0);

        // Discover active windows
        var keys = vom.EnumerateKeys("\\Windows\\");
        var windowIndices = keys
            .Select(k => k.Split('\\')[2])
            .Where(s => int.TryParse(s, out _))
            .Select(int.Parse)
            .Distinct()
            .OrderBy(i => i)
            .ToList();

        // Write layout mode
        string layoutText = $" ❖ {layout} ";
        int cursorX = WriteString(composite, 0, y, layoutText, barFg, barBg);

        // Viewport indicator — only when scrolled away from origin
        if (vpX != 0 || vpY != 0)
        {
            string vpText = $"[{vpX},{vpY}] ";
            cursorX = WriteString(composite, cursorX, y, vpText, 220, barBg); // amber
        }

        cursorX = WriteString(composite, cursorX, y, "│ ", barFg, barBg);

        // Write window tags
        for (int j = 0; j < windowIndices.Count; j++)
        {
            int i = windowIndices[j];
            string title = vom.Get<string>($"\\Windows\\{i}\\Title", $"Win {i}");
            string tagText = $" {i}:{title} ";

            byte fg = barFg;
            byte bg = barBg;
            byte style = 0;

            if (i == focusIndex)
            {
                fg = 255;  // Pure white
                bg = 24;   // Dark blue background for focused item
                style = 1; // Bold
            }

            cursorX = WriteString(composite, cursorX, y, tagText, fg, bg, style);
        }

        // Right side: theme button + FPS
        int bgMode = vom.Get<int>("\\Dwm\\BgMode", 0);
        string themeName = Compositor.BgThemeName(bgMode);
        string themeText = $"│ ● {themeName} ";
        string fpsText   = $"│ FPS: {currentFps} ";
        int fpsX   = composite.Width - fpsText.Length;
        int themeX = fpsX - themeText.Length;

        if (themeX > cursorX)
        {
            // Store click zone in VOM for hit-testing
            vom.Set("\\Dwm\\ThemeBtnX", themeX);
            vom.Set("\\Dwm\\ThemeBtnW", themeText.Length);
            vom.Set("\\Dwm\\ThemeBtnY", y);
            WriteString(composite, themeX, y, themeText, 220, barBg); // amber
        }
        if (fpsX > cursorX)
        {
            WriteString(composite, fpsX, y, fpsText, barFg, barBg);
        }
    }

    private static int WriteString(CellBuffer buffer, int x, int y, string text, byte fg, byte bg, byte style = 0)
    {
        for (int i = 0; i < text.Length; i++)
        {
            int targetX = x + i;
            if (targetX >= buffer.Width) break;
            ref var cell = ref buffer.At(targetX, y);
            cell.Rune = text[i];
            cell.Fg = fg;
            cell.Bg = bg;
            cell.Style = style;
        }
        return x + text.Length;
    }
}
