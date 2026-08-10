using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Subsystem.Port;

/// <summary>
/// HLSL-compatible 64-byte layout structured data mapping directly to StructuredBuffer<UiElementData>.
/// Memory Alignment: Structured tightly into four 16-byte boundaries (Float4 vectors).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct UiElementData
{
    // float4 Rect (Offset 0): x, y, w, h in normalized coordinates (0.0 - 1.0)
    public float X;
    public float Y;
    public float W;
    public float H;

    // float4 Color (Offset 16): r, g, b, a base fill
    public float R;
    public float G;
    public float B;
    public float A;

    // float4 DepthRotTypeActive (Offset 32)
    public float ZDepth;      // Push-Z: 0.0f (Front) to 1.0f (Back)
    public float Rotation;    // Angle in radians
    public float ElementType; // 1.0 = Wallpaper, 2.0 = Card (world), 3.0 = Chrome (screen-fixed)
    public float IsActive;    // 1.0f = Active, 0.0f = Disabled

    // float4 Params (Offset 48)
    public float ColorId;
    public float TexBlend;    // Blending factor with visual text/glyph texture
    private readonly float _pad1;
    private readonly float _pad2;

    public UiElementData(float x, float y, float w, float h, float r, float g, float b, float a = 1.0f, float z = 0.0f, float rot = 0.0f, float type = 2.0f)
    {
        X = x; Y = y; W = w; H = h;
        R = r; G = g; B = b; A = a;
        ZDepth = z; Rotation = rot; ElementType = type; IsActive = 1.0f;
        ColorId = 0.0f; TexBlend = 0.0f;
        _pad1 = 0.0f; _pad2 = 0.0f;
    }
}

/// <summary>
/// Layout-compatible with constant buffer register b0 (16-byte packed alignment).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct CompositorConfig
{
    public float ResolutionX;
    public float ResolutionY;
    public float Time;
    public float DpiScale;

    public float ThemeColorR;
    public float ThemeColorG;
    public float ThemeColorB;
    public float ThemeColorA;

    public float CameraX;
    public float CameraY;
    private readonly float _pad1;
    private readonly float _pad2;

    public CompositorConfig(float resX, float resY, float time, float dpi, float tr, float tg, float tb, float ta, float camX, float camY)
    {
        ResolutionX = resX; ResolutionY = resY;
        Time = time; DpiScale = dpi;
        ThemeColorR = tr; ThemeColorG = tg; ThemeColorB = tb; ThemeColorA = ta;
        CameraX = camX; CameraY = camY;
        _pad1 = 0.0f; _pad2 = 0.0f;
    }
}

/// <summary>
/// GpuCompositor host manager and math fallback rasterizer engine.
/// Coordinates the current display lists, configurations, and renders high-fidelity fallbacks.
/// </summary>
public sealed class GpuCompositor
{
    public const int MaxElements = 256;

    public List<UiElementData> Scene { get; } = new(MaxElements);
    public float CameraX { get; set; } = 0.0f;
    public float CameraY { get; set; } = 0.0f;
    public float Time { get; set; } = 0.0f;
    public float DpiScale { get; set; } = 1.0f;
    public (float R, float G, float B, float A) ThemeColor { get; set; } = (0.0f, 0.52f, 1.0f, 1.0f);

    public void Clear() => Scene.Clear();

    public void AddElement(UiElementData element)
    {
        if (Scene.Count < MaxElements)
        {
            Scene.Add(element);
        }
    }

