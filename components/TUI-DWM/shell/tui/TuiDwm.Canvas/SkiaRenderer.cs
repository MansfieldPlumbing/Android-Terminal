// SkiaRenderer.cs — Skia software compositor, blits to a raw Win32 HWND.
//
// Same drawing code runs on Android — no HLSL, no D3D12 surface, no Vortice.
// Software raster is fast enough at 1280×800 / 120fps on modern CPUs.
// Upgrade path: swap SKSurface.Create(info) for an OpenGL/Vulkan GRContext.
//
// Layers per frame (back to front):
//   1. Background  — animated star field + deep-space gradient
//   2. Windows     — SDF-equivalent RoundRect: shadow → glass body → titlebar → chrome
//   3. Content     — terminal cell grid drawn with SKFont into each window's content area
//
// P/Invoke surface: GetDC / ReleaseDC / SetDIBitsToDevice — nothing else.

using System;
using System.Runtime.InteropServices;
using SkiaSharp;
using TuiDwm.Core;

namespace TuiDwm.Canvas;

// ── Window descriptor ────────────────────────────────────────────────────────
public struct WindowEntry
{
    public float X, Y, W, H;     // pixel-space position and size
    public float Z;               // depth: 0 = front, 1 = submerged in background
    public bool  Active;
    public string Title;
    public TerminalSession? Content;
}

// ── Renderer ─────────────────────────────────────────────────────────────────
public sealed class SkiaRenderer : IDisposable
{
    private nint       _hwnd;
    private int        _width, _height;
    private SKBitmap   _bitmap  = null!;
    private SKCanvas   _canvas  = null!;

    // Cached paints — allocated once, reused every frame
    private readonly SKPaint _bgPaint      = new() { IsAntialias = false };
    private readonly SKPaint _shadowPaint  = new() { IsAntialias = true, Color = new SKColor(0, 0, 0, 100) };
    private readonly SKPaint _glassPaint   = new() { IsAntialias = true };
    private readonly SKPaint _titlePaint   = new() { IsAntialias = true };
    private readonly SKPaint _chromePaint  = new() { IsAntialias = true };
    private readonly SKPaint _cellBg       = new() { IsAntialias = false };
    private readonly SKPaint _cellFg       = new() { IsAntialias = true };
    private readonly SKPaint _starPaint    = new() { IsAntialias = true, StrokeCap = SKStrokeCap.Round };

    private SKFont     _font = null!;
    private float      _fontCellW, _fontCellH;

    // xterm 16-color palette
    private static readonly uint[] Palette16 =
    {
        0xFF0C0C0C, 0xFFC50F1F, 0xFF13A10E, 0xFFC19C00,
        0xFF0037DA, 0xFF881798, 0xFF3A96DD, 0xFFCCCCCC,
        0xFF767676, 0xFFE74856, 0xFF16C60C, 0xFFF9F1A5,
        0xFF3B78FF, 0xFFB4009E, 0xFF61D6D6, 0xFFF2F2F2,
    };

    // ── Init / Resize ─────────────────────────────────────────────────────────
    public void Initialize(nint hwnd, int width, int height)
    {
        _hwnd   = hwnd;
        _width  = width;
        _height = height;
        AllocateSurface();
        InitFont();
    }

    public void Resize(int w, int h)
    {
        _width  = w;
        _height = h;
        _bitmap?.Dispose();
        _canvas?.Dispose();
        AllocateSurface();
    }

    private void AllocateSurface()
    {
        var info = new SKImageInfo(_width, _height, SKColorType.Bgra8888, SKAlphaType.Premul);
        _bitmap  = new SKBitmap(info);
        _canvas  = new SKCanvas(_bitmap);
    }

    private void InitFont()
    {
        // Try Cascadia Code → Consolas → Courier New
        SKTypeface? tf =
            SKTypeface.FromFamilyName("Cascadia Code") ??
            SKTypeface.FromFamilyName("Cascadia Mono") ??
            SKTypeface.FromFamilyName("Consolas")      ??
            SKTypeface.FromFamilyName("Courier New")   ??
            SKTypeface.Default;

        _font       = new SKFont(tf, 14f) { Edging = SKFontEdging.SubpixelAntialias };
        _fontCellW  = 8f;
        _fontCellH  = 17f;
    }

    // ── Frame ─────────────────────────────────────────────────────────────────
    public void Render(ReadOnlySpan<WindowEntry> windows, float time)
    {
        _canvas.Clear();
        DrawBackground(time);

        // Back-to-front by Z (higher Z = deeper = drawn first)
        // Span doesn't support LINQ, so we do a simple selection sort pass inline.
        // Window count is tiny (< 16) so this is fine.
        Span<int> order = stackalloc int[windows.Length];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        for (int i = 0; i < order.Length - 1; i++)
            for (int j = i + 1; j < order.Length; j++)
                if (windows[order[i]].Z < windows[order[j]].Z)
                    (order[i], order[j]) = (order[j], order[i]);

        foreach (int idx in order)
        {
            var win = windows[idx];
            DrawWindow(win, time);
        }

        Blit();
    }

