using System;
using System.IO;
using System.Text;
using System.Threading;
using TuiDwm.Core;

namespace TuiDwm.Plugins.Terminal;

// Self-contained VT terminal plugin. No external parser dependency.
//
// Architecture:
//   PTY pump thread  →  ProcessByte()  →  _screen (CellBuffer)
//   Compositor thread  ←  Render()  ←  Blit from _screen under _lock
//
// VT coverage: printable, CR/LF/BS/TAB, CSI cursor/erase/SGR (including 256-color),
// alt screen (\x1b[?1049h/l). All state held in _screen + cursor fields.
public class TerminalPlugin : ITuiPlugin
{
    private Vom?       _vom;
    private string     _basePath = "";
    private PtySession? _pty;
    private readonly object _lock = new();

    private int        _width  = 80;
    private int        _height = 24;
    private CellBuffer _screen = new(80, 24);

    // ── VT parser state ───────────────────────────────────────────────────
    private int    _curX, _curY;
    private byte   _fg = 7, _bg = 16;
    private byte   _style;
    private bool   _inEsc;
    private bool   _inCsi;
    private readonly StringBuilder _csiParam = new(32);

    // Alt screen
    private CellBuffer? _altScreen;
    private bool        _useAlt;

    // ── Plugin interface ──────────────────────────────────────────────────
    public Vomt GetTemplate() => new Vomt
    {
        PluginName = "Terminal",
        Entries = new()
        {
            ("\\Title",     "PowerShell"),
            ("\\MinWidth",  40),
            ("\\MinHeight", 10),
            ("\\Focused",   false),
            ("\\Visible",   true),
        }
    };

    public void Initialize(Vom vom, string basePath)
    {
        _vom      = vom;
        _basePath = basePath;

        int w = Math.Max(10, vom.Get<int>($"{basePath}\\Width",  60) - 2);
        int h = Math.Max(5,  vom.Get<int>($"{basePath}\\Height", 15) - 3);

        lock (_lock)
        {
            _width  = w;
            _height = h;
            _screen = new CellBuffer(w, h);
        }

        StartPty();
    }

    private void StartPty()
    {
        string shell = ResolveShell();
        string commandLine = shell.Contains("pwsh") || shell.Contains("powershell")
            ? $"{shell} -NoExit -NoLogo"
            : shell;

        _vom?.Set($"{_basePath}\\Title", $"Terminal ({Path.GetFileNameWithoutExtension(shell)})");

        try
        {
            _pty = PtySession.Start(commandLine, (short)_width, (short)_height);
            new Thread(ReadPtyLoop) { IsBackground = true, Name = "Terminal-PTY" }.Start();
        }
        catch (Exception ex)
        {
            lock (_lock) { WriteStr($"\r\nFailed to start PTY: {ex.Message}\r\n"); }
        }
    }

