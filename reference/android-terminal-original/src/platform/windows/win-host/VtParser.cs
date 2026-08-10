using System.Runtime.InteropServices;
using System.Text;

namespace WinHost;

/// <summary>
/// VT100/VT220 state machine. Maintains a packed cell grid for direct GPU upload.
/// Cell encoding: (bgIdx << 24) | (fgIdx << 16) | charCode  ← terminal_grid.wgsl contract
/// </summary>
public sealed class VtParser
{
    // ── Public state ──────────────────────────────────────────────────────────

    public int  Cols          { get; private set; }
    public int  Rows          { get; private set; }
    public int  CursorCol     { get; private set; }
    public int  CursorRow     { get; private set; }
    public bool CursorVisible { get; private set; } = true;

    public ReadOnlySpan<uint> Grid => _grid.AsSpan(0, Cols * Rows);

    // ── Private state ─────────────────────────────────────────────────────────

    private uint[] _grid;
    private int  _fg      = 7;    // ANSI palette index (default = light gray)
    private int  _bg      = 0;    // ANSI palette index (default = black)
    private bool _reverse = false;

    private enum State { Ground, Esc, Csi, Osc, Charset }
    private State _state    = State.Ground;
    private string _csiBuf  = "";
    private string _oscBuf  = "";

    // ── Construction ──────────────────────────────────────────────────────────

    public VtParser(int cols, int rows)
    {
        Cols = cols; Rows = rows;
        _grid = new uint[cols * rows];
        FillGrid(BlankCell());
    }

