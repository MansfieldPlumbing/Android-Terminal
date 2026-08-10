using System.Runtime.InteropServices;

namespace TuiDwm.Engine;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct UiElementData
{
    // pt1: [ X, Y, Width, Height ]
    public float X;
    public float Y;
    public float Width;
    public float Height;

    // pt2: [ R, G, B, A ]
    public float R;
    public float G;
    public float B;
    public float A;

    // pt3: [ Z, Rotation, ElementType, _Pad ]
    public float Z;
    public float Rotation;
    public float ElementType;
    public float Pad1;

    // pt4: [ ColorId, TexBlend, _Pad, _Pad ]
    public float ColorId;
    public float TexBlend;
    public float Pad2;
    public float Pad3;
}