    /// <summary>
    /// Performs a high-performance, pixel-perfect software parallel rasterization of the GPU compositor.
    /// Provides 100% math and pixel parity with the HLSL shader execution pipeline when GPU contexts are lost.
    /// </summary>
    public byte[] RasterizeCpu(int width, int height)
    {
        var buffer = new byte[width * height * 4];
        float aspect = (float)width / height;
        var config = new CompositorConfig(width, height, Time, DpiScale, ThemeColor.R, ThemeColor.G, ThemeColor.B, ThemeColor.A, CameraX, CameraY);

        Parallel.For(0, height, y =>
        {
            float uvY = 1.0f - ((y + 0.5f) / height); // Y-Up space match
            int rowOffset = y * width * 4;

            for (int x = 0; x < width; x++)
            {
                float uvX = (x + 0.5f) / width;

                // 1. Render Procedural Background
                float worldX = uvX * aspect + config.CameraX * aspect;
                float worldY = uvY + config.CameraY;
                float rBg = MathF.Sin(worldX * 0.5f) * 0.5f + 0.5f;
                float gBg = MathF.Cos(worldY * 0.5f + 2.0f) * 0.5f + 0.5f;
                float bBg = MathF.Sin((worldX - worldY) * 0.5f) * 0.5f + 0.5f;

                float bgR = rBg * 0.28f + 0.08f;
                float bgG = gBg * 0.28f + 0.08f;
                float bgB = bBg * 0.28f + 0.08f;

                float finalR = bgR, finalG = bgG, finalB = bgB, finalA = 1.0f;

                // 2. Iterate and Alpha-Blend Spatial Layers
                for (int i = 0; i < Scene.Count; i++)
                {
                    UiElementData e = Scene[i];
                    if (e.IsActive < 0.5f) continue;
                    if (e.W <= 0.0f || e.H <= 0.0f) continue;

                    float zScale = 1.0f + (e.ZDepth * 2.0f);
                    float camX = (e.ElementType == 2.0f) ? config.CameraX : 0.0f;
                    float camY = (e.ElementType == 2.0f) ? config.CameraY : 0.0f;

                    // Projection mapping relative to center (0.5, 0.5)
                    float worldCenterX = e.X + (e.W * 0.5f);
                    float worldCenterY = e.Y + (e.H * 0.5f);
                    float screenCenterX = 0.5f + (worldCenterX - 0.5f) / zScale - (camX / zScale);
                    float screenCenterY = 0.5f + (worldCenterY - 0.5f) / zScale - (camY / zScale);

                    float pixelOffsetX = (uvX - screenCenterX) * zScale;
                    float pixelOffsetY = (uvY - screenCenterY) * zScale;

                    if (e.Rotation != 0.0f)
                    {
                        var rot = Rotate2d(pixelOffsetX, pixelOffsetY, -e.Rotation, aspect);
                        pixelOffsetX = rot.X;
                        pixelOffsetY = rot.Y;
                    }

                    float halfW = e.W * 0.5f;
                    float halfH = e.H * 0.5f;
                    float heightVal = Math.Clamp(1.0f - e.ZDepth, 0.0f, 1.0f);

                    // Compute dynamic volumetric drop-shadows
                    float dropY = 0.005f + (heightVal * 0.02f);
                    float shadowOffX = 0.0f;
                    float shadowOffY = dropY;
                    if (e.Rotation != 0.0f)
                    {
                        var sRot = Rotate2d(shadowOffX, shadowOffY, -e.Rotation, aspect);
                        shadowOffX = sRot.X;
                        shadowOffY = sRot.Y;
                    }

                    float shadowBlur = Math.Max(0.015f, heightVal * 0.05f);
                    float shadowDist = SdRoundedBox(pixelOffsetX - shadowOffX, pixelOffsetY - shadowOffY, halfW, halfH, 0.02f, aspect);
                    if (shadowDist < shadowBlur)
                    {
                        float intensity = 1.0f;
                        if (shadowDist > 0.0f)
                        {
                            intensity = 1.0f - SmoothStep(0.0f, shadowBlur, shadowDist);
                        }
                        float shadowMix = intensity * Mix(0.1f, 0.25f, heightVal);
                        finalR *= (1.0f - shadowMix);
                        finalG *= (1.0f - shadowMix);
                        finalB *= (1.0f - shadowMix);
                    }

                    // Render central card geometric body
                    float bodyDist = SdRoundedBox(pixelOffsetX, pixelOffsetY, halfW, halfH, 0.015f, aspect);
                    if (bodyDist < 0.0f)
                    {
                        float rCol = e.R, gCol = e.G, bCol = e.B, aCol = e.A;

                        // 3D Glass edge bevel factor
                        float innerY = pixelOffsetY / e.H + 0.5f;
                        float bR = Mix(rCol * 1.12f, rCol * 0.85f, innerY);
                        float bG = Mix(gCol * 1.12f, gCol * 0.85f, innerY);
                        float bB = Mix(bCol * 1.12f, bCol * 0.85f, innerY);

                        // Glossy highlighting border
                        float innerBorder = SdRoundedBox(pixelOffsetX, pixelOffsetY, halfW - 0.003f, halfH - 0.003f, 0.015f, aspect);
                        if (innerBorder > -0.003f)
                        {
                            bR = Mix(bR, 1.0f, 0.12f);
                            bG = Mix(bG, 1.0f, 0.12f);
                            bB = Mix(bB, 1.0f, 0.12f);
                        }

                        // Depth fog atmospheric attenuation
                        float fogRatio = Math.Clamp(-e.ZDepth * 0.6f, 0.0f, 1.0f);
                        bR = Mix(bR, bgR, fogRatio);
                        bG = Mix(bG, bgG, fogRatio);
                        bB = Mix(bB, bgB, fogRatio);

                        // Linear compositing blend pass
                        finalR = Mix(finalR, bR, aCol);
                        finalG = Mix(finalG, bG, aCol);
                        finalB = Mix(finalB, bB, aCol);
                    }
                }

                // Write BGRA output channels safely
                int pxIndex = rowOffset + (x * 4);
                buffer[pxIndex]     = (byte)Math.Clamp(finalB * 255.0f, 0.0f, 255.0f); // B
                buffer[pxIndex + 1] = (byte)Math.Clamp(finalG * 255.0f, 0.0f, 255.0f); // G
                buffer[pxIndex + 2] = (byte)Math.Clamp(finalR * 255.0f, 0.0f, 255.0f); // R
                buffer[pxIndex + 3] = (byte)Math.Clamp(finalA * 255.0f, 0.0f, 255.0f); // A
            }
        });

        return buffer;
    }

    private static float SdRoundedBox(float px, float py, float bx, float by, float r, float aspect)
    {
        float pAspectX = px * aspect;
        float pAspectY = py;
        float bAspectX = bx * aspect;
        float bAspectY = by;

        float dx = MathF.Abs(pAspectX) - bAspectX + r;
        float dy = MathF.Abs(pAspectY) - bAspectY + r;

        float maxInside = MathF.Min(MathF.Max(dx, dy), 0.0f);
        float lenOutside = MathF.Sqrt(MathF.Max(dx, 0.0f) * MathF.Max(dx, 0.0f) + MathF.Max(dy, 0.0f) * MathF.Max(dy, 0.0f));

        return maxInside + lenOutside - r;
    }

    private static (float X, float Y) Rotate2d(float x, float y, float angle, float aspect)
    {
        float px = x * aspect;
        float py = y;
        float s = MathF.Sin(angle);
        float c = MathF.Cos(angle);

        float rotX = px * c - py * s;
        float rotY = px * s + py * c;

        return (rotX / aspect, rotY);
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }

    private static float Mix(float start, float end, float amount) => start + (end - start) * amount;
}
