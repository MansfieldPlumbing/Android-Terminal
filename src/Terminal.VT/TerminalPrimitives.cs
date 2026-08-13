namespace Terminal.Engine;

[Flags]
public enum TerminalAttributes : byte
{
    None = 0,
    Bold = 1 << 0,
    Dim = 1 << 1,
    Italic = 1 << 2,
    Underline = 1 << 3,
    Inverse = 1 << 4,
    Strike = 1 << 5
}

public enum TerminalColorKind : byte
{
    Default,
    Indexed,
    Rgb
}

public readonly record struct TerminalColor(TerminalColorKind Kind, uint Value)
{
    public static TerminalColor Default => new(TerminalColorKind.Default, 0);
    public static TerminalColor Indexed(byte index) => new(TerminalColorKind.Indexed, index);
    public static TerminalColor Rgb(byte red, byte green, byte blue) =>
        new(TerminalColorKind.Rgb, (uint)(red << 16 | green << 8 | blue));

    public byte Index => (byte)Value;
    public byte Red => (byte)(Value >> 16);
    public byte Green => (byte)(Value >> 8);
    public byte Blue => (byte)Value;
}

public readonly record struct TerminalCell(
    string Grapheme,
    byte DisplayWidth,
    TerminalColor Foreground,
    TerminalColor Background,
    TerminalAttributes Attributes,
    int HyperlinkId)
{
    public static TerminalCell Empty => new(
        " ", 1, TerminalColor.Default, TerminalColor.Default, TerminalAttributes.None, 0);

    public bool IsContinuation => DisplayWidth == 0;
}

public readonly record struct TerminalCursor(int Column, int Row, bool Visible);
public readonly record struct TerminalPoint(int Column, int Row);
public readonly record struct TerminalSelection(TerminalPoint Anchor, TerminalPoint Active);

public sealed record TerminalSnapshot(
    int Columns,
    int Rows,
    TerminalCell[][] Lines,
    TerminalCursor Cursor,
    TerminalSelection? Selection,
    int ScrollbackOffset,
    bool IsAlternateScreen,
    long Revision,
    int[] DirtyRows);

public abstract record TerminalEvent;
public sealed record TerminalBellEvent : TerminalEvent;
public sealed record TerminalTitleChangedEvent(string Title) : TerminalEvent;
public sealed record TerminalReplyRequestedEvent(string Reply) : TerminalEvent;
public sealed record TerminalHyperlinkEvent(int Id, string? Uri) : TerminalEvent;
