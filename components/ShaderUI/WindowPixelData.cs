using System;

namespace Subsystem;

public sealed class WindowPixelData
{
    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    public int RowBytes => Width * 4;

    public WindowPixelData(byte[] pixels, int width, int height)
    {
        Pixels = pixels;
        Width = width;
        Height = height;
    }
}
