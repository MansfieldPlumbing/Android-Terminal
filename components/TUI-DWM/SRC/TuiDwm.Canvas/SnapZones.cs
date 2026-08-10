using System;

namespace TuiDwm.Canvas;

// Snap zone detection for touch/mouse drag.
//
// Zones are detected by proximity to screen edges/corners during drag.
// On release in a snap zone, the session window snaps to that layout.
//
// Layout zones:
//   Full        — top center / anywhere top 32px strip
//   LeftHalf    — left edge proximity
//   RightHalf   — right edge proximity
//   TopLeft     — top-left corner
//   TopRight    — top-right corner
//   BottomLeft  — bottom-left corner
//   BottomRight — bottom-right corner
//   None        — no snap

public enum SnapZone
{
    None,
    Full,
    LeftHalf, RightHalf,
    TopLeft, TopRight,
    BottomLeft, BottomRight,
}

public static class SnapZones
{
    private const int EdgeMargin   = 48;  // px from edge to activate snap
    private const int CornerMargin = 96;  // px from corner to activate corner snap

    public static SnapZone Detect(int x, int y, int screenW, int screenH)
    {
        bool nearLeft   = x < EdgeMargin;
        bool nearRight  = x > screenW - EdgeMargin;
        bool nearTop    = y < EdgeMargin;
        bool nearBottom = y > screenH - EdgeMargin;

        if (nearTop && nearLeft)  return SnapZone.TopLeft;
        if (nearTop && nearRight) return SnapZone.TopRight;
        if (nearBottom && nearLeft)  return SnapZone.BottomLeft;
        if (nearBottom && nearRight) return SnapZone.BottomRight;
        if (nearTop)    return SnapZone.Full;
        if (nearLeft)   return SnapZone.LeftHalf;
        if (nearRight)  return SnapZone.RightHalf;

        return SnapZone.None;
    }

    // Returns (x, y, w, h) in pixels for a given zone and screen size.
    public static (int X, int Y, int W, int H) GetRect(SnapZone zone, int screenW, int screenH)
    {
        int half = screenW / 2;
        int hHalf = screenH / 2;

        return zone switch
        {
            SnapZone.Full        => (0,    0,    screenW, screenH),
            SnapZone.LeftHalf    => (0,    0,    half,    screenH),
            SnapZone.RightHalf   => (half, 0,    half,    screenH),
            SnapZone.TopLeft     => (0,    0,    half,    hHalf),
            SnapZone.TopRight    => (half, 0,    half,    hHalf),
            SnapZone.BottomLeft  => (0,    hHalf, half,   hHalf),
            SnapZone.BottomRight => (half, hHalf, half,   hHalf),
            _                    => (0, 0, screenW, screenH),
        };
    }
}
