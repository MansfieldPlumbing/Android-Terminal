using System.Text;
using System.Globalization;

namespace Terminal.Engine;

public sealed class TerminalEngine
{
    private enum ParserState { Ground, Escape, Csi, Osc, OscEscape }

    private sealed class Screen
    {
        public TerminalCell[][] Lines;
        public int CursorColumn;
        public int CursorRow;
        public int SavedColumn;
        public int SavedRow;
        public int ScrollTop;
        public int ScrollBottom;
        public bool WrapPending;

        public Screen(int columns, int rows)
        {
            Lines = CreateLines(columns, rows);
            ScrollBottom = rows - 1;
        }

        public void Resize(int columns, int rows)
        {
            var resized = CreateLines(columns, rows);
            int copyRows = Math.Min(rows, Lines.Length);
            int copyColumns = Math.Min(columns, Lines[0].Length);
            for (int row = 0; row < copyRows; row++)
                Array.Copy(Lines[row], resized[row], copyColumns);
            Lines = resized;
            CursorColumn = Math.Clamp(CursorColumn, 0, columns - 1);
            CursorRow = Math.Clamp(CursorRow, 0, rows - 1);
            SavedColumn = Math.Clamp(SavedColumn, 0, columns - 1);
            SavedRow = Math.Clamp(SavedRow, 0, rows - 1);
            ScrollTop = 0;
            ScrollBottom = rows - 1;
            WrapPending = false;
        }
    }

    private readonly object _gate = new();
    private readonly StringBuilder _sequence = new(64);
    private readonly List<TerminalCell[]> _scrollback = [];
    private readonly HashSet<int> _dirtyRows = [];
    private readonly Dictionary<int, string> _hyperlinks = [];
    private readonly List<TerminalEvent> _events = [];
    private Screen _primary;
    private Screen _alternate;
    private Screen _screen;
    private ParserState _parserState;
    private TerminalColor _foreground = TerminalColor.Default;
    private TerminalColor _background = TerminalColor.Default;
    private TerminalAttributes _attributes;
    private bool _cursorVisible = true;
    private bool _autoWrap = true;
    private bool _alternateActive;
    private int _activeHyperlink;
    private int _nextHyperlink = 1;
    private int _maxScrollback;
    private int _scrollbackOffset;
    private char? _pendingHighSurrogate;
    private long _revision;
    private string _composition = string.Empty;
    private int _compositionCaret;

    public TerminalEngine(int columns = 80, int rows = 24, int maxScrollback = 2000)
    {
        if (columns <= 0) throw new ArgumentOutOfRangeException(nameof(columns));
        if (rows <= 0) throw new ArgumentOutOfRangeException(nameof(rows));
        _maxScrollback = Math.Max(0, maxScrollback);
        _primary = new Screen(columns, rows);
        _alternate = new Screen(columns, rows);
        _screen = _primary;
        MarkAllDirty();
    }

    public event Action<TerminalEvent>? SemanticEvent;
    public event Action? Changed;

    public int Columns { get { lock (_gate) return _screen.Lines[0].Length; } }
    public int Rows { get { lock (_gate) return _screen.Lines.Length; } }
    public string Title { get; private set; } = string.Empty;

    public int MaxScrollback
    {
        get { lock (_gate) return _maxScrollback; }
        set
        {
            lock (_gate)
            {
                _maxScrollback = Math.Max(0, value);
                TrimScrollback();
            }
        }
    }

    public void Feed(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        TerminalEvent[] emitted;
        lock (_gate)
        {
            FeedCore(text.AsSpan());
            emitted = [.. _events];
            _events.Clear();
        }
        foreach (TerminalEvent terminalEvent in emitted)
            SemanticEvent?.Invoke(terminalEvent);
        Changed?.Invoke();
    }

    public void Resize(int columns, int rows)
    {
        if (columns <= 0 || rows <= 0) return;
        lock (_gate)
        {
            if (columns == Columns && rows == Rows) return;
            _primary.Resize(columns, rows);
            _alternate.Resize(columns, rows);
            for (int i = 0; i < _scrollback.Count; i++)
                _scrollback[i] = ResizeLine(_scrollback[i], columns);
            _scrollbackOffset = Math.Clamp(_scrollbackOffset, 0, _scrollback.Count);
            MarkAllDirty();
            _revision++;
        }
        Changed?.Invoke();
    }

