using Terminal.VT;

var tests = new (string Name, Action Body)[]
{
    ("C0 and cursor addressing", C0AndCursorAddressing),
    ("erase semantics", EraseSemantics),
    ("SGR attributes and colors", SgrAttributesAndColors),
    ("scroll regions", ScrollRegions),
    ("alternate screen restoration", AlternateScreenRestoration),
    ("wide and combining glyphs", WideAndCombiningGlyphs),
    ("stable scrollback viewport", StableScrollbackViewport),
    ("OSC title and hyperlinks", OscTitleAndHyperlinks),
    ("modes and terminal replies", ModesAndTerminalReplies),
    ("dirty rows and selection", DirtyRowsAndSelection),
    ("input composition projection", InputCompositionProjection),
    ("incremental parser boundaries", IncrementalParserBoundaries)
};

int failures = 0;
foreach ((string name, Action body) in tests)
{
    try
    {
        body();
        Console.WriteLine($"PASS  {name}");
    }
    catch (Exception error)
    {
        failures++;
        Console.Error.WriteLine($"FAIL  {name}: {error.Message}");
    }
}

Console.WriteLine($"Terminal.VT contract: {tests.Length - failures}/{tests.Length} passed");
return failures == 0 ? 0 : 1;

static void C0AndCursorAddressing()
{
    var engine = new TerminalVT(8, 3);
    engine.Feed("abc\rZ\x1b[2;3HX");
    TerminalSnapshot snapshot = engine.CaptureSnapshot();
    Equal("Zbc", Text(snapshot, 0, 3));
    Equal("  X", Text(snapshot, 1, 3));
    Equal(new TerminalCursor(3, 1, true), snapshot.Cursor);

    engine.Feed("\x1b[s\x1b[3;8H!\x1b[u?");
    snapshot = engine.CaptureSnapshot();
    Equal('!', snapshot.Lines[2][7].Grapheme[0]);
    Equal('?', snapshot.Lines[1][3].Grapheme[0]);
}

static void EraseSemantics()
{
    var engine = new TerminalVT(6, 2);
    engine.Feed("abcdef\x1b[1;3H\x1b[K");
    TerminalSnapshot snapshot = engine.CaptureSnapshot();
    Equal("ab    ", Text(snapshot, 0, 6));

    engine.Feed("1234\x1b[2J");
    snapshot = engine.CaptureSnapshot();
    Equal("      ", Text(snapshot, 0, 6));
    Equal("      ", Text(snapshot, 1, 6));
}

static void SgrAttributesAndColors()
{
    var engine = new TerminalVT(8, 2);
    engine.Feed("\x1b[1;3;4;9;38;2;12;34;56;48;5;196mX");
    TerminalCell cell = engine.CaptureSnapshot().Lines[0][0];
    True(cell.Attributes.HasFlag(TerminalAttributes.Bold));
    True(cell.Attributes.HasFlag(TerminalAttributes.Italic));
    True(cell.Attributes.HasFlag(TerminalAttributes.Underline));
    True(cell.Attributes.HasFlag(TerminalAttributes.Strike));
    Equal(TerminalColorKind.Rgb, cell.Foreground.Kind);
    Equal((byte)12, cell.Foreground.Red);
    Equal((byte)34, cell.Foreground.Green);
    Equal((byte)56, cell.Foreground.Blue);
    Equal(TerminalColorKind.Indexed, cell.Background.Kind);
    Equal((byte)196, cell.Background.Index);
}

static void ScrollRegions()
{
    var engine = new TerminalVT(4, 4);
    engine.Feed("A\r\nB\r\nC\r\nD");
    engine.Feed("\x1b[2;3r\x1b[3;1H\n");
    TerminalSnapshot snapshot = engine.CaptureSnapshot();
    Equal('A', snapshot.Lines[0][0].Grapheme[0]);
    Equal('C', snapshot.Lines[1][0].Grapheme[0]);
    Equal(' ', snapshot.Lines[2][0].Grapheme[0]);
    Equal('D', snapshot.Lines[3][0].Grapheme[0]);
}

static void AlternateScreenRestoration()
{
    var engine = new TerminalVT(8, 2);
    engine.Feed("primary");
    engine.Feed("\x1b[?1049hALT");
    TerminalSnapshot alternate = engine.CaptureSnapshot();
    True(alternate.IsAlternateScreen);
    Equal("ALT", Text(alternate, 0, 3));

    engine.Feed("\x1b[?1049l");
    TerminalSnapshot primary = engine.CaptureSnapshot();
    True(!primary.IsAlternateScreen);
    Equal("primary", Text(primary, 0, 7));
}

