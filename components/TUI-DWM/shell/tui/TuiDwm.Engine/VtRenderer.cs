using System;
using System.Runtime.InteropServices;
using System.Text;
using TuiDwm.Core;

namespace TuiDwm.Engine;

public class VtRenderer
{
    // conhost leaves VT processing off by default; these let us turn it on so the
    // renderer's escapes display in classic console hosts, not only Windows Terminal.
    private const int  STD_OUTPUT_HANDLE = -11;
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
    private const uint DISABLE_NEWLINE_AUTO_RETURN        = 0x0008;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    private readonly StringBuilder _sb = new(1024 * 32);
    private byte _lastFg = 255;
    private byte _lastBg = 255;
    private byte _lastStyle = 255;

    public void Initialize()
    {
        // Windows Terminal processes VT unconditionally; classic conhost does NOT —
        // without this it prints the escapes below as literal text. Enable VT on stdout
        // first so the engine renders the same in either host.
        IntPtr hOut = GetStdHandle(STD_OUTPUT_HANDLE);
        if (GetConsoleMode(hOut, out uint outMode))
            SetConsoleMode(hOut, outMode | ENABLE_VIRTUAL_TERMINAL_PROCESSING | DISABLE_NEWLINE_AUTO_RETURN);

        Console.OutputEncoding = Encoding.UTF8;
        // Enter alternate screen buffer, hide cursor, clear screen, reset attributes
        Console.Out.Write("\x1b[?1049h\x1b[?25l\x1b[0m\x1b[2J");
        Console.Out.Flush();
        _lastFg = 255;
        _lastBg = 255;
        _lastStyle = 255;
    }

    // Force a full-screen clear + re-render on next frame (call on terminal resize)
    public void Invalidate()
    {
        Console.Out.Write("\x1b[0m\x1b[2J");
        Console.Out.Flush();
        _lastFg = 255;
        _lastBg = 255;
        _lastStyle = 255;
    }

    public void Shutdown()
    {
        // Reset styles, show cursor, exit alternate screen
        Console.Out.Write("\x1b[0m\x1b[?25h\x1b[?1049l");
        Console.Out.Flush();
    }

    public void Render(CellBuffer current, CellBuffer previous)
    {
        _sb.Clear();
        int width = current.Width;
        int height = current.Height;

        // Ensure previous buffer matches dimensions
        if (previous.Width != width || previous.Height != height)
        {
            previous.Resize(width, height);
            previous.Clear(new Cell('\0', 255, 255, 255)); // force difference on everything
        }

        for (int y = 0; y < height; y++)
        {
            bool inSequence = false;

            for (int x = 0; x < width; x++)
            {
                ref Cell curr = ref current.At(x, y);
                ref Cell prev = ref previous.At(x, y);

                if (curr == prev)
                {
                    inSequence = false;
                    continue;
                }

                if (!inSequence)
                {
                    _sb.Append("\x1b[").Append(y + 1).Append(';').Append(x + 1).Append('H');
                    inSequence = true;
                }

                ApplyStyles(curr.Fg, curr.Bg, curr.Style);
                _sb.Append(curr.Rune == '\0' ? ' ' : curr.Rune);
            }
        }

        if (_sb.Length > 0)
        {
            Console.Out.Write(_sb.ToString());
            Console.Out.Flush();
        }
    }

    private void ApplyStyles(byte fg, byte bg, byte style)
    {
        bool resetNeeded = false;

        // If styles were turned off, a reset is required
        if (_lastStyle != style)
        {
            if ((_lastStyle & ~style) != 0)
            {
                resetNeeded = true;
            }
        }

        if (resetNeeded)
        {
            _sb.Append("\x1b[0m");
            _lastFg = 7;
            _lastBg = 0;
            _lastStyle = 0;
        }

        if (_lastStyle != style)
        {
            byte addedStyles = (byte)(style & ~_lastStyle);
            if (resetNeeded) addedStyles = style;

            if ((addedStyles & 1) != 0) _sb.Append("\x1b[1m");  // Bold
            if ((addedStyles & 2) != 0) _sb.Append("\x1b[2m");  // Dim
            if ((addedStyles & 4) != 0) _sb.Append("\x1b[3m");  // Italic
            if ((addedStyles & 8) != 0) _sb.Append("\x1b[4m");  // Underline
            if ((addedStyles & 16) != 0) _sb.Append("\x1b[5m"); // Blink
            if ((addedStyles & 32) != 0) _sb.Append("\x1b[7m"); // Reverse

            _lastStyle = style;
        }

        if (_lastFg != fg)
        {
            _sb.Append("\x1b[38;5;").Append(fg).Append('m');
            _lastFg = fg;
        }

        if (_lastBg != bg)
        {
            if (bg == 0)
                _sb.Append("\x1b[49m"); // Bg=0 → terminal default (Mica/acrylic shows through)
            else
                _sb.Append("\x1b[48;5;").Append(bg).Append('m');
            _lastBg = bg;
        }
    }
}
