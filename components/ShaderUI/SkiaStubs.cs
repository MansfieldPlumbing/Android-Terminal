using System;
using System.IO;

namespace SkiaSharp
{
    public enum SKColorType
    {
        Bgra8888,
        Rgba8888
    }

    public enum SKAlphaType
    {
        Premul,
        Opaque
    }

    public struct SKImageInfo
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public SKColorType ColorType { get; set; }
        public SKAlphaType AlphaType { get; set; }

        public SKImageInfo(int width, int height, SKColorType colorType = SKColorType.Bgra8888, SKAlphaType alphaType = SKAlphaType.Premul)
        {
            Width = width;
            Height = height;
            ColorType = colorType;
            AlphaType = alphaType;
        }
    }

    public class SKBitmap : IDisposable
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int RowBytes => Width * 4;
        public bool IsEmpty => Width <= 0 || Height <= 0;

        public SKBitmap() { }

        public SKBitmap(int width, int height, bool isOpaque = false)
        {
            Width = width;
            Height = height;
        }

        public SKBitmap(SKImageInfo info)
        {
            Width = info.Width;
            Height = info.Height;
        }

        public static SKBitmap Decode(string path) => new SKBitmap(1024, 1024);
        public static SKBitmap Decode(Stream stream) => new SKBitmap(1024, 1024);
        public static SKBitmap Decode(SKCodec codec) => new SKBitmap(1024, 1024);

        public IntPtr GetPixels() => IntPtr.Zero;

        public void Dispose() { }
    }

    public class SKCanvas : IDisposable
    {
        public SKCanvas(SKBitmap bitmap) { }

        public void DrawRect(float x, float y, float w, float h, SKPaint paint) { }
        public void DrawText(string text, float x, float y, SKFont font, SKPaint paint) { }
        public void Clear() { }
        public void Clear(SKColor color) { }

        public void Dispose() { }
    }

    public class SKTypeface : IDisposable
    {
        public static readonly SKTypeface Default = new SKTypeface();

        public static SKTypeface FromFamilyName(string name) => new SKTypeface();
        public static SKTypeface FromFile(string path) => new SKTypeface();

        public void Dispose() { }
    }

    public enum SKFontEdging
    {
        SubpixelAntialias,
        Alias,
        Antialias
    }

    public class SKFont : IDisposable
    {
        public float Size { get; set; }
        public SKFontEdging Edging { get; set; }

        public SKFont() { }

        public SKFont(SKTypeface typeface, float size = 12f)
        {
            Size = size;
        }

        public void Dispose() { }
    }

    public enum SKBlendMode
    {
        Src,
        SrcOver,
        Clear
    }

    public struct SKColor
    {
        public uint Value { get; set; }

        public SKColor(uint value)
        {
            Value = value;
        }

        public SKColor(byte r, byte g, byte b, byte a = 255)
        {
            Value = (uint)((a << 24) | (r << 16) | (g << 8) | b);
        }
    }

    public static class SKColors
    {
        public static readonly SKColor Transparent = new SKColor(0, 0, 0, 0);
        public static readonly SKColor Black = new SKColor(0, 0, 0, 255);
        public static readonly SKColor White = new SKColor(255, 255, 255, 255);
    }

    public class SKPaint : IDisposable
    {
        public bool IsAntialias { get; set; }
        public SKColor Color { get; set; }
        public SKBlendMode BlendMode { get; set; }
        public float StrokeWidth { get; set; }

        public void Dispose() { }
    }

    public class SKCodec : IDisposable
    {
        public static SKCodec Create(string path) => new SKCodec();
        public static SKCodec Create(Stream stream) => new SKCodec();

        public void Dispose() { }
    }
}
