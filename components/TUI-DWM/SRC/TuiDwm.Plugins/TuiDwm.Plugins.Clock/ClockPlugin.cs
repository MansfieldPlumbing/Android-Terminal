using System;
using TuiDwm.Core;

namespace TuiDwm.Plugins.Clock;

public class ClockPlugin : ITuiPlugin
{
    private Vom? _vom;
    private string _basePath = "";
    private int _fgColor = 10; // green clock (xterm color 10)
    private int _bgColor = 0;  // black background

    private static readonly string[][] Digits = new string[][]
    {
        new[] { "███", "█ █", "█ █", "█ █", "███" }, // 0
        new[] { "  █", "  █", "  █", "  █", "  █" }, // 1
        new[] { "███", "  █", "███", "█  ", "███" }, // 2
        new[] { "███", "  █", "███", "  █", "███" }, // 3
        new[] { "█ █", "█ █", "███", "  █", "  █" }, // 4
        new[] { "███", "█  ", "███", "  █", "███" }, // 5
        new[] { "███", "█  ", "███", "█ █", "███" }, // 6
        new[] { "███", "  █", "  █", "  █", "  █" }, // 7
        new[] { "███", "█ █", "███", "█ █", "███" }, // 8
        new[] { "███", "█ █", "███", "  █", "███" }, // 9
        new[] { "   ", " █ ", "   ", " █ ", "   " }  // :
    };

    public Vomt GetTemplate()
    {
        return new Vomt
        {
            PluginName = "Clock",
            Entries = new()
            {
                ("\\Title", "Clock (Interactive)"),
                ("\\MinWidth", 25),
                ("\\MinHeight", 6),
                ("\\Focused", false),
                ("\\Visible", true)
            }
        };
    }

    public void Initialize(Vom vom, string basePath)
    {
        _vom = vom;
        _basePath = basePath;
    }

    public void Render(CellBuffer buffer)
    {
        // Dark blue grid background or simple clear
        buffer.Clear(new Cell(' ', 244, 233, 0)); // dim gray text, very dark background

        string timeStr = DateTime.Now.ToString("HH:mm:ss");

        // We need 31 columns for the large clock, plus borders
        if (buffer.Width >= 33 && buffer.Height >= 7)
        {
            int totalW = 8 * 3 + 7; // 8 characters of width 3, plus 7 spaces between them
            int totalH = 5;

            int startX = (buffer.Width - totalW) / 2;
            int startY = (buffer.Height - totalH) / 2;

            int curX = startX;
            for (int i = 0; i < timeStr.Length; i++)
            {
                char c = timeStr[i];
                string[] digitPattern = c switch
                {
                    ':' => Digits[10],
                    _ => Digits[c - '0']
                };

                for (int dy = 0; dy < 5; dy++)
                {
                    string row = digitPattern[dy];
                    for (int dx = 0; dx < 3; dx++)
                    {
                        if (row[dx] == '█')
                        {
                            int px = curX + dx;
                            int py = startY + dy;
                            if (px >= 0 && px < buffer.Width && py >= 0 && py < buffer.Height)
                            {
                                ref var cell = ref buffer.At(px, py);
                                cell.Rune = '█';
                                cell.Fg = (byte)_fgColor;
                                cell.Bg = (byte)_bgColor;
                                cell.Style = 1; // Bold
                            }
                        }
                    }
                }
                curX += 4; // width 3 + gap 1
            }

            // Draw interaction prompt
            string prompt = "[Space] Cycle Color";
            int pxPrompt = (buffer.Width - prompt.Length) / 2;
            int pyPrompt = buffer.Height - 1;
            if (pyPrompt > startY + 5 && pxPrompt >= 0)
            {
                for (int i = 0; i < prompt.Length && pxPrompt + i < buffer.Width; i++)
                {
                    ref var cell = ref buffer.At(pxPrompt + i, pyPrompt);
                    cell.Rune = prompt[i];
                    cell.Fg = 244;
                    cell.Bg = 233;
                    cell.Style = 2; // Dim
                }
            }
        }
        else
        {
            // Small mode
            string text = $"Time: {timeStr}";
            int startX = (buffer.Width - text.Length) / 2;
            int startY = buffer.Height / 2;

            startX = Math.Max(0, startX);
            startY = Math.Max(0, startY);

            for (int i = 0; i < text.Length && (startX + i) < buffer.Width; i++)
            {
                ref var cell = ref buffer.At(startX + i, startY);
                cell.Rune = text[i];
                cell.Fg = (byte)_fgColor;
                cell.Bg = (byte)_bgColor;
                cell.Style = 1; // Bold
            }
        }
    }

    public void HandleInput(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.Spacebar || key.Key == ConsoleKey.Enter)
        {
            // Cycle through vibrant colors: green -> yellow -> blue -> magenta -> cyan -> red
            _fgColor = _fgColor switch
            {
                10 => 11, // Green -> Yellow
                11 => 12, // Yellow -> Blue (Bright)
                12 => 13, // Blue -> Magenta
                13 => 14, // Magenta -> Cyan
                14 => 9,  // Cyan -> Red
                _ => 10   // Red -> Green
            };
        }
    }

    public void OnResize(int newWidth, int newHeight)
    {
    }
}
