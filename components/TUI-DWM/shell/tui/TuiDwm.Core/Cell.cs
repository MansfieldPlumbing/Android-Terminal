using System.Runtime.InteropServices;

namespace TuiDwm.Core;

[StructLayout(LayoutKind.Explicit, Size = 4)]
public struct Cell
{
    [FieldOffset(0)] public char Rune;   // 16-bit character (preserves full ANSI/Unicode)
    [FieldOffset(2)] public byte Fg;     // 8-bit foreground color
    [FieldOffset(3)] public byte Bg;     // 8-bit background color

    // Dummy property to satisfy existing code without taking up memory in the 32-bit struct
    public byte Style 
    { 
        get => 0; 
        set { /* bit-pack into Fg/Bg later if needed */ } 
    }

    public Cell(char rune, byte fg = 7, byte bg = 16, byte style = 0)
    {
        Rune = rune;
        Fg = fg;
        Bg = bg;
    }

    public static readonly Cell Empty = new Cell(' ', 7, 16);

    public override readonly bool Equals(object? obj) => obj is Cell cell && Equals(cell);
    public readonly bool Equals(Cell other) => Rune == other.Rune && Fg == other.Fg && Bg == other.Bg;
    public override readonly int GetHashCode() => HashCode.Combine(Rune, Fg, Bg);
    public static bool operator ==(Cell left, Cell right) => left.Equals(right);
    public static bool operator !=(Cell left, Cell right) => !left.Equals(right);
}