    public void ScrollViewport(int rows)
    {
        if (rows == 0) return;
        lock (_gate)
        {
            if (_alternateActive) return;
            _scrollbackOffset = Math.Clamp(_scrollbackOffset + rows, 0, _scrollback.Count);
            MarkAllDirty();
            _revision++;
        }
        Changed?.Invoke();
    }

    public void SetSelection(TerminalPoint anchor, TerminalPoint active)
    {
        lock (_gate)
        {
            Selection = new TerminalSelection(anchor, active);
            MarkAllDirty();
            _revision++;
        }
        Changed?.Invoke();
    }

    public void ClearSelection()
    {
        lock (_gate)
        {
            if (Selection == null) return;
            Selection = null;
            MarkAllDirty();
            _revision++;
        }
        Changed?.Invoke();
    }

    public void SetComposition(string? text, int caretIndex)
    {
        lock (_gate)
        {
            _composition = text ?? string.Empty;
            _compositionCaret = Math.Clamp(caretIndex, 0, _composition.Length);
            MarkAllDirty();
            _revision++;
        }
        Changed?.Invoke();
    }

    public TerminalSelection? Selection { get; private set; }

    public string? GetHyperlink(int id)
    {
        lock (_gate) return _hyperlinks.TryGetValue(id, out string? uri) ? uri : null;
    }

    public TerminalSnapshot CaptureSnapshot(bool consumeDirtyRows = true)
    {
        lock (_gate)
        {
            int rows = _screen.Lines.Length;
            int columns = _screen.Lines[0].Length;
            TerminalCell[][] lines;
            if (_alternateActive || _scrollbackOffset == 0)
            {
                lines = CloneLines(_screen.Lines);
            }
            else
            {
                int total = _scrollback.Count + rows;
                int start = Math.Max(0, total - rows - _scrollbackOffset);
                lines = new TerminalCell[rows][];
                for (int destination = 0; destination < rows; destination++)
                {
                    int source = start + destination;
                    lines[destination] = source < _scrollback.Count
                        ? (TerminalCell[])_scrollback[source].Clone()
                        : source < total
                            ? (TerminalCell[])_screen.Lines[source - _scrollback.Count].Clone()
                            : BlankLine(columns);
                }
            }

            TerminalCursor cursor = new(_screen.CursorColumn, _screen.CursorRow,
                _cursorVisible && _scrollbackOffset == 0);
            if (!_alternateActive && _scrollbackOffset == 0 && _composition.Length > 0)
                cursor = OverlayComposition(lines, cursor.Visible);

            int[] dirty = [.. _dirtyRows.Order()];
            if (consumeDirtyRows) _dirtyRows.Clear();
            return new TerminalSnapshot(
                columns,
                rows,
                lines,
                cursor,
                Selection,
                _scrollbackOffset,
                _alternateActive,
                _revision,
                dirty);
        }
    }

