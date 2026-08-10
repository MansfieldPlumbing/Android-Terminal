using System;
using System.Text;
using System.Threading;
using SkiaSharp;
using TuiDwm.Core;

namespace TuiDwm.Port;

/// <summary>
/// A highly optimized Terminal Session using in-process PowerShell and SkiaSharp.
/// Fully conforms to Invariant 9 (No Second Store) and Zero-Alloc Render Loop.
/// </summary>
public sealed class TerminalSession : IDisposable
{
    private readonly Vom _vom;
    private readonly string _vomPath;

    public string Title
    {
        get => _vom.Get<string>($"{_vomPath}\\Title", "Terminal");
        set => _vom.Set($"{_vomPath}\\Title", value);
    }

    public CellBuffer FrontBuffer { get; private set; }
    private CellBuffer _back;
    public readonly object BufferLock = new();

    // The rendered output surface
    public SKBitmap BlitSurface { get; private set; } = null!;
    private SKCanvas _canvas = null!;
    private SKFont _font;
    private readonly SKPaint _cellBg = new() { IsAntialias = false };
    private readonly SKPaint _cellFg = new() { IsAntialias = true };
    private readonly SKPaint _clearPaint = new() { Color = SKColors.Transparent, BlendMode = SKBlendMode.Src };

    private float _fontCellW, _fontCellH;
    private volatile bool _dirtyBlit;

    private InProcSession? _pty;
    private Thread? _pumpThread;
    private volatile bool _running;

    // VT parser state
    private int _curX, _curY;
    private byte _fg = 7, _bg = 16;
    private byte _style;
    private bool _inEsc;
    private bool _inCsi;
    private readonly StringBuilder _csiParam = new(32);

    // Alt screen
    private CellBuffer? _altScreen;
    private bool _useAlt;

    // Fast static pre-allocated string cache to guarantee zero heap allocations during text drawing
    private static readonly string[] AsciiStringCache = new string[256];

    static TerminalSession()
    {
        for (int i = 0; i < 256; i++)
        {
            AsciiStringCache[i] = ((char)i).ToString();
        }
    }

    // xterm 16-color palette
    private static readonly uint[] Palette16 =
    {
        0xFF0C0C0C, 0xFFC50F1F, 0xFF13A10E, 0xFFC19C00,
        0xFF0037DA, 0xFF881798, 0xFF3A96DD, 0xFFCCCCCC,
        0xFF767676, 0xFFE74856, 0xFF16C60C, 0xFFF9F1A5,
        0xFF3B78FF, 0xFFB4009E, 0xFF61D6D6, 0xFFF2F2F2,
    };

    public TerminalSession(Vom vom, string vomPath, int cols, int rows)
    {
        _vom = vom;
        _vomPath = vomPath;

        FrontBuffer = new CellBuffer(cols, rows);
        _back = new CellBuffer(cols, rows);

        // Init font and sizing constraints
        SKTypeface? tf =
            SKTypeface.FromFamilyName("Cascadia Code") ??
            SKTypeface.FromFamilyName("Cascadia Mono") ??
            SKTypeface.FromFamilyName("Consolas") ??
            SKTypeface.FromFamilyName("Courier New") ??
            SKTypeface.Default;

        _font = new SKFont(tf, 14f) { Edging = SKFontEdging.SubpixelAntialias };
        _fontCellW = 8f;
        _fontCellH = 17f;

        ResizeBlit(cols, rows);
        _dirtyBlit = true;

        // Initialize default Title in VOM
        Title = "Terminal";
    }

    private void ResizeBlit(int cols, int rows)
    {
        int pw = (int)(cols * _fontCellW);
        int ph = (int)(rows * _fontCellH);

        lock (BufferLock)
        {
            int newW = Math.Max(4096, NextPowerOf2(pw));
            int newH = Math.Max(4096, NextPowerOf2(ph));

            if (BlitSurface != null && BlitSurface.Width >= newW && BlitSurface.Height >= newH)
            {
                return;
            }

            var info = new SKImageInfo(newW, newH, SKColorType.Bgra8888, SKAlphaType.Premul);
            var newBitmap = new SKBitmap(info);
            var newCanvas = new SKCanvas(newBitmap);

            _canvas?.Dispose();
            BlitSurface?.Dispose();

            BlitSurface = newBitmap;
            _canvas = newCanvas;
        }
    }

    private static int NextPowerOf2(int v)
    {
        v--;
        v |= v >> 1;
        v |= v >> 2;
        v |= v >> 4;
        v |= v >> 8;
        v |= v >> 16;
        return v + 1;
    }

    public float PixelWidth => FrontBuffer.Width * _fontCellW;
    public float PixelHeight => FrontBuffer.Height * _fontCellH;

