namespace NativePwshConsole;

internal readonly record struct Cell(char Rune, uint Foreground, uint Background);
internal readonly record struct CellLine(Cell[] Cells);

internal sealed class CellBuffer
{
    private readonly List<List<Cell>> _lines = [new()];
    private readonly object _gate = new();
    private readonly List<char> _csi = new();
    private bool _inEscape;
    private bool _inCsi;
    private int _scrollOffset;
    private uint _foreground = 0xfff5f5f5;
    private uint _background;
    public int MaxLines { get; set; } = 2000;

    public void Write(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_gate)
        {
            foreach (char ch in text.Replace("\r\n", "\n"))
            {
                if (_inCsi)
                {
                    if (ch is >= '@' and <= '~')
                    {
                        if (ch == 'm') ApplySgr(new string(_csi.ToArray()));
                        _csi.Clear();
                        _inCsi = false;
                    }
                    else _csi.Add(ch);
                    continue;
                }
                if (_inEscape)
                {
                    _inEscape = false;
                    if (ch == '[') { _inCsi = true; _csi.Clear(); }
                    continue;
                }
                if (ch == '\x1b') { _inEscape = true; continue; }
                if (ch == '\r') continue;
                if (ch == '\n') _lines.Add(new List<Cell>());
                else if (ch == '\b')
                {
                    if (_lines[^1].Count > 0) _lines[^1].RemoveAt(_lines[^1].Count - 1);
                }
                else if (ch >= ' ') _lines[^1].Add(new Cell(ch, _foreground, _background));
            }
            if (_lines.Count > MaxLines) _lines.RemoveRange(0, _lines.Count - MaxLines);
            _scrollOffset = 0;
        }
    }

    public CellLine[] Snapshot(int rows, int columns)
    {
        lock (_gate)
        {
            var wrapped = new List<CellLine>();
            foreach (List<Cell> line in _lines)
            {
                if (line.Count == 0) { wrapped.Add(new CellLine([])); continue; }
                for (int i = 0; i < line.Count; i += columns)
                    wrapped.Add(new CellLine(line.Skip(i).Take(Math.Min(columns, line.Count - i)).ToArray()));
            }
            int maxOffset = Math.Max(0, wrapped.Count - rows);
            _scrollOffset = Math.Clamp(_scrollOffset, 0, maxOffset);
            int end = Math.Max(0, wrapped.Count - _scrollOffset);
            int start = Math.Max(0, end - rows);
            return wrapped.Skip(start).Take(end - start).ToArray();
        }
    }

    public void ScrollRows(int rows, int viewportRows, int columns)
    {
        lock (_gate)
        {
            int wrappedCount = _lines.Sum(line => Math.Max(1, (line.Count + columns - 1) / columns));
            _scrollOffset = Math.Clamp(_scrollOffset + rows, 0, Math.Max(0, wrappedCount - viewportRows));
        }
    }

    private void ApplySgr(string value)
    {
        int[] p = value.Length == 0 ? [0] : value.Split(';').Select(x => int.TryParse(x, out int n) ? n : 0).ToArray();
        for (int i = 0; i < p.Length; i++)
        {
            int n = p[i];
            if (n == 0) { _foreground = 0xfff5f5f5; _background = 0; }
            else if (n == 39) _foreground = 0xfff5f5f5;
            else if (n == 49) _background = 0;
            else if (n is >= 30 and <= 37) _foreground = Basic(n - 30, false);
            else if (n is >= 90 and <= 97) _foreground = Basic(n - 90, true);
            else if (n == 38 && i + 2 < p.Length && p[i + 1] == 5) { _foreground = Xterm(p[i + 2]); i += 2; }
            else if (n == 38 && i + 4 < p.Length && p[i + 1] == 2)
            { _foreground = Argb(p[i + 2], p[i + 3], p[i + 4]); i += 4; }
        }
    }

    private static uint Basic(int i, bool bright)
    {
        uint[] normal = [0xff0c0c0c,0xffc50f1f,0xff13a10e,0xffc19c00,0xff0037da,0xff881798,0xff3a96dd,0xffcccccc];
        uint[] light = [0xff767676,0xffe74856,0xff16c60c,0xfff9f1a5,0xff3b78ff,0xffb4009e,0xff61d6d6,0xfff2f2f2];
        return (bright ? light : normal)[Math.Clamp(i, 0, 7)];
    }

    private static uint Xterm(int n)
    {
        if (n < 16) return Basic(n & 7, n >= 8);
        if (n < 232)
        {
            int v = n - 16, r = v / 36, g = (v / 6) % 6, b = v % 6;
            int C(int x) => x == 0 ? 0 : 55 + x * 40;
            return Argb(C(r), C(g), C(b));
        }
        int gray = 8 + (Math.Clamp(n, 232, 255) - 232) * 10;
        return Argb(gray, gray, gray);
    }

    private static uint Argb(int r, int g, int b) => 0xff000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b;
}
