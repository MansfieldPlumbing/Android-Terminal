using System.Runtime.InteropServices;

namespace TuiDwm.Port;

/// <summary>
/// C# Mirror of the 16-float (64-byte) GPU-aligned structural layout used
/// by DirectPort's StructuredBuffer state fabric.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct UiElementData
{
    // --- pt1: Spatial bounds (Normalized screen coordinates) ---
    public float X;
    public float Y;
    public float Width;
    public float Height;

    // --- pt2: Solid color (RGBA) ---
    public float R;
    public float G;
    public float B;
    public float A;

    // --- pt3: Cinematic depth and type identifiers ---
    public float Z;
    public float Rotation;
    public float ElementType; // 1.0=Button, 2.0=Window, 3.0=Taskbar, 4.0/5.0=SnapZone, 6.0=Wallpaper
    public float IsActive;    // 1.0=Active, 0.0=Inactive (skip render)

    // --- pt4: Custom texture/widget mapping variables ---
    public float ColorId;     // Custom color or widget variant index
    public float TexBlend;    // Texture sampling blend ratio (0.0 to 1.0)
    public float Pad1;
    public float Pad2;

    // Helper bridge properties for Compositor mappings
    public float2 Position
    {
        get => new float2(X, Y);
        set { X = value.x; Y = value.y; }
    }

    public float2 Size
    {
        get => new float2(Width, Height);
        set { Width = value.x; Height = value.y; }
    }

    public float4 Color
    {
        get => new float4(R, G, B, A);
        set { R = value.r; G = value.g; B = value.b; A = value.a; }
    }

    public float ZDepth
    {
        get => Z;
        set => Z = value;
    }
}
