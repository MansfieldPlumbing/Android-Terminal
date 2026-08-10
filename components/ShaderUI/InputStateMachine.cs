using System;
using System.Collections.Generic;
using TuiDwm.Core;

namespace TuiDwm.Port;

public enum IntentMode
{
    None = 0,
    Translate = 1,
    ZPush = 2,
    Rotate = 3,
    Resize = 4,
    Fling = 5,
    CycleColor = 6,
    PanCanvas = 7
}

public struct ContactMatrix
{
    public float Cx;           // Centroid X
    public float Cy;           // Centroid Y
    public float DeltaZ;       // Pinch distance
    public float DeltaRot;     // Twist angle
    public int ActivePoints;   // Physical contacts
}

/// <summary>
/// Orchestrates input gestures and manages translation, rotation, resizing,
/// and zoom mechanics on windows and widgets.
/// </summary>
public sealed class InputStateMachine
{
    private readonly Vom _vom;
    private readonly List<PluginInstance> _plugins;
    private readonly SnapManager _snapManager;
    private readonly WindowPhysics _physics;

    public IntentMode ActiveIntentMode { get; private set; } = IntentMode.None;
    public PluginInstance? DraggedElement { get; private set; } = null;
    public string DraggedElementPath { get; private set; } = "";

    public float InitialPinchDistance { get; set; } = 0f;
    public float InitialTwistAngle { get; set; } = 0f;
    public int LastActivePoints { get; set; } = 0;

    public float LastX { get; set; } = 0f;
    public float LastY { get; set; } = 0f;
    public float StartX { get; set; } = 0f;
    public float StartY { get; set; } = 0f;

    public float StartW { get; set; } = 0f;
    public float StartH { get; set; } = 0f;
    public float StartBoundX { get; set; } = 0f;
    public float StartBoundY { get; set; } = 0f;

    public float DragOffsetX { get; set; } = 0f;
    public float DragOffsetY { get; set; } = 0f;

    public bool IsLeftEdgeResize { get; set; } = false;
    public bool IsRightEdgeResize { get; set; } = false;
    public bool IsTopEdgeResize { get; set; } = false;
    public bool IsBottomEdgeResize { get; set; } = false;

    public bool WindowManagementEnabled { get; set; } = true;
    public bool IsTouch { get; set; } = false;

    public float CameraX { get; set; } = 0f;
    public float CameraY { get; set; } = 0f;

    public InputStateMachine(Vom vom, List<PluginInstance> plugins, SnapManager snapManager, WindowPhysics physics)
    {
        _vom = vom;
        _plugins = plugins;
        _snapManager = snapManager;
        _physics = physics;
    }

    /// <summary>
    /// Evaluates mouse or touch contacts to acquire structural target dragging/zooming/resizing intents.
    /// </summary>
    public void AcquireIntent(ContactMatrix matrix, IntentMode mode, float screenW, float screenH)
    {
        if (matrix.ActivePoints == 0) return;

        HitResult? hit = _physics.ResolveTargetElementAndRegion(matrix.Cx, matrix.Cy, screenW, screenH, IsTouch, CameraX, CameraY);
        PluginInstance? hitEl = null;
        if (hit.HasValue && hit.Value.WindowIndex >= 0)
        {
            for (int i = 0; i < _plugins.Count; i++)
            {
                if (_plugins[i].WindowIndex == hit.Value.WindowIndex)
                {
                    hitEl = _plugins[i];
                    break;
                }
            }
        }

        string basePath = hit.HasValue ? hit.Value.BasePath : "";
        HitRegion hitRegion = hit.HasValue ? hit.Value.Region : HitRegion.None;

        if (hitEl != null && mode == IntentMode.Translate && matrix.ActivePoints == 1)
        {
            bool isResizable = _vom.Get<bool>($"{basePath}\\IsResizable", true);
            if (isResizable)
            {
                // Set active resize boundaries
                IsLeftEdgeResize = hitRegion == HitRegion.LeftEdge || hitRegion == HitRegion.TopLeftCorner || hitRegion == HitRegion.BottomLeftCorner;
                IsRightEdgeResize = hitRegion == HitRegion.RightEdge || hitRegion == HitRegion.BottomRightCorner;
                IsTopEdgeResize = hitRegion == HitRegion.TopLeftCorner;
                IsBottomEdgeResize = hitRegion == HitRegion.BottomEdge || hitRegion == HitRegion.BottomLeftCorner || hitRegion == HitRegion.BottomRightCorner;

                if (IsLeftEdgeResize || IsRightEdgeResize || IsTopEdgeResize || IsBottomEdgeResize)
                {
                    mode = IntentMode.Resize;
                }
            }
        }

        // If trying to translate but window is locked/undraggable, pan instead
        if (hitEl != null && mode == IntentMode.Translate && !_vom.Get<bool>($"{basePath}\\IsDraggable", true))
        {
            mode = IntentMode.PanCanvas;
        }

        // Window body clicks default to panning
        if (hitEl != null && mode == IntentMode.Translate && hitEl.Plugin.GetTemplate().PluginName == "Terminal" && hitRegion != HitRegion.Titlebar)
        {
            mode = IntentMode.PanCanvas;
        }

        if (hitEl == null && mode == IntentMode.Translate)
        {
            mode = IntentMode.PanCanvas;
        }

        if (mode == IntentMode.PanCanvas)
        {
            ActiveIntentMode = mode;
            DraggedElement = null;
            DraggedElementPath = "";
            LastX = matrix.Cx;
            LastY = matrix.Cy;
            return;
        }

        if (hitEl != null && mode != IntentMode.None)
        {
            DraggedElement = hitEl;
            DraggedElementPath = basePath;
            ActiveIntentMode = mode;

            int currentZIndex = _vom.Get<int>($"{basePath}\\ZIndex", hitEl.WindowIndex);
            for (int i = 0; i < _plugins.Count; i++)
            {
                var p = _plugins[i];
                int pZ = _vom.Get<int>($"{p.BasePath}\\ZIndex", p.WindowIndex);
                if (p != hitEl && pZ > currentZIndex)
                {
                    _vom.Set($"{p.BasePath}\\ZIndex", pZ - 1);
                }
            }
            _vom.Set($"{basePath}\\ZIndex", 99990); // Bring to front

            RecalibrateIntentOffsets(matrix, screenW, screenH);
        }
    }