    private void FeedCore(ReadOnlySpan<char> text)
    {
        int index = 0;
        if (_pendingHighSurrogate is char pending)
        {
            _pendingHighSurrogate = null;
            if (text.Length > 0 && char.IsLowSurrogate(text[0]))
            {
                WriteRune(new Rune(pending, text[0]));
                index = 1;
            }
            else WriteRune(Rune.ReplacementChar);
        }

        for (; index < text.Length; index++)
        {
            char character = text[index];
            if (_parserState == ParserState.Ground && char.IsHighSurrogate(character))
            {
                if (index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                {
                    WriteRune(new Rune(character, text[++index]));
                    continue;
                }
                if (index + 1 == text.Length)
                {
                    _pendingHighSurrogate = character;
                    continue;
                }
                WriteRune(Rune.ReplacementChar);
                continue;
            }

            if (_parserState == ParserState.Ground && char.IsLowSurrogate(character))
            {
                WriteRune(Rune.ReplacementChar);
                continue;
            }

            ProcessCharacter(character);
        }
    }

    private void ProcessCharacter(char character)
    {
        switch (_parserState)
        {
            case ParserState.Ground:
                ProcessGround(character);
                break;
            case ParserState.Escape:
                ProcessEscape(character);
                break;
            case ParserState.Csi:
                if (character is >= '@' and <= '~')
                {
                    ProcessCsi(_sequence.ToString(), character);
                    _sequence.Clear();
                    _parserState = ParserState.Ground;
                }
                else if (_sequence.Length < 256) _sequence.Append(character);
                else CancelSequence();
                break;
            case ParserState.Osc:
                if (character == '\a') FinishOsc();
                else if (character == '\x1b') _parserState = ParserState.OscEscape;
                else if (_sequence.Length < 4096) _sequence.Append(character);
                else CancelSequence();
                break;
            case ParserState.OscEscape:
                if (character == '\\') FinishOsc();
                else
                {
                    if (_sequence.Length < 4095) _sequence.Append('\x1b').Append(character);
                    _parserState = ParserState.Osc;
                }
                break;
        }
    }

    private void ProcessGround(char character)
    {
        switch (character)
        {
            case '\x1b':
                _parserState = ParserState.Escape;
                break;
            case '\a':
                Emit(new TerminalBellEvent());
                break;
            case '\b':
                _screen.CursorColumn = Math.Max(0, _screen.CursorColumn - 1);
                _screen.WrapPending = false;
                break;
            case '\t':
                _screen.CursorColumn = Math.Min(Columns - 1, (_screen.CursorColumn / 8 + 1) * 8);
                _screen.WrapPending = false;
                break;
            case '\r':
                _screen.CursorColumn = 0;
                _screen.WrapPending = false;
                break;
            case '\n':
            case '\v':
            case '\f':
                LineFeed();
                break;
            default:
                if (!char.IsControl(character)) WriteRune(new Rune(character));
                break;
        }
    }

    private void ProcessEscape(char character)
    {
        _parserState = ParserState.Ground;
        switch (character)
        {
            case '[':
                _sequence.Clear();
                _parserState = ParserState.Csi;
                break;
            case ']':
                _sequence.Clear();
                _parserState = ParserState.Osc;
                break;
            case '7': SaveCursor(); break;
            case '8': RestoreCursor(); break;
            case 'D': LineFeed(); break;
            case 'E': _screen.CursorColumn = 0; LineFeed(); break;
            case 'M': ReverseIndex(); break;
            case 'c': Reset(); break;
        }
    }

    private void ProcessCsi(string sequence, char final)
    {
        bool privateMode = sequence.StartsWith('?');
        if (privateMode) sequence = sequence[1..];
        int[] parameters = ParseParameters(sequence);
        int P(int index, int fallback = 1, bool zeroIsFallback = true)
        {
            if (index >= parameters.Length || parameters[index] < 0) return fallback;
            int value = parameters[index];
            return zeroIsFallback && value == 0 ? fallback : value;
        }

        switch (final)
        {
            case 'A': MoveCursor(0, -P(0)); break;
            case 'B': MoveCursor(0, P(0)); break;
            case 'C': MoveCursor(P(0), 0); break;
            case 'D': MoveCursor(-P(0), 0); break;
            case 'E': _screen.CursorColumn = 0; MoveCursor(0, P(0)); break;
            case 'F': _screen.CursorColumn = 0; MoveCursor(0, -P(0)); break;
            case 'G': SetCursor(P(0) - 1, _screen.CursorRow); break;
            case 'd': SetCursor(_screen.CursorColumn, P(0) - 1); break;
            case 'H':
            case 'f': SetCursor(P(1) - 1, P(0) - 1); break;
            case 'J': EraseDisplay(P(0, 0, false)); break;
            case 'K': EraseLine(P(0, 0, false)); break;
            case '@': InsertCharacters(P(0)); break;
            case 'P': DeleteCharacters(P(0)); break;
            case 'X': EraseCharacters(P(0)); break;
            case 'L': InsertLines(P(0)); break;
            case 'M': DeleteLines(P(0)); break;
            case 'S': ScrollUp(_screen.ScrollTop, _screen.ScrollBottom, P(0)); break;
            case 'T': ScrollDown(_screen.ScrollTop, _screen.ScrollBottom, P(0)); break;
            case 'm': ApplySgr(parameters); break;
            case 'r':
                int top = P(0) - 1;
                int bottom = P(1, Rows) - 1;
                if (top >= 0 && bottom < Rows && top < bottom)
                {
                    _screen.ScrollTop = top;
                    _screen.ScrollBottom = bottom;
                    SetCursor(0, 0);
                }
                break;
            case 's': SaveCursor(); break;
            case 'u': RestoreCursor(); break;
            case 'h': SetModes(parameters, privateMode, true); break;
            case 'l': SetModes(parameters, privateMode, false); break;
            case 'n':
                if (P(0, 0, false) == 6)
                    Emit(new TerminalReplyRequestedEvent($"\x1b[{_screen.CursorRow + 1};{_screen.CursorColumn + 1}R"));
                break;
        }
    }

    private void SetModes(int[] parameters, bool privateMode, bool enabled)
    {
        if (!privateMode) return;
        foreach (int mode in parameters)
        {
            switch (mode)
            {
                case 7: _autoWrap = enabled; _screen.WrapPending = false; break;
                case 25: _cursorVisible = enabled; MarkDirty(_screen.CursorRow); break;
                case 47:
                case 1047:
                case 1049:
                    if (enabled) EnterAlternateScreen(); else LeaveAlternateScreen();
                    break;
            }
        }
    }

    private void ApplySgr(int[] parameters)
    {
        if (parameters.Length == 0) parameters = [0];
        for (int index = 0; index < parameters.Length; index++)
        {
            int code = parameters[index] < 0 ? 0 : parameters[index];
            switch (code)
            {
                case 0: ResetRendition(); break;
                case 1: _attributes |= TerminalAttributes.Bold; break;
                case 2: _attributes |= TerminalAttributes.Dim; break;
                case 3: _attributes |= TerminalAttributes.Italic; break;
                case 4: _attributes |= TerminalAttributes.Underline; break;
                case 7: _attributes |= TerminalAttributes.Inverse; break;
                case 9: _attributes |= TerminalAttributes.Strike; break;
                case 22: _attributes &= ~(TerminalAttributes.Bold | TerminalAttributes.Dim); break;
                case 23: _attributes &= ~TerminalAttributes.Italic; break;
                case 24: _attributes &= ~TerminalAttributes.Underline; break;
                case 27: _attributes &= ~TerminalAttributes.Inverse; break;
                case 29: _attributes &= ~TerminalAttributes.Strike; break;
                case >= 30 and <= 37: _foreground = TerminalColor.Indexed((byte)(code - 30)); break;
                case 39: _foreground = TerminalColor.Default; break;
                case >= 40 and <= 47: _background = TerminalColor.Indexed((byte)(code - 40)); break;
                case 49: _background = TerminalColor.Default; break;
                case >= 90 and <= 97: _foreground = TerminalColor.Indexed((byte)(code - 90 + 8)); break;
                case >= 100 and <= 107: _background = TerminalColor.Indexed((byte)(code - 100 + 8)); break;
                case 38: index = ReadExtendedColor(parameters, index, ref _foreground); break;
                case 48: index = ReadExtendedColor(parameters, index, ref _background); break;
            }
        }
    }

    private static int ReadExtendedColor(int[] parameters, int index, ref TerminalColor destination)
    {
        if (index + 2 < parameters.Length && parameters[index + 1] == 5)
        {
            destination = TerminalColor.Indexed((byte)Math.Clamp(parameters[index + 2], 0, 255));
            return index + 2;
        }
        if (index + 4 < parameters.Length && parameters[index + 1] == 2)
        {
            destination = TerminalColor.Rgb(
                (byte)Math.Clamp(parameters[index + 2], 0, 255),
                (byte)Math.Clamp(parameters[index + 3], 0, 255),
                (byte)Math.Clamp(parameters[index + 4], 0, 255));
            return index + 4;
        }
        return index;
    }

    private void ProcessOsc(string content)
    {
        int separator = content.IndexOf(';');
        string command = separator < 0 ? content : content[..separator];
        string payload = separator < 0 ? string.Empty : content[(separator + 1)..];
        if (command is "0" or "2")
        {
            Title = payload;
            Emit(new TerminalTitleChangedEvent(payload));
            return;
        }
        if (command != "8") return;

        int uriSeparator = payload.IndexOf(';');
        string uri = uriSeparator < 0 ? string.Empty : payload[(uriSeparator + 1)..];
        if (string.IsNullOrEmpty(uri))
        {
            int closed = _activeHyperlink;
            _activeHyperlink = 0;
            Emit(new TerminalHyperlinkEvent(closed, null));
        }
        else
        {
            int id = _nextHyperlink++;
            _hyperlinks[id] = uri;
            _activeHyperlink = id;
            Emit(new TerminalHyperlinkEvent(id, uri));
        }
    }

    private void WriteRune(Rune rune)
    {
        int width = UnicodeWidth.Of(rune);
        if (width == 0)
        {
            AppendCombiningMark(rune.ToString());
            return;
        }

        int columns = Columns;
        if (_screen.WrapPending && _autoWrap)
        {
            _screen.CursorColumn = 0;
            LineFeed();
        }
        _screen.WrapPending = false;

        if (width == 2 && _screen.CursorColumn == columns - 1)
        {
            if (_autoWrap)
            {
                _screen.CursorColumn = 0;
                LineFeed();
            }
            else width = 1;
        }

        int column = _screen.CursorColumn;
        int row = _screen.CursorRow;
        ClearWideCell(row, column);
        var cell = new TerminalCell(rune.ToString(), (byte)width, _foreground, _background, _attributes, _activeHyperlink);
        _screen.Lines[row][column] = cell;
        if (width == 2 && column + 1 < columns)
        {
            ClearWideCell(row, column + 1);
            _screen.Lines[row][column + 1] = new TerminalCell(
                string.Empty, 0, _foreground, _background, _attributes, _activeHyperlink);
        }
        MarkDirty(row);
        _revision++;

        int next = column + width;
        if (next >= columns)
        {
            _screen.CursorColumn = columns - 1;
            _screen.WrapPending = _autoWrap;
        }
        else _screen.CursorColumn = next;
    }

    private TerminalCursor OverlayComposition(TerminalCell[][] lines, bool visible)
    {
        int row = _screen.CursorRow;
        int column = _screen.CursorColumn;
        int caretRow = row;
        int caretColumn = column;
        bool caretPlaced = false;
        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(_composition);
        while (elements.MoveNext())
        {
            int elementIndex = elements.ElementIndex;
            if (!caretPlaced && elementIndex >= _compositionCaret)
            {
                caretRow = row;
                caretColumn = column;
                caretPlaced = true;
            }

            string element = elements.GetTextElement();
            if (element is "\r" or "\n" or "\r\n")
            {
                row++;
                column = 0;
                if (row >= Rows) break;
                continue;
            }

            Rune rune = Rune.GetRuneAt(element, 0);
            int width = Math.Max(1, UnicodeWidth.Of(rune));
            if (width == 2 && column == Columns - 1 || column >= Columns)
            {
                row++;
                column = 0;
            }
            if (row >= Rows) break;

            lines[row][column] = new TerminalCell(
                element, (byte)width, _foreground, _background, _attributes, 0);
            if (width == 2 && column + 1 < Columns)
                lines[row][column + 1] = new TerminalCell(
                    string.Empty, 0, _foreground, _background, _attributes, 0);
            column += width;
            if (column >= Columns)
            {
                row++;
                column = 0;
            }
        }

        if (!caretPlaced)
        {
            caretRow = Math.Min(row, Rows - 1);
            caretColumn = Math.Clamp(column, 0, Columns - 1);
        }
        return new TerminalCursor(caretColumn, caretRow, visible);
    }

    private void AppendCombiningMark(string mark)
    {
        int row = _screen.CursorRow;
        int column = _screen.CursorColumn - 1;
        if (column < 0 && row > 0) { row--; column = Columns - 1; }
        if (column < 0) return;
        if (_screen.Lines[row][column].IsContinuation && column > 0) column--;
        TerminalCell current = _screen.Lines[row][column];
        if (current.Grapheme == " ") return;
        _screen.Lines[row][column] = current with { Grapheme = current.Grapheme + mark };
        MarkDirty(row);
        _revision++;
    }

    private void LineFeed()
    {
        _screen.WrapPending = false;
        if (_screen.CursorRow == _screen.ScrollBottom)
            ScrollUp(_screen.ScrollTop, _screen.ScrollBottom, 1);
        else _screen.CursorRow = Math.Min(Rows - 1, _screen.CursorRow + 1);
    }

    private void ReverseIndex()
    {
        _screen.WrapPending = false;
        if (_screen.CursorRow == _screen.ScrollTop)
            ScrollDown(_screen.ScrollTop, _screen.ScrollBottom, 1);
        else _screen.CursorRow = Math.Max(0, _screen.CursorRow - 1);
    }

    private void ScrollUp(int top, int bottom, int count)
    {
        count = Math.Clamp(count, 1, bottom - top + 1);
        for (int iteration = 0; iteration < count; iteration++)
        {
            if (!_alternateActive && ReferenceEquals(_screen, _primary) && top == 0 && bottom == Rows - 1)
                PushScrollback(_screen.Lines[top]);
            TerminalCell[] removed = _screen.Lines[top];
            for (int row = top; row < bottom; row++) _screen.Lines[row] = _screen.Lines[row + 1];
            _screen.Lines[bottom] = ClearLine(removed);
        }
        MarkDirtyRange(top, bottom);
        _revision++;
    }

    private void ScrollDown(int top, int bottom, int count)
    {
        count = Math.Clamp(count, 1, bottom - top + 1);
        for (int iteration = 0; iteration < count; iteration++)
        {
            TerminalCell[] removed = _screen.Lines[bottom];
            for (int row = bottom; row > top; row--) _screen.Lines[row] = _screen.Lines[row - 1];
            _screen.Lines[top] = ClearLine(removed);
        }
        MarkDirtyRange(top, bottom);
        _revision++;
    }

    private void PushScrollback(TerminalCell[] line)
    {
        if (_maxScrollback == 0) return;
        _scrollback.Add((TerminalCell[])line.Clone());
        if (_scrollbackOffset > 0) _scrollbackOffset++;
        TrimScrollback();
    }

    private void TrimScrollback()
    {
        int remove = Math.Max(0, _scrollback.Count - _maxScrollback);
        if (remove > 0)
        {
            _scrollback.RemoveRange(0, remove);
            _scrollbackOffset = Math.Max(0, _scrollbackOffset - remove);
        }
        _scrollbackOffset = Math.Clamp(_scrollbackOffset, 0, _scrollback.Count);
    }

    private void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseRange(_screen.CursorRow, _screen.CursorColumn, Columns - 1);
                for (int row = _screen.CursorRow + 1; row < Rows; row++) EraseRange(row, 0, Columns - 1);
                break;
            case 1:
                for (int row = 0; row < _screen.CursorRow; row++) EraseRange(row, 0, Columns - 1);
                EraseRange(_screen.CursorRow, 0, _screen.CursorColumn);
                break;
            case 2:
                for (int row = 0; row < Rows; row++) EraseRange(row, 0, Columns - 1);
                break;
            case 3:
                _scrollback.Clear();
                _scrollbackOffset = 0;
                break;
        }
    }

    private void EraseLine(int mode)
    {
        if (mode == 0) EraseRange(_screen.CursorRow, _screen.CursorColumn, Columns - 1);
        else if (mode == 1) EraseRange(_screen.CursorRow, 0, _screen.CursorColumn);
        else if (mode == 2) EraseRange(_screen.CursorRow, 0, Columns - 1);
    }

    private void EraseRange(int row, int start, int end)
    {
        start = Math.Clamp(start, 0, Columns - 1);
        end = Math.Clamp(end, 0, Columns - 1);
        for (int column = start; column <= end; column++) _screen.Lines[row][column] = BlankCell();
        MarkDirty(row);
        _revision++;
    }

    private void InsertCharacters(int count)
    {
        int column = _screen.CursorColumn;
        count = Math.Clamp(count, 1, Columns - column);
        var line = _screen.Lines[_screen.CursorRow];
        Array.Copy(line, column, line, column + count, Columns - column - count);
        for (int i = column; i < column + count; i++) line[i] = BlankCell();
        MarkDirty(_screen.CursorRow);
        _revision++;
    }

    private void DeleteCharacters(int count)
    {
        int column = _screen.CursorColumn;
        count = Math.Clamp(count, 1, Columns - column);
        var line = _screen.Lines[_screen.CursorRow];
        Array.Copy(line, column + count, line, column, Columns - column - count);
        for (int i = Columns - count; i < Columns; i++) line[i] = BlankCell();
        MarkDirty(_screen.CursorRow);
        _revision++;
    }

    private void EraseCharacters(int count) =>
        EraseRange(_screen.CursorRow, _screen.CursorColumn, _screen.CursorColumn + count - 1);

    private void InsertLines(int count)
    {
        if (_screen.CursorRow < _screen.ScrollTop || _screen.CursorRow > _screen.ScrollBottom) return;
        ScrollDown(_screen.CursorRow, _screen.ScrollBottom, count);
    }

    private void DeleteLines(int count)
    {
        if (_screen.CursorRow < _screen.ScrollTop || _screen.CursorRow > _screen.ScrollBottom) return;
        ScrollUp(_screen.CursorRow, _screen.ScrollBottom, count);
    }

    private void EnterAlternateScreen()
    {
        if (_alternateActive) return;
        _alternate = new Screen(Columns, Rows);
        _screen = _alternate;
        _alternateActive = true;
        _scrollbackOffset = 0;
        MarkAllDirty();
        _revision++;
    }

    private void LeaveAlternateScreen()
    {
        if (!_alternateActive) return;
        _screen = _primary;
        _alternateActive = false;
        MarkAllDirty();
        _revision++;
    }

    private void Reset()
    {
        int columns = Columns;
        int rows = Rows;
        _primary = new Screen(columns, rows);
        _alternate = new Screen(columns, rows);
        _screen = _primary;
        _alternateActive = false;
        _scrollback.Clear();
        _scrollbackOffset = 0;
        _cursorVisible = true;
        _autoWrap = true;
        _activeHyperlink = 0;
        ResetRendition();
        MarkAllDirty();
        _revision++;
    }

    private void SaveCursor()
    {
        _screen.SavedColumn = _screen.CursorColumn;
        _screen.SavedRow = _screen.CursorRow;
    }

    private void RestoreCursor() => SetCursor(_screen.SavedColumn, _screen.SavedRow);

    private void MoveCursor(int columns, int rows) =>
        SetCursor(_screen.CursorColumn + columns, _screen.CursorRow + rows);

    private void SetCursor(int column, int row)
    {
        _screen.CursorColumn = Math.Clamp(column, 0, Columns - 1);
        _screen.CursorRow = Math.Clamp(row, 0, Rows - 1);
        _screen.WrapPending = false;
    }

    private void ClearWideCell(int row, int column)
    {
        TerminalCell cell = _screen.Lines[row][column];
        if (cell.IsContinuation && column > 0) _screen.Lines[row][column - 1] = BlankCell();
        else if (cell.DisplayWidth == 2 && column + 1 < Columns) _screen.Lines[row][column + 1] = BlankCell();
        _screen.Lines[row][column] = BlankCell();
    }

    private TerminalCell BlankCell() => new(
        " ", 1, _foreground, _background, _attributes, 0);

    private TerminalCell[] ClearLine(TerminalCell[] line)
    {
        Array.Fill(line, BlankCell());
        return line;
    }

    private void ResetRendition()
    {
        _foreground = TerminalColor.Default;
        _background = TerminalColor.Default;
        _attributes = TerminalAttributes.None;
    }

    private void FinishOsc()
    {
        ProcessOsc(_sequence.ToString());
        _sequence.Clear();
        _parserState = ParserState.Ground;
    }

    private void CancelSequence()
    {
        _sequence.Clear();
        _parserState = ParserState.Ground;
    }

    private void Emit(TerminalEvent terminalEvent) => _events.Add(terminalEvent);

    private void MarkDirty(int row) => _dirtyRows.Add(row);

    private void MarkDirtyRange(int first, int last)
    {
        for (int row = first; row <= last; row++) _dirtyRows.Add(row);
    }

    private void MarkAllDirty() => MarkDirtyRange(0, _screen.Lines.Length - 1);

    private static int[] ParseParameters(string sequence)
    {
        if (sequence.Length == 0) return [];
        string[] parts = sequence.Split(';');
        var values = new int[parts.Length];
        for (int index = 0; index < parts.Length; index++)
        {
            string part = parts[index];
            int colon = part.IndexOf(':');
            if (colon >= 0) part = part[..colon];
            values[index] = int.TryParse(part, out int value) ? value : -1;
        }
        return values;
    }

    private static TerminalCell[][] CreateLines(int columns, int rows)
    {
        var lines = new TerminalCell[rows][];
        for (int row = 0; row < rows; row++) lines[row] = BlankLine(columns);
        return lines;
    }

    private static TerminalCell[] BlankLine(int columns)
    {
        var line = new TerminalCell[columns];
        Array.Fill(line, TerminalCell.Empty);
        return line;
    }

    private static TerminalCell[] ResizeLine(TerminalCell[] line, int columns)
    {
        TerminalCell[] resized = BlankLine(columns);
        Array.Copy(line, resized, Math.Min(line.Length, columns));
        return resized;
    }

    private static TerminalCell[][] CloneLines(TerminalCell[][] lines)
    {
        var clone = new TerminalCell[lines.Length][];
        for (int row = 0; row < lines.Length; row++) clone[row] = (TerminalCell[])lines[row].Clone();
        return clone;
    }
}