    // ── Background ────────────────────────────────────────────────────────────
    private void DrawBackground(float time)
    {
        // Deep-space gradient
        using var bg = SKShader.CreateLinearGradient(
            new SKPoint(0, 0), new SKPoint(0, _height),
            new[] { new SKColor(2, 4, 12), new SKColor(8, 14, 30), new SKColor(4, 8, 20) },
            null, SKShaderTileMode.Clamp);
        _bgPaint.Shader = bg;
        _canvas.DrawRect(0, 0, _width, _height, _bgPaint);
        _bgPaint.Shader = null;

        // Star field — deterministic per-star, animated by time
        float cx = _width  * 0.5f;
        float cy = _height * 0.5f;

        for (int s = 0; s < 220; s++)
        {
            // Stable hash → angle + seed speed
            float angle = (s * 2.399963f);               // golden-angle spread
            float speed = 0.04f + (s % 7) * 0.018f;
            float depth = ((s * 1.618f) % 1.0f);         // 0..1
            float t     = (time * speed + depth) % 1.0f; // 0 = center, 1 = edge

            float r  = t * (_width > _height ? _width : _height) * 0.75f;
            float dx = MathF.Cos(angle);
            float dy = MathF.Sin(angle);

            float x1 = cx + dx * r;
            float y1 = cy + dy * r;
            float x0 = cx + dx * (r - t * 8f);          // tail length grows with speed
            float y0 = cy + dy * (r - t * 8f);

            byte alpha = (byte)(t * 200f);
            float thick = t * 1.8f;
            _starPaint.Color       = new SKColor(180, 220, 255, alpha);
            _starPaint.StrokeWidth = Math.Max(0.5f, thick);
            _canvas.DrawLine(x0, y0, x1, y1, _starPaint);
        }
    }

    // ── Window ────────────────────────────────────────────────────────────────
    private const float CornerR     = 8f;
    private const float TitleH      = 26f;
    private const float ButtonR     = 6f;
    private const float ShadowBlur  = 18f;

    private void DrawWindow(WindowEntry win, float time)
    {
        var rect = new SKRect(win.X, win.Y, win.X + win.W, win.Y + win.H);

        // ── Shadow ────────────────────────────────────────────────────────────
        _shadowPaint.ImageFilter = SKImageFilter.CreateBlur(ShadowBlur, ShadowBlur);
        _shadowPaint.Color       = new SKColor(0, 0, 0, (byte)(90 - win.Z * 60));
        var shadowRect = rect;
        shadowRect.Inflate(6, 6);
        shadowRect.Offset(0, 4);
        _canvas.DrawRoundRect(shadowRect, CornerR + 4, CornerR + 4, _shadowPaint);
        _shadowPaint.ImageFilter?.Dispose();
        _shadowPaint.ImageFilter = null;

        // ── Glass body ────────────────────────────────────────────────────────
        // Z-depth: deeper windows are more transparent / more immersed in background
        byte bodyAlpha = (byte)(210 - win.Z * 140);

        using var bodyShader = SKShader.CreateLinearGradient(
            new SKPoint(win.X, win.Y), new SKPoint(win.X, win.Y + win.H),
            new[] { new SKColor(18, 26, 40, bodyAlpha), new SKColor(12, 18, 30, bodyAlpha) },
            null, SKShaderTileMode.Clamp);
        _glassPaint.Shader = bodyShader;
        _canvas.DrawRoundRect(rect, CornerR, CornerR, _glassPaint);
        _glassPaint.Shader = null;

        // Border
        _glassPaint.Style       = SKPaintStyle.Stroke;
        _glassPaint.StrokeWidth = win.Active ? 1.5f : 0.8f;
        _glassPaint.Color       = win.Active
            ? new SKColor(0, 140, 255, 180)
            : new SKColor(60, 80, 100, 80);
        _canvas.DrawRoundRect(rect, CornerR, CornerR, _glassPaint);
        _glassPaint.Style = SKPaintStyle.Fill;

        // ── Titlebar ──────────────────────────────────────────────────────────
        var titleRect = new SKRect(win.X, win.Y, win.X + win.W, win.Y + TitleH);
        using var titleShader = SKShader.CreateLinearGradient(
            new SKPoint(win.X, win.Y), new SKPoint(win.X, win.Y + TitleH),
            win.Active
                ? new[] { new SKColor(0, 90, 180, bodyAlpha),  new SKColor(0, 60, 130, bodyAlpha) }
                : new[] { new SKColor(35, 42, 55, bodyAlpha),  new SKColor(25, 32, 45, bodyAlpha) },
            null, SKShaderTileMode.Clamp);
        _titlePaint.Shader = titleShader;

        // Clip titlebar to top rounded corners only
        using (new SKAutoCanvasRestore(_canvas, true))
        {
            var titlePath = new SKPath();
            titlePath.AddRoundRect(rect, CornerR, CornerR);
            _canvas.ClipRect(titleRect);
            _canvas.ClipPath(titlePath);
            _canvas.DrawRect(titleRect, _titlePaint);
        }
        _titlePaint.Shader = null;

        // Title text
        _cellFg.Color = new SKColor(220, 230, 240, (byte)(200 - win.Z * 100));
        _canvas.DrawText(win.Title ?? "", win.X + 12f, win.Y + TitleH - 7f, _font, _cellFg);

        // Chrome buttons (close / max / min) — right side of titlebar
        float bx = win.X + win.W - 14f;
        float by = win.Y + TitleH * 0.5f;
        DrawChromeButton(_canvas, bx,        by, new SKColor(255, 95, 86));   // close
        DrawChromeButton(_canvas, bx - 20f,  by, new SKColor(255, 189, 46));  // max
        DrawChromeButton(_canvas, bx - 40f,  by, new SKColor(39, 201, 63));   // min

        // ── Terminal content ──────────────────────────────────────────────────
        if (win.Content != null)
            DrawTerminalContent(_canvas, win);
    }