    public void RecalibrateIntentOffsets(ContactMatrix matrix, float screenW, float screenH)
    {
        if (DraggedElement == null) return;

        float zDepth = _vom.Get<float>($"{DraggedElementPath}\\Z", 0f);
        float zScale = 1.0f + (zDepth * 2.0f);

        DragOffsetX = _vom.Get<float>($"{DraggedElementPath}\\X", 0f) - (0.5f + (matrix.Cx - 0.5f) * zScale);
        DragOffsetY = _vom.Get<float>($"{DraggedElementPath}\\Y", 0f) - (0.5f + (matrix.Cy - 0.5f) * zScale);

        InitialPinchDistance = matrix.DeltaZ;
        InitialTwistAngle = matrix.DeltaRot;

        LastX = matrix.Cx;
        LastY = matrix.Cy;
        StartX = matrix.Cx;
        StartY = matrix.Cy;

        StartW = _vom.Get<float>($"{DraggedElementPath}\\Width", 300f);
        StartH = _vom.Get<float>($"{DraggedElementPath}\\Height", 200f);
        StartBoundX = _vom.Get<float>($"{DraggedElementPath}\\X", 0f);
        StartBoundY = _vom.Get<float>($"{DraggedElementPath}\\Y", 0f);
    }

    /// <summary>
    /// Processes active movement to translate/rotate/resize widgets.
    /// </summary>
    public void MapIntent(ContactMatrix matrix, float screenW, float screenH)
    {
        if (ActiveIntentMode == IntentMode.None) return;

        float dx = matrix.Cx - LastX;
        float dy = matrix.Cy - LastY;

        if (ActiveIntentMode == IntentMode.PanCanvas)
        {
            CameraX -= dx;
            CameraY -= dy;
        }
        else if (DraggedElement != null)
        {
            float zDepth = _vom.Get<float>($"{DraggedElementPath}\\Z", 0f);
            float zScale = 1.0f + (zDepth * 2.0f);
            float aspect = screenW / screenH;

            if (ActiveIntentMode == IntentMode.Resize)
            {
                // Dynamic edge resizing aligned under local matrix transforms
                float totalDx = matrix.Cx - StartX;
                float totalDy = matrix.Cy - StartY;

                float rotX = totalDx * aspect;
                float rotY = totalDy;

                float localDx = (rotX / aspect) * zScale;
                float localDy = rotY * zScale;

                float cx = StartBoundX + StartW * 0.5f;
                float cy = StartBoundY + StartH * 0.5f;
                float nw = StartW;
                float nh = StartH;

                if (IsRightEdgeResize) { nw += localDx; cx += localDx * 0.5f; }
                if (IsLeftEdgeResize)  { nw -= localDx; cx += localDx * 0.5f; }
                if (IsBottomEdgeResize){ nh += localDy; cy += localDy * 0.5f; }
                if (IsTopEdgeResize)   { nh -= localDy; cy += localDy * 0.5f; }

                nw = Math.Max(0.1f, nw);
                nh = Math.Max(0.1f, nh);

                _vom.Set($"{DraggedElementPath}\\Width", nw);
                _vom.Set($"{DraggedElementPath}\\Height", nh);
                _vom.Set($"{DraggedElementPath}\\X", cx - nw * 0.5f);
                _vom.Set($"{DraggedElementPath}\\Y", cy - nh * 0.5f);
                _vom.Set($"{DraggedElementPath}\\TargetW", nw);
                _vom.Set($"{DraggedElementPath}\\TargetH", nh);
            }
            else if (ActiveIntentMode == IntentMode.Translate)
            {
                _vom.Delete($"{DraggedElementPath}\\TargetX");
                _vom.Delete($"{DraggedElementPath}\\TargetY");

                float newX = 0.5f + (matrix.Cx - 0.5f) * zScale + DragOffsetX;
                float newY = 0.5f + (matrix.Cy - 0.5f) * zScale + DragOffsetY;

                _vom.Set($"{DraggedElementPath}\\X", newX);
                _vom.Set($"{DraggedElementPath}\\Y", newY);

                if (WindowManagementEnabled)
                {
                    bool inZone = false;
                    string edge = "";
                    if (matrix.Cy < 0.05f) { inZone = true; edge = "top"; }
                    else if (matrix.Cy > 0.95f) { inZone = true; edge = "bottom"; }
                    else if (matrix.Cx < 0.15f) { inZone = true; edge = "left"; }
                    else if (matrix.Cx > 0.85f) { inZone = true; edge = "right"; }

                    if (inZone)
                    {
                        if (!_snapManager.WasInZone)
                        {
                            _snapManager.AllocateSnapGroup(edge, screenW, screenH);
                        }
                        _snapManager.TrackSnapProgress(matrix.Cx, matrix.Cy);
                    }
                    else
                    {
                        if (_snapManager.WasInZone)
                        {
                            _snapManager.FreeSnapGroup();
                        }
                    }
                }
            }
            else if (ActiveIntentMode == IntentMode.ZPush)
            {
                // Multi-touch Pinch to Zoom / Z-Pushing depth adjustment
                float dyPx = matrix.DeltaZ - InitialPinchDistance;
                float scaleRate = 0.005f;
                float newZ = zDepth - (dyPx * scaleRate);
                
                _vom.Set($"{DraggedElementPath}\\Z", Math.Clamp(newZ, 0.0f, 1.0f));
                InitialPinchDistance = matrix.DeltaZ;
                _vom.Set($"{DraggedElementPath}\\BaseZSlot", 0);
                RecalibrateIntentOffsets(matrix, screenW, screenH);
            }
            else if (ActiveIntentMode == IntentMode.Rotate)
            {
                // Twist/rotation math
                float angleDelta = matrix.DeltaRot - InitialTwistAngle;
                float rot = _vom.Get<float>($"{DraggedElementPath}\\Rotation", 0f);
                _vom.Set($"{DraggedElementPath}\\Rotation", rot + angleDelta);
                InitialTwistAngle = matrix.DeltaRot;
            }
        }

        LastX = matrix.Cx;
        LastY = matrix.Cy;
    }

