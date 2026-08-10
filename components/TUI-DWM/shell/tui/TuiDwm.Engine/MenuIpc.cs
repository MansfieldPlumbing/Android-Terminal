using System.Runtime.InteropServices;

namespace TuiDwm.Engine;

[StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Unicode)]
public struct MenuElement
{
    public uint CommandId;
    public uint Flags;
    public int ParentIndex;
    public int HasChildren; // Win32 BOOL

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string Label;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct MenuPayload
{
    // Win32 POINT struct: LONG x, LONG y (both 4 bytes)
    public int CursorX;
    public int CursorY;

    public int ItemCount;
    
    // Explicit padding to ensure 8-byte alignment before the elements array
    public int _Padding;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
    public MenuElement[] Elements;
}
