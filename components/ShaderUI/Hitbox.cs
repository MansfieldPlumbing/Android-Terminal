using System;
using System.Collections.Generic;
using TuiDwm.Core;

namespace TuiDwm.Port;

/// <summary>
/// Specifies the geometric regions of a window or interactive widget.
/// </summary>
public enum HitRegion
{
    None,
    Body,
    Titlebar,
    CloseButton,
    MaximizeButton,
    MinimizeButton,
    TopLeftCorner,
    TopRightCorner,
    BottomLeftCorner,
    BottomRightCorner,
    LeftEdge,
    RightEdge,
    TopEdge,
    BottomEdge,
    Button,
    WindowDecoration
}

/// <summary>
/// Structure containing the details of a successful hit test.
/// </summary>
public struct HitResult
{
    public int WindowIndex;
    public string BasePath;
    public HitRegion Region;
}

/// <summary>
/// Base class for hit test evaluation.
/// </summary>
public abstract class BaseHitbox
{
    public abstract HitRegion TestHit(Vom vom, string basePath, float localPx, float localPy, float screenW, float screenH, bool isTouch);
}

/// <summary>
/// Evaluates hits inside window body/canvas client area.
/// </summary>
public sealed class WindowBodyHitbox : BaseHitbox
{
    public override HitRegion TestHit(Vom vom, string basePath, float localPx, float localPy, float screenW, float screenH, bool isTouch)
    {
        float colNorm = 1.0f / screenW;
        float rowNorm = 1.0f / screenH;
        float w = vom.Get<float>($"{basePath}\\Width", 0f) * colNorm;
        float h = vom.Get<float>($"{basePath}\\Height", 0f) * rowNorm;

        if (Math.Abs(localPx) <= w * 0.5f && Math.Abs(localPy) <= h * 0.5f)
        {
            return HitRegion.Body;
        }
        return HitRegion.None;
    }
}

/// <summary>
/// Evaluates hits within immediate-mode buttons.
/// </summary>
public sealed class ButtonHitbox : BaseHitbox
{
    public override HitRegion TestHit(Vom vom, string basePath, float localPx, float localPy, float screenW, float screenH, bool isTouch)
    {
        float colNorm = 1.0f / screenW;
        float rowNorm = 1.0f / screenH;
        float w = vom.Get<float>($"{basePath}\\Width", 0f) * colNorm;
        float h = vom.Get<float>($"{basePath}\\Height", 0f) * rowNorm;

        if (Math.Abs(localPx) <= w * 0.5f && Math.Abs(localPy) <= h * 0.5f)
        {
            return HitRegion.Button;
        }
        return HitRegion.None;
    }
}

/// <summary>
/// Evaluates hits inside window titlebar and control buttons (Min/Max/Close).
/// </summary>
public sealed class TitlebarHitbox : BaseHitbox
{
    public override HitRegion TestHit(Vom vom, string basePath, float localPx, float localPy, float screenW, float screenH, bool isTouch)
    {
        float colNorm = 1.0f / screenW;
        float rowNorm = 1.0f / screenH;
        float w = vom.Get<float>($"{basePath}\\Width", 0f) * colNorm;
        float h = vom.Get<float>($"{basePath}\\Height", 0f) * rowNorm;

        float scale = vom.Get<float>("\\Dwm\\DpiScale", 1.0f);
        float titlebarHeightPx = (isTouch ? 46.0f : 36.0f) * scale;
        
        // localPy goes from -h/2 (top) to +h/2 (bottom)
        float yFromTopPx = (localPy + h * 0.5f) * screenH;

        if (yFromTopPx >= 0 && yFromTopPx < titlebarHeightPx)
        {
            // Evaluate horizontally from right to left
            float xFromRightPx = (w * 0.5f - localPx) * screenW;
            float btnWidthPx = (isTouch ? 52.0f : 46.0f) * scale;

            if (xFromRightPx < btnWidthPx * 1.0f)
            {
                return HitRegion.CloseButton;
            }
            if (xFromRightPx < btnWidthPx * 2.0f)
            {
                return HitRegion.MaximizeButton;
            }
            if (xFromRightPx < btnWidthPx * 3.0f)
            {
                return HitRegion.MinimizeButton;
            }
            return HitRegion.Titlebar;
        }
        return HitRegion.None;
    }
}