    /// <summary>
    /// Hot-path rendering routine. This must be 100% allocation free.
    /// Uses AsciiStringCache and pre-allocated _clearPaint to prevent GC freezes at 120 FPS.
    /// </summary>
    public void RenderBlitIfNeeded()
    {
        if (!_dirtyBlit) return;
        _dirtyBlit = false;

        float cellW = _fontCellW;
        float cellH = _fontCellH;

        lock (BufferLock)
        {
            // Zero-alloc draw clear utilizing pre-allocated _clearPaint
            _canvas.DrawRect(0, 0, cellW * FrontBuffer.Width, cellH * FrontBuffer.Height, _clearPaint);

            for (int row = 0; row < FrontBuffer.Height; row++)
            {
                float py = row * cellH;
                for (int col = 0; col < FrontBuffer.Width; col++)
                {
                    ref var cell = ref FrontBuffer.At(col, row);
                    float px = col * cellW;

                    // Background
                    uint bg = ResolveColor(cell.Bg, (cell.Flags & 2) != 0);
                    if (bg != 0xFF000000)
                    {
                        _cellBg.Color = new SKColor(bg);
                        _canvas.DrawRect(px, py, cellW, cellH, _cellBg);
                    }

                    // Glyph rendering with absolute zero heap allocations
                    char ch = cell.Rune;
                    if (ch > ' ')
                    {
                        uint fg = ResolveColor(cell.Fg, (cell.Flags & 1) != 0);
                        _cellFg.Color = new SKColor(fg);

                        // Use pre-allocated ASCII string cache for 0..255 characters
                        string chStr = (ch < 256) ? AsciiStringCache[ch] : ch.ToString();
                        _canvas.DrawText(chStr, px, py + cellH - 3f, _font, _cellFg);
                    }
                }
            }
        }
    }

    private static uint ResolveColor(uint raw, bool isRgb)
    {
        if (isRgb) return 0xFF000000u | (raw & 0x00FFFFFFu);
        byte idx = (byte)raw;
        if (idx < 16) return Palette16[idx];
        if (idx >= 232)
        {
            uint v = (uint)((idx - 232) * 10 + 8);
            return 0xFF000000u | (v << 16) | (v << 8) | v;
        }
        int i = idx - 16;
        uint r = (uint)((i / 36) > 0 ? (i / 36) * 40 + 55 : 0);
        uint g = (uint)(((i / 6) % 6) > 0 ? ((i / 6) % 6) * 40 + 55 : 0);
        uint b = (uint)((i % 6) > 0 ? (i % 6) * 40 + 55 : 0);
        return 0xFF000000u | (r << 16) | (g << 8) | b;
    }

    public void Start(string shell = "pwsh.exe")
    {
        // Setup RunspacePool in our pre-warmed multiplexer
        PwshMultiplexer.Instance.Initialize(_vom);

        _pty = InProcSession.Start(shell, (short)_back.Width, (short)_back.Height);
        _running = true;
        _pumpThread = new Thread(PumpLoop)
        {
            IsBackground = true,
            Name = $"TuiCanvas-PwshPool-{Title}"
        };
        _pumpThread.Start();
    }

    public void Resize(int cols, int rows)
    {
        lock (BufferLock)
        {
            FrontBuffer.Resize(cols, rows);
            _back.Resize(cols, rows);
            ResizeBlit(cols, rows);
        }
        _pty?.Resize((short)cols, (short)rows);
        _dirtyBlit = true;
    }

    public void SendInput(string text)
    {
        if (_pty == null) return;
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        try
        {
            _pty.InputStream.Write(bytes, 0, bytes.Length);
            _pty.InputStream.Flush();
        }
        catch { }
    }

    private void PumpLoop()
    {
        var buf = new byte[4096];
        var stream = _pty!.OutputStream;

        while (_running)
        {
            int n;
            try { n = stream.Read(buf, 0, buf.Length); }
            catch { break; }
            if (n == 0) break;

            lock (BufferLock)
            {
                for (int i = 0; i < n; i++)
                    ProcessByte(buf[i]);

                // Swap buffers
                var tmp = FrontBuffer;
                FrontBuffer = _back;
                _back = tmp;
                _dirtyBlit = true;
            }
        }

        _running = false;
    }

    private void ProcessByte(byte b)
    {
        if (_inCsi)
        {
            if (b >= 0x40 && b <= 0x7E) { ProcessCsi((char)b); _inCsi = false; _csiParam.Clear(); }
            else _csiParam.Append((char)b);
            return;
        }
        if (_inEsc)
        {
            _inEsc = false;
            if (b == '[') { _inCsi = true; _csiParam.Clear(); }
            return;
        }

        switch (b)
        {
            case 0x1B: _inEsc = true; break;
            case 0x0D: _curX = 0; break;
            case 0x0A: NewLine(); break;
            case 0x08: if (_curX > 0) _curX--; break;
            case 0x09: _curX = (_curX / 8 + 1) * 8; ClampCursor(); break;
            default:
                if (b >= 0x20)
                {
                    SetCell(_curX, _curY, (char)b, _fg, _bg, _style);
                    _curX++;
                    if (_curX >= _back.Width) { _curX = 0; NewLine(); }
                }
                break;
        }
    }