    /// <summary>
    /// Commits target bounds when drag/resizing completes and frees temporary resources.
    /// </summary>
    public void ReleaseIntent()
    {
        if (WindowManagementEnabled && DraggedElement != null && ActiveIntentMode == IntentMode.Translate)
        {
            if (_snapManager.ActiveSnapSlot.HasValue)
            {
                var slot = _snapManager.ActiveSnapSlot.Value;
                _vom.Set($"{DraggedElementPath}\\TargetX", slot.Gx);
                _vom.Set($"{DraggedElementPath}\\TargetY", slot.Gy);
                _vom.Set($"{DraggedElementPath}\\TargetW", slot.Gw);
                _vom.Set($"{DraggedElementPath}\\TargetH", slot.Gh);
                
                _vom.Set($"{DraggedElementPath}\\Rotation", 0.0f);
                _vom.Set($"{DraggedElementPath}\\Z", 0.0f);
                _vom.Set($"{DraggedElementPath}\\BaseZSlot", 0);
            }
            else
            {
                _vom.Delete($"{DraggedElementPath}\\TargetX");
                _vom.Delete($"{DraggedElementPath}\\TargetY");
                _vom.Delete($"{DraggedElementPath}\\TargetW");
                _vom.Delete($"{DraggedElementPath}\\TargetH");
            }
        }

        _snapManager.FreeSnapGroup();

        if (DraggedElement != null && ActiveIntentMode == IntentMode.ZPush)
        {
            float z = _vom.Get<float>($"{DraggedElementPath}\\Z", 0f);
            int discreteZ = Math.Clamp((int)Math.Round(z * 10.0f), 0, 10);
            _vom.Set($"{DraggedElementPath}\\BaseZSlot", discreteZ);
            _vom.Delete($"{DraggedElementPath}\\TargetW");
            _vom.Delete($"{DraggedElementPath}\\TargetH");
        }

        DraggedElement = null;
        DraggedElementPath = "";
        ActiveIntentMode = IntentMode.None;
        IsLeftEdgeResize = IsRightEdgeResize = IsTopEdgeResize = IsBottomEdgeResize = false;
    }
}