static void WideAndCombiningGlyphs()
{
    var engine = new TerminalVT(4, 2);
    engine.Feed("abc界");
    TerminalSnapshot snapshot = engine.CaptureSnapshot();
    Equal("abc", Text(snapshot, 0, 3));
    Equal("界", snapshot.Lines[1][0].Grapheme);
    Equal((byte)2, snapshot.Lines[1][0].DisplayWidth);
    Equal((byte)0, snapshot.Lines[1][1].DisplayWidth);

    engine = new TerminalVT(4, 1);
    engine.Feed("e\u0301");
    Equal("e\u0301", engine.CaptureSnapshot().Lines[0][0].Grapheme);
}

static void StableScrollbackViewport()
{
    var engine = new TerminalVT(4, 2, 10);
    engine.Feed("A\r\nB\r\nC");
    engine.ScrollViewport(1);
    TerminalSnapshot before = engine.CaptureSnapshot();
    Equal('A', before.Lines[0][0].Grapheme[0]);
    Equal('B', before.Lines[1][0].Grapheme[0]);

    engine.Feed("\r\nD");
    TerminalSnapshot after = engine.CaptureSnapshot();
    Equal('A', after.Lines[0][0].Grapheme[0]);
    Equal('B', after.Lines[1][0].Grapheme[0]);
    Equal(2, after.ScrollbackOffset);
}

static void OscTitleAndHyperlinks()
{
    var events = new List<TerminalEvent>();
    var engine = new TerminalVT(12, 2);
    engine.SemanticEvent += events.Add;
    engine.Feed("\x1b]2;Build console\a");
    engine.Feed("\x1b]8;;https://example.test\x1b\\link\x1b]8;;\x1b\\");
    TerminalSnapshot snapshot = engine.CaptureSnapshot();
    Equal("Build console", engine.Title);
    int id = snapshot.Lines[0][0].HyperlinkId;
    True(id > 0);
    Equal("https://example.test", engine.GetHyperlink(id));
    True(events.OfType<TerminalTitleChangedEvent>().Any(e => e.Title == "Build console"));
}

static void ModesAndTerminalReplies()
{
    string? reply = null;
    var engine = new TerminalVT(8, 2);
    engine.SemanticEvent += e => reply = (e as TerminalReplyRequestedEvent)?.Reply ?? reply;
    engine.Feed("\x1b[?25l\x1b[2;4H\x1b[6n");
    TerminalSnapshot snapshot = engine.CaptureSnapshot();
    True(!snapshot.Cursor.Visible);
    Equal("\x1b[2;4R", reply);
}

static void DirtyRowsAndSelection()
{
    var engine = new TerminalVT(8, 3);
    _ = engine.CaptureSnapshot();
    engine.Feed("\x1b[2;1HX");
    TerminalSnapshot snapshot = engine.CaptureSnapshot();
    Equal(1, snapshot.DirtyRows.Length);
    Equal(1, snapshot.DirtyRows[0]);

    var selection = new TerminalSelection(new TerminalPoint(0, 0), new TerminalPoint(1, 1));
    engine.SetSelection(selection.Anchor, selection.Active);
    Equal(selection, engine.CaptureSnapshot().Selection);
}

static void InputCompositionProjection()
{
    var engine = new TerminalVT(12, 2);
    engine.Feed("PS> ");
    engine.SetComposition("Get-Item", 3);
    TerminalSnapshot composed = engine.CaptureSnapshot();
    Equal("PS> Get-Item", Text(composed, 0, 12));
    Equal(new TerminalCursor(7, 0, true), composed.Cursor);

    engine.SetComposition(string.Empty, 0);
    TerminalSnapshot cleared = engine.CaptureSnapshot();
    Equal("PS>         ", Text(cleared, 0, 12));
    Equal(new TerminalCursor(4, 0, true), cleared.Cursor);
}

static void IncrementalParserBoundaries()
{
    var engine = new TerminalVT(12, 2);
    engine.Feed("\x1b[3");
    engine.Feed("1mR");
    engine.Feed("\ud83d");
    engine.Feed("\ude80");
    engine.Feed("\x1b]2;split");
    engine.Feed(" title\x1b");
    engine.Feed("\\");

    TerminalSnapshot snapshot = engine.CaptureSnapshot();
    Equal(TerminalColor.Indexed(1), snapshot.Lines[0][0].Foreground);
    Equal("🚀", snapshot.Lines[0][1].Grapheme);
    Equal((byte)2, snapshot.Lines[0][1].DisplayWidth);
    Equal("split title", engine.Title);
}

static string Text(TerminalSnapshot snapshot, int row, int length) =>
    string.Concat(snapshot.Lines[row].Take(length).Where(cell => !cell.IsContinuation).Select(cell => cell.Grapheme));

static void True(bool value)
{
    if (!value) throw new InvalidOperationException("Expected true.");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected <{expected}> but found <{actual}>.");
}