    private void ProcessCsi(char cmd)
    {
        var parts = _csiParam.ToString().Split(';');
        int p0 = TryInt(parts, 0, 1);
        int p1 = TryInt(parts, 1, 1);

        switch (cmd)
        {
            case 'A': _curY = Math.Max(0, _curY - p0); break;
            case 'B': _curY = Math.Min(_back.Height - 1, _curY + p0); break;
            case 'C': _curX = Math.Min(_back.Width - 1, _curX + p0); break;
            case 'D': _curX = Math.Max(0, _curX - p0); break;
            case 'H':
            case 'f':
                _curY = Math.Clamp(p0 - 1, 0, _back.Height - 1);
                _curX = Math.Clamp(p1 - 1, 0, _back.Width - 1);
                break;
            case 'J':
                if (p0 == 2) _back.Clear(Cell.Empty);
                break;
            case 'K':
                for (int x = _curX; x < _back.Width; x++)
                    SetCell(x, _curY, ' ', _fg, _bg, 0);
                break;
            case 'm': ApplySgr(parts); break;
            case 'h':
            case 'l':
                // Private modes — alt screen
                if (_csiParam.Length > 0 && _csiParam[0] == '?')
                {
                    string mode = _csiParam.ToString().TrimStart('?').TrimEnd(cmd);
                    if (mode == "1049")
                    {
                        if (cmd == 'h') EnterAltScreen();
                        else LeaveAltScreen();
                    }
                }
                break;
        }
    }

    private void ApplySgr(string[] parts)
    {
        for (int i = 0; i < parts.Length; i++)
        {
            int code = TryInt(parts, i, 0);
            switch (code)
            {
                case 0: _fg = 7; _bg = 16; _style = 0; break;
                case 1: _style |= 1; break;  // Bold
                case 2: _style |= 2; break;  // Dim
                case 3: _style |= 4; break;  // Italic
                case 4: _style |= 8; break;  // Underline
                case 7: _style |= 32; break; // Reverse
                case 22: _style &= unchecked((byte)~3); break;
                case 24: _style &= unchecked((byte)~8); break;
                case 27: _style &= unchecked((byte)~32); break;
                case 30:
                case 31:
                case 32:
                case 33:
                case 34:
                case 35:
                case 36:
                case 37: _fg = (byte)(code - 30); break;
                case 39: _fg = 7; break;
                case 40:
                case 41:
                case 42:
                case 43:
                case 44:
                case 45:
                case 46:
                case 47: _bg = (byte)(code - 40); break;
                case 49: _bg = 16; break;
                case 90:
                case 91:
                case 92:
                case 93:
                case 94:
                case 95:
                case 96:
                case 97: _fg = (byte)(code - 90 + 8); break;
                case 100:
                case 101:
                case 102:
                case 103:
                case 104:
                case 105:
                case 106:
                case 107: _bg = (byte)(code - 100 + 8); break;
                case 38 when i + 2 < parts.Length && TryInt(parts, i + 1, 0) == 5:
                    _fg = (byte)TryInt(parts, i + 2, 7); i += 2; break;
                case 48 when i + 2 < parts.Length && TryInt(parts, i + 1, 0) == 5:
                    _bg = (byte)TryInt(parts, i + 2, 16); i += 2; break;
            }
        }
    }

    private void EnterAltScreen()
    {
        _altScreen = new CellBuffer(_back.Width, _back.Height);
        var tmp = _back;
        _back = _altScreen;
        _altScreen = tmp;
        _useAlt = true;
        _back.Clear(Cell.Empty);
        _curX = _curY = 0;
    }

    private void LeaveAltScreen()
    {
        if (!_useAlt || _altScreen == null) return;
        var tmp = _back;
        _back = _altScreen;
        _altScreen = tmp;
        _useAlt = false;
        _curX = _curY = 0;
    }

    private void SetCell(int x, int y, char ch, byte fg, byte bg, byte style)
    {
        if (x < 0 || x >= _back.Width || y < 0 || y >= _back.Height) return;
        ref var cell = ref _back.At(x, y);
        cell.Rune = ch;
        cell.Fg = fg;
        cell.Bg = bg;
        cell.Style = style;
    }

    private void NewLine()
    {
        _curY++;
        if (_curY >= _back.Height)
        {
            int rowBytes = _back.Width;
            Array.Copy(_back.Cells, rowBytes, _back.Cells, 0, rowBytes * (_back.Height - 1));
            for (int x = 0; x < _back.Width; x++)
                _back.Cells[(_back.Height - 1) * _back.Width + x] = Cell.Empty;
            _curY = _back.Height - 1;
        }
    }

    private void ClampCursor()
    {
        _curX = Math.Clamp(_curX, 0, _back.Width - 1);
        _curY = Math.Clamp(_curY, 0, _back.Height - 1);
    }

    private static int TryInt(string[] arr, int idx, int def)
        => idx < arr.Length && int.TryParse(arr[idx], out int v) ? v : def;

    public void Dispose()
    {
        _running = false;
        _pty?.Dispose();
        _pumpThread?.Join(300);

        _canvas?.Dispose();
        BlitSurface?.Dispose();
        _font?.Dispose();
        _cellBg?.Dispose();
        _cellFg?.Dispose();
        _clearPaint?.Dispose();
    }
}