/// <summary>
/// Evaluates hits inside outer resize handles (edges and corners).
/// </summary>
public sealed class WindowDecorationHitbox : BaseHitbox
{
    public override HitRegion TestHit(Vom vom, string basePath, float localPx, float localPy, float screenW, float screenH, bool isTouch)
    {
        float colNorm = 1.0f / screenW;
        float rowNorm = 1.0f / screenH;
        float w = vom.Get<float>($"{basePath}\\Width", 0f) * colNorm;
        float h = vom.Get<float>($"{basePath}\\Height", 0f) * rowNorm;

        float scale = vom.Get<float>("\\Dwm\\DpiScale", 1.0f);
        float marginPx = (isTouch ? 12.0f : 6.0f) * scale; // wider handles for touch

        float xFromLeftPx = (localPx + w * 0.5f) * screenW;
        float xFromRightPx = (w * 0.5f - localPx) * screenW;
        float yFromTopPx = (localPy + h * 0.5f) * screenH;
        float yFromBottomPx = (h * 0.5f - localPy) * screenH;

        bool isLeft = xFromLeftPx >= 0 && xFromLeftPx < marginPx;
        bool isRight = xFromRightPx >= 0 && xFromRightPx < marginPx;
        bool isTop = yFromTopPx >= 0 && yFromTopPx < marginPx;
        bool isBottom = yFromBottomPx >= 0 && yFromBottomPx < marginPx;

        // Simplify resize behavior for objects pushed far back
        float zDepth = vom.Get<float>($"{basePath}\\Z", 0f);
        if (zDepth > 0.25f)
        {
            return HitRegion.None;
        }

        if (isRight && isBottom) return HitRegion.BottomRightCorner;
        if (isLeft && isTop) return HitRegion.TopLeftCorner;
        if (isRight && isTop) return HitRegion.TopRightCorner;
        if (isLeft && isBottom) return HitRegion.BottomLeftCorner;

        if (isLeft) return HitRegion.LeftEdge;
        if (isRight) return HitRegion.RightEdge;
        if (isTop) return HitRegion.TopEdge;
        if (isBottom) return HitRegion.BottomEdge;

        return HitRegion.None;
    }
}

/// <summary>
/// Aggregates multiple sub-hitboxes to resolve composite element regions.
/// </summary>
public sealed class CompositeHitbox : BaseHitbox
{
    private readonly List<BaseHitbox> _children = new();

    public CompositeHitbox(IEnumerable<BaseHitbox> children)
    {
        _children.AddRange(children);
    }

    public override HitRegion TestHit(Vom vom, string basePath, float localPx, float localPy, float screenW, float screenH, bool isTouch)
    {
        for (int i = 0; i < _children.Count; i++)
        {
            var hit = _children[i].TestHit(vom, basePath, localPx, localPy, screenW, screenH, isTouch);
            if (hit != HitRegion.None)
            {
                return hit;
            }
        }
        return HitRegion.None;
    }
}

/// <summary>
/// Master coordinator that resolves exact HitRegions for different widget classes.
/// </summary>
public sealed class QuadHitboxResolver
{
    private readonly CompositeHitbox _windowHitbox = new(new BaseHitbox[]
    {
        new WindowDecorationHitbox(),
        new TitlebarHitbox(),
        new WindowBodyHitbox()
    });

    private readonly CompositeHitbox _buttonHitbox = new(new BaseHitbox[]
    {
        new ButtonHitbox()
    });

    public HitRegion ResolveHitRegion(Vom vom, string basePath, float elementType, float localPx, float localPy, float screenW, float screenH, bool isTouch)
    {
        float colNorm = 1.0f / screenW;
        float rowNorm = 1.0f / screenH;
        float w = vom.Get<float>($"{basePath}\\Width", 0f) * colNorm;
        float h = vom.Get<float>($"{basePath}\\Height", 0f) * rowNorm;

        // Fail-safe out-of-bounds check
        if (Math.Abs(localPx) > w * 0.5f || Math.Abs(localPy) > h * 0.5f)
        {
            return HitRegion.None;
        }

        if (elementType == 2.0f) // Standard Window
        {
            return _windowHitbox.TestHit(vom, basePath, localPx, localPy, screenW, screenH, isTouch);
        }
        
        if (elementType == 1.0f) // Buttons / Icons
        {
            return _buttonHitbox.TestHit(vom, basePath, localPx, localPy, screenW, screenH, isTouch);
        }

        return HitRegion.Body;
    }
}