    private static string ResolveShell()
    {
        string[] candidates = {
            @"C:\bin\pwsh\pwsh.exe",
            @"C:\Program Files\PowerShell\7\pwsh.exe",
        };
        foreach (var p in candidates)
            if (File.Exists(p)) return p;

        foreach (var exe in new[] { "pwsh.exe", "powershell.exe", "cmd.exe" })
        {
            if (File.Exists(exe)) return exe;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                var full = Path.Combine(dir, exe);
                if (File.Exists(full)) return full;
            }
        }
        return "cmd.exe";
    }

    // ── PTY pump (dedicated background thread) ────────────────────────────
    private void ReadPtyLoop()
    {
        var stream = _pty?.OutputStream;
        if (stream == null) return;

        var buf = new byte[4096];

        // PowerShell DSR/CPR handshake — unblocks the initial prompt
        byte[] dsr = { 0x1B, 0x5B, 0x36, 0x6E };
        byte[] cpr = Encoding.UTF8.GetBytes("\x1b[1;1R");

        try
        {
            while (true)
            {
                int n = stream.Read(buf, 0, buf.Length);
                if (n == 0) break; // EOF — process exited

                if (IndexOf(buf, n, dsr) >= 0)
                {
                    try { _pty?.InputStream.Write(cpr, 0, cpr.Length); _pty?.InputStream.Flush(); }
                    catch { }
                }

                lock (_lock)
                {
                    for (int i = 0; i < n; i++)
                        ProcessByte(buf[i]);
                }
            }
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            lock (_lock) { WriteStr($"\r\n[Terminal error: {ex.Message}]\r\n"); }
        }
    }

    // ── Minimal VT parser ─────────────────────────────────────────────────
    private void ProcessByte(byte b)
    {
        if (_inCsi)
        {
            if (b >= 0x40 && b <= 0x7E) { ProcessCsi((char)b); _inCsi = false; _csiParam.Clear(); }
            else _csiParam.Append((char)b);
            return;
        }
        if (_inEsc) { _inEsc = false; if (b == '[') { _inCsi = true; _csiParam.Clear(); } return; }

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
                    SetCell(_curX, _curY, (char)b);
                    if (++_curX >= _screen.Width) { _curX = 0; NewLine(); }
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
            case 'B': _curY = Math.Min(_screen.Height - 1, _curY + p0); break;
            case 'C': _curX = Math.Min(_screen.Width  - 1, _curX + p0); break;
            case 'D': _curX = Math.Max(0, _curX - p0); break;
            case 'H': case 'f':
                _curY = Math.Clamp(p0 - 1, 0, _screen.Height - 1);
                _curX = Math.Clamp(p1 - 1, 0, _screen.Width  - 1);
                break;
            case 'J':
                EraseDisplay(p0);
                break;
            case 'K':
                EraseLine(p0);
                break;
            case 'P': // DCH — delete characters
                DeleteChars(p0);
                break;
            case 'S': // SU — scroll up
                for (int i = 0; i < p0; i++) ScrollUp();
                break;
            case 'T': // SD — scroll down
                for (int i = 0; i < p0; i++) ScrollDown();
                break;
            case 'm': ApplySgr(parts); break;
            case 'h': case 'l':
                if (_csiParam.Length > 0 && _csiParam[0] == '?')
                {
                    string mode = _csiParam.ToString().TrimStart('?').TrimEnd(cmd);
                    if (mode == "1049") { if (cmd == 'h') EnterAltScreen(); else LeaveAltScreen(); }
                    if (mode == "25") { /* cursor visibility — ignored */ }
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
                case 0:  _fg = 7; _bg = 16; _style = 0; break;
                case 1:  _style |= 1; break;   // bold
                case 2:  _style |= 2; break;   // dim
                case 3:  _style |= 4; break;   // italic
                case 4:  _style |= 8; break;   // underline
                case 7:  _style |= 32; break;  // reverse
                case 22: _style &= unchecked((byte)~3); break;
                case 24: _style &= unchecked((byte)~8); break;
                case 27: _style &= unchecked((byte)~32); break;
                case int c when c >= 30 && c <= 37: _fg = (byte)(c - 30); break;
                case 39: _fg = 7; break;
                case int c when c >= 40 && c <= 47: _bg = (byte)(c - 40); break;
                case 49: _bg = 16; break;
                case int c when c >= 90 && c <= 97:  _fg = (byte)(c - 90 + 8); break;
                case int c when c >= 100 && c <= 107: _bg = (byte)(c - 100 + 8); break;
                // 256-color fg: ESC[38;5;Nm
                case 38 when i + 2 < parts.Length && TryInt(parts, i + 1, -1) == 5:
                    _fg = (byte)TryInt(parts, i + 2, 7); i += 2; break;
                // 256-color bg: ESC[48;5;Nm
                case 48 when i + 2 < parts.Length && TryInt(parts, i + 1, -1) == 5:
                    _bg = (byte)TryInt(parts, i + 2, 16); i += 2; break;
                // Truecolor fg: ESC[38;2;R;G;Bm — skip (no RGB in byte palette)
                case 38 when i + 4 < parts.Length && TryInt(parts, i + 1, -1) == 2:
                    i += 4; break;
                // Truecolor bg
                case 48 when i + 4 < parts.Length && TryInt(parts, i + 1, -1) == 2:
                    i += 4; break;
            }
        }
    }

    // ── Screen operations ─────────────────────────────────────────────────
    private void SetCell(int x, int y, char ch)
    {
        if (x < 0 || x >= _screen.Width || y < 0 || y >= _screen.Height) return;
        ref var c = ref _screen.At(x, y);
        c.Rune = ch;
        c.Fg   = (_style & 32) != 0 ? _bg : _fg;
        c.Bg   = (_style & 32) != 0 ? _fg : _bg;
    }

    private void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0: // cursor to end
                for (int x = _curX; x < _screen.Width; x++) SetCell(x, _curY, ' ');
                for (int y = _curY + 1; y < _screen.Height; y++)
                    for (int x = 0; x < _screen.Width; x++) SetCell(x, y, ' ');
                break;
            case 1: // start to cursor
                for (int y = 0; y < _curY; y++)
                    for (int x = 0; x < _screen.Width; x++) SetCell(x, y, ' ');
                for (int x = 0; x <= _curX; x++) SetCell(x, _curY, ' ');
                break;
            case 2: case 3: _screen.Clear(Cell.Empty); break;
        }
    }

    private void EraseLine(int mode)
    {
        switch (mode)
        {
            case 0: for (int x = _curX; x < _screen.Width; x++) SetCell(x, _curY, ' '); break;
            case 1: for (int x = 0; x <= _curX; x++) SetCell(x, _curY, ' '); break;
            case 2: for (int x = 0; x < _screen.Width; x++) SetCell(x, _curY, ' '); break;
        }
    }

    private void DeleteChars(int n)
    {
        int w = _screen.Width;
        if (_curX >= w) return;
        int count = Math.Min(n, w - _curX);
        for (int x = _curX; x < w - count; x++)
            _screen.At(x, _curY) = _screen.At(x + count, _curY);
        for (int x = w - count; x < w; x++)
            _screen.At(x, _curY) = Cell.Empty;
    }

    private void NewLine()
    {
        _curY++;
        if (_curY >= _screen.Height) { ScrollUp(); _curY = _screen.Height - 1; }
    }

    private void ScrollUp()
    {
        int w = _screen.Width;
        Array.Copy(_screen.Cells, w, _screen.Cells, 0, w * (_screen.Height - 1));
        for (int x = 0; x < w; x++)
            _screen.Cells[(_screen.Height - 1) * w + x] = Cell.Empty;
    }

    private void ScrollDown()
    {
        int w = _screen.Width;
        Array.Copy(_screen.Cells, 0, _screen.Cells, w, w * (_screen.Height - 1));
        for (int x = 0; x < w; x++)
            _screen.Cells[x] = Cell.Empty;
    }

    private void EnterAltScreen()
    {
        _altScreen = new CellBuffer(_screen.Width, _screen.Height);
        (_screen, _altScreen) = (_altScreen, _screen);
        _useAlt = true;
        _screen.Clear(Cell.Empty);
        _curX = _curY = 0;
    }

    private void LeaveAltScreen()
    {
        if (!_useAlt || _altScreen == null) return;
        (_screen, _altScreen) = (_altScreen, _screen);
        _useAlt = false;
        _curX = _curY = 0;
    }

    private void ClampCursor()
    {
        _curX = Math.Clamp(_curX, 0, Math.Max(0, _screen.Width  - 1));
        _curY = Math.Clamp(_curY, 0, Math.Max(0, _screen.Height - 1));
    }

    private void WriteStr(string s)
    {
        foreach (char c in s) ProcessByte((byte)c);
    }

    private static int TryInt(string[] a, int i, int def)
        => i < a.Length && int.TryParse(a[i], out int v) ? v : def;

    private static int IndexOf(byte[] buf, int len, byte[] pat)
    {
        int limit = len - pat.Length;
        for (int i = 0; i <= limit; i++)
        {
            bool ok = true;
            for (int j = 0; j < pat.Length; j++) if (buf[i + j] != pat[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    // ── Render ────────────────────────────────────────────────────────────
    public void Render(CellBuffer buffer)
    {
        lock (_lock)
        {
            buffer.Clear(Cell.Empty);
            buffer.Blit(_screen, 0, 0, _screen.Width, _screen.Height, 0, 0);

            bool focused = _vom?.Get<bool>($"{_basePath}\\Focused", false) ?? false;
            if (focused && _curX < buffer.Width && _curY < buffer.Height)
            {
                ref var cur = ref buffer.At(_curX, _curY);
                cur.Rune = '█';
                cur.Fg   = 51;
            }
        }
    }

    // ── Input ─────────────────────────────────────────────────────────────
    public void HandleInput(ConsoleKeyInfo key)
    {
        if (_pty == null || _pty.IsDisposed || _pty.HasExited) return;
        try
        {
            string seq = key.Key switch
            {
                ConsoleKey.Enter      => "\r",
                ConsoleKey.Backspace  => "\x7f",
                ConsoleKey.Tab        => "\t",
                ConsoleKey.Escape     => "\x1b",
                ConsoleKey.UpArrow    => "\x1b[A",
                ConsoleKey.DownArrow  => "\x1b[B",
                ConsoleKey.RightArrow => "\x1b[C",
                ConsoleKey.LeftArrow  => "\x1b[D",
                ConsoleKey.Delete     => "\x1b[3~",
                ConsoleKey.Home       => "\x1b[H",
                ConsoleKey.End        => "\x1b[F",
                ConsoleKey.PageUp     => "\x1b[5~",
                ConsoleKey.PageDown   => "\x1b[6~",
                _ => key.KeyChar != '\0' ? key.KeyChar.ToString() : ""
            };
            if (seq.Length == 0) return;
            byte[] bytes = Encoding.UTF8.GetBytes(seq);
            _pty.InputStream.Write(bytes, 0, bytes.Length);
            _pty.InputStream.Flush();
        }
        catch { }
    }

    // ── Resize ────────────────────────────────────────────────────────────
    public void OnResize(int newWidth, int newHeight)
    {
        if (newWidth <= 0 || newHeight <= 0) return;
        lock (_lock)
        {
            _width  = newWidth;
            _height = newHeight;
            _screen.Resize(newWidth, newHeight);
            _altScreen?.Resize(newWidth, newHeight);
        }
        _pty?.Resize((short)newWidth, (short)newHeight);
    }
}