    private void DrawChromeButton(SKCanvas canvas, float cx, float cy, SKColor color)
    {
        _chromePaint.Color = color;
        canvas.DrawCircle(cx, cy, ButtonR, _chromePaint);
        _chromePaint.Style       = SKPaintStyle.Stroke;
        _chromePaint.StrokeWidth = 0.5f;
        _chromePaint.Color       = new SKColor(0, 0, 0, 40);
        canvas.DrawCircle(cx, cy, ButtonR, _chromePaint);
        _chromePaint.Style = SKPaintStyle.Fill;
    }

    // ── Terminal content ──────────────────────────────────────────────────────
    private void DrawTerminalContent(SKCanvas canvas, WindowEntry win)
    {
        var session = win.Content!;
        
        session.RenderBlitIfNeeded();

        lock (session.BufferLock)
        {
            if (session.BlitSurface == null) return;

            float contentY = win.Y + TitleH + 2f;
            float contentH = win.H - TitleH - 2f;
            
            var sourceRect = new SKRect(0, 0, session.PixelWidth, session.PixelHeight);
            var destRect = new SKRect(win.X, contentY, win.X + win.W, contentY + contentH);

            // High-quality bitmap scaling is handled natively by SKCanvas if we add a paint,
            // but we can just use FilterQuality = High for downsampling/upsampling.
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.High };
            canvas.DrawBitmap(session.BlitSurface, sourceRect, destRect, paint);
        }
    }

    // ── Blit to HWND ──────────────────────────────────────────────────────────
    private void Blit()
    {
        nint hdc = GetDC(_hwnd);
        if (hdc == 0) return;
        try
        {
            var bmi = new BITMAPINFO
            {
                biSize        = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth       = _width,
                biHeight      = -_height,   // negative = top-down
                biPlanes      = 1,
                biBitCount    = 32,
                biCompression = 0,          // BI_RGB
            };
            SetDIBitsToDevice(hdc,
                0, 0, (uint)_width, (uint)_height,
                0, 0, 0, (uint)_height,
                _bitmap.GetPixels(), ref bmi, 0);
        }
        finally
        {
            ReleaseDC(_hwnd, hdc);
        }
    }

    // ── Dispose ───────────────────────────────────────────────────────────────
    public void Dispose()
    {
        _canvas?.Dispose();
        _bitmap?.Dispose();
        _font?.Dispose();
        _bgPaint?.Dispose();   _shadowPaint?.Dispose();
        _glassPaint?.Dispose(); _titlePaint?.Dispose();
        _chromePaint?.Dispose(); _cellBg?.Dispose();
        _cellFg?.Dispose();    _starPaint?.Dispose();
    }

    // ── GDI P/Invokes ─────────────────────────────────────────────────────────
    [DllImport("user32")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32")] private static extern int  ReleaseDC(nint hwnd, nint hdc);

    [DllImport("gdi32")]
    private static extern int SetDIBitsToDevice(
        nint hdc, int xDst, int yDst, uint w, uint h,
        int xSrc, int ySrc, uint startScan, uint scanLines,
        nint bits, ref BITMAPINFO bmi, uint colorUse);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int  biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int  biCompression, biSizeImage;
        public int  biXPelsPerMeter, biYPelsPerMeter;
        public int  biClrUsed, biClrImportant;
    }

    // BITMAPINFO with no color table (32-bit RGB)
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int   biSize;
        public int   biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int   biCompression, biSizeImage;
        public int   biXPelsPerMeter, biYPelsPerMeter;
        public int   biClrUsed, biClrImportant;
    }
}