    public void Resize(int cols, int rows)
    {
        var next = new uint[cols * rows];
        Array.Fill(next, BlankCell());
        int copyRows = Math.Min(rows, Rows);
        int copyCols = Math.Min(cols, Cols);
        for (int r = 0; r < copyRows; r++)
            for (int c = 0; c < copyCols; c++)
                next[r * cols + c] = _grid[r * Cols + c];
        Cols = cols; Rows = rows; _grid = next;
        CursorCol = Math.Min(CursorCol, cols - 1);
        CursorRow = Math.Min(CursorRow, rows - 1);
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    public void Write(ReadOnlySpan<byte> bytes) => Write(Encoding.UTF8.GetString(bytes));

    public void Write(string text)
    {
        foreach (char ch in text)
            switch (_state)
            {
                case State.Ground:  Ground(ch);              break;
                case State.Esc:     Esc(ch);                 break;
                case State.Csi:     Csi(ch);                 break;
                case State.Osc:     Osc(ch);                 break;
                case State.Charset: _state = State.Ground;   break;
            }
    }

    // ── State handlers ────────────────────────────────────────────────────────

    private void Ground(char ch)
    {
        switch (ch)
        {
            case '\x1b': _state = State.Esc; return;
            case '\r':   CursorCol = 0; return;
            case '\n': case '\x0b': case '\x0c': AdvRow(); return;
            case '\b': if (CursorCol > 0) CursorCol--; return;
            case '\t': CursorCol = Math.Min(Cols - 1, (CursorCol / 8 + 1) * 8); return;
            case '\x00': case '\x07': case '\x0e': case '\x0f': return;
        }
        int code = ch;
        if (code < 32 || code == 127) return;
        PutChar(code);
    }

    private void PutChar(int code)
    {
        if (CursorCol >= Cols) { CursorCol = 0; AdvRow(); }
        _grid[CursorRow * Cols + CursorCol] = MakeCell(code);
        CursorCol++;
    }

    private void AdvRow()
    {
        CursorRow++;
        if (CursorRow >= Rows) { ScrollUp(1); CursorRow = Rows - 1; }
    }

    private void Esc(char ch)
    {
        _state = State.Ground;
        switch (ch)
        {
            case '[': _state = State.Csi; _csiBuf = ""; break;
            case ']': _state = State.Osc; _oscBuf = ""; break;
            case '(': case ')': _state = State.Charset; break;
            case 'M': if (CursorRow > 0) CursorRow--; break;  // RI
            case 'c': HardReset(); break;                      // RIS
        }
    }

    private void Csi(char ch)
    {
        if (ch is >= '\x20' and <= '\x7e')
        {
            _csiBuf += ch;
            if (ch is >= '@' and <= '~')
            { ApplyCsi(_csiBuf); _state = State.Ground; _csiBuf = ""; }
        }
        else { _state = State.Ground; _csiBuf = ""; }
    }

    private void Osc(char ch)
    {
        if (ch is '\x07' or '\\') _state = State.Ground;
        else _oscBuf += ch;
    }

    // ── CSI dispatch ──────────────────────────────────────────────────────────

    private void ApplyCsi(string seq)
    {
        char   cmd  = seq[^1];
        string raw  = seq[..^1];
        bool   priv = raw.Length > 0 && raw[0] == '?';
        string ps   = priv ? raw[1..] : raw;

        int[] p = ps.Length == 0 ? [] :
            [..ps.Split(';').Select(s => int.TryParse(s, out int v) ? v : 0)];

        int P(int i, int def = 0) => i < p.Length ? p[i] : def;
        int P1(int def = 1)       => p.Length > 0 && p[0] != 0 ? p[0] : def;

        switch (cmd)
        {
            case 'm': Sgr(p); break;
            case 'A': CursorRow = Math.Max(0, CursorRow - P1()); break;
            case 'B': CursorRow = Math.Min(Rows - 1, CursorRow + P1()); break;
            case 'C': CursorCol = Math.Min(Cols - 1, CursorCol + P1()); break;
            case 'D': CursorCol = Math.Max(0, CursorCol - P1()); break;
            case 'E': CursorRow = Math.Min(Rows - 1, CursorRow + P1()); CursorCol = 0; break;
            case 'F': CursorRow = Math.Max(0, CursorRow - P1()); CursorCol = 0; break;
            case 'G': CursorCol = Math.Clamp(P1(1) - 1, 0, Cols - 1); break;
            case 'H': case 'f':
                CursorRow = Math.Clamp(P(0, 1) - 1, 0, Rows - 1);
                CursorCol = Math.Clamp(P(1, 1) - 1, 0, Cols - 1);
                break;
            case 'J': EraseDisplay(P(0)); break;
            case 'K': EraseLine(P(0));    break;
            case 'S': ScrollUp(P1());     break;
            case 'T': ScrollDown(P1());   break;
            case 'd': CursorRow = Math.Clamp(P1(1) - 1, 0, Rows - 1); break;
            case 'h': if (priv && P(0) == 25) CursorVisible = true;  break;
            case 'l': if (priv && P(0) == 25) CursorVisible = false; break;
            case 'n': break;  // DSR — host handles CPR reply
            case 'r': break;  // DECSTBM — TODO: scroll regions
        }
    }

    // ── SGR ───────────────────────────────────────────────────────────────────

    private void Sgr(int[] p)
    {
        if (p.Length == 0 || (p.Length == 1 && p[0] == 0))
        { _fg = 7; _bg = 0; _reverse = false; return; }

        for (int i = 0; i < p.Length; i++)
        {
            int v = p[i];
            switch (v)
            {
                case 0:  _fg = 7; _bg = 0; _reverse = false; break;
                case 1:  break;  // bold — no bold atlas; ignore
                case 3:  break;  // italic
                case 4:  break;  // underline — TODO
                case 7:  _reverse = true;  break;
                case 22: case 23: case 24: break;
                case 27: _reverse = false; break;
                case >= 30 and <= 37: _fg = v - 30; break;
                case 38:
                    if (i + 2 < p.Length && p[i + 1] == 5)
                    { _fg = Math.Min(p[i + 2], 15); i += 2; }   // 256→clamp to 16
                    else if (i + 4 < p.Length && p[i + 1] == 2)
                    { i += 4; }                                   // RGB truecolor → skip
                    break;
                case 39: _fg = 7; break;
                case >= 40 and <= 47: _bg = v - 40; break;
                case 48:
                    if (i + 2 < p.Length && p[i + 1] == 5)
                    { _bg = Math.Min(p[i + 2], 15); i += 2; }
                    else if (i + 4 < p.Length && p[i + 1] == 2)
                    { i += 4; }
                    break;
                case 49: _bg = 0; break;
                case >= 90 and <= 97:  _fg = v - 82; break;  // bright fg 8-15
                case >= 100 and <= 107: _bg = v - 92; break;  // bright bg 8-15
            }
        }
    }

    // ── Erase / scroll helpers ────────────────────────────────────────────────

    private void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0: Fill(CursorRow * Cols + CursorCol, Rows * Cols); break;
            case 1: Fill(0, CursorRow * Cols + CursorCol + 1); break;
            case 2: case 3: FillGrid(BlankCell()); break;
        }
    }

    private void EraseLine(int mode)
    {
        switch (mode)
        {
            case 0: Fill(CursorRow * Cols + CursorCol, (CursorRow + 1) * Cols); break;
            case 1: Fill(CursorRow * Cols, CursorRow * Cols + CursorCol + 1); break;
            case 2: Fill(CursorRow * Cols, (CursorRow + 1) * Cols); break;
        }
    }

    private void Fill(int from, int to)
    {
        uint b = BlankCell();
        to = Math.Min(to, _grid.Length);
        for (int i = from; i < to; i++) _grid[i] = b;
    }

    private void FillGrid(uint value) => Array.Fill(_grid, value, 0, Cols * Rows);

    private void ScrollUp(int n)
    {
        n = Math.Clamp(n, 1, Rows);
        Array.Copy(_grid, n * Cols, _grid, 0, (Rows - n) * Cols);
        Fill((Rows - n) * Cols, Rows * Cols);
    }

    private void ScrollDown(int n)
    {
        n = Math.Clamp(n, 1, Rows);
        Array.Copy(_grid, 0, _grid, n * Cols, (Rows - n) * Cols);
        Fill(0, n * Cols);
    }

    private void HardReset()
    {
        _fg = 7; _bg = 0; _reverse = false;
        CursorCol = CursorRow = 0; CursorVisible = true;
        _state = State.Ground;
        FillGrid(BlankCell());
    }

    // ── Cell packing ─────────────────────────────────────────────────────────

    private uint MakeCell(int charCode)
    {
        int fg = _reverse ? _bg : _fg;
        int bg = _reverse ? _fg : _bg;
        return ((uint)bg << 24) | ((uint)fg << 16) | (uint)charCode;
    }
    private uint BlankCell() => MakeCell(32);

    // ── Smoke test patterns ───────────────────────────────────────────────────

    public enum TestPattern { Grid, Colors, Charset, Noise }

    public void LoadTestPattern(TestPattern pattern)
    {
        HardReset();
        var rng = new Random(42);
        for (int r = 0; r < Rows; r++)
        for (int c = 0; c < Cols; c++)
        {
            _grid[r * Cols + c] = pattern switch
            {
                TestPattern.Grid    => GridPatternCell(r, c),
                TestPattern.Colors  => ColorsPatternCell(r, c),
                TestPattern.Charset => ((uint)0 << 24) | ((uint)15 << 16) | (uint)(32 + (r * Cols + c) % 95),
                TestPattern.Noise   => ((uint)(uint)rng.Next(16) << 24) | ((uint)rng.Next(16) << 16) | (uint)(33 + rng.Next(94)),
                _ => BlankCell()
            };
        }
    }

    private uint GridPatternCell(int r, int c)
    {
        bool edge = r == 0 || r == Rows - 1 || c == 0 || c == Cols - 1;
        int fg = edge ? 11 : ((r + c) % 2 == 0 ? 7 : 8);
        int bg = edge ? 1  : ((r + c) % 2 == 0 ? 0 : 4);
        int ch = edge ? '+' : ' ';
        return ((uint)bg << 24) | ((uint)fg << 16) | (uint)ch;
    }

    private uint ColorsPatternCell(int r, int c)
    {
        int fg = (c * 16 / Math.Max(Cols, 1)) % 16;
        int bg = (r * 8  / Math.Max(Rows, 1)) % 16;
        int ch = 'A' + fg;
        return ((uint)bg << 24) | ((uint)fg << 16) | (uint)ch;
    }
}
