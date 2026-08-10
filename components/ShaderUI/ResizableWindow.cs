using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using TuiDwm.Core;

namespace Subsystem;

/// <summary>
/// Native Win32 window host that captures touch, pen, and mouse inputs via the Windows Pointer API,
/// and feeds unified multi-touch gestures directly into the InputStateMachine.
/// </summary>
public sealed class Win32Window : IDisposable
{
    public event Action<int, int>? Resized;
    public event Action? CloseRequested;

    public nint Hwnd { get; private set; }
    public int ClientWidth { get; private set; } = 1920;
    public int ClientHeight { get; private set; } = 1080;

    private readonly string _className = "TuiDwm_GpuCompositor_" + Environment.ProcessId;
    private GCHandle _selfHandle;
    private WndProc _wndProcDelegate = null!;

    private InputStateMachine? _inputStateMachine;
    private readonly Dictionary<uint, POINT> _activePoints = new();

    public void Initialize(InputStateMachine ism)
    {
        _inputStateMachine = ism;
    }

    public void Create(string title, int w, int h)
    {
        ClientWidth = w;
        ClientHeight = h;
        _selfHandle = GCHandle.Alloc(this);
        _wndProcDelegate = WndProcCallback;

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0x0003, // CS_HREDRAW | CS_VREDRAW
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = GetModuleHandle(null),
            hCursor = LoadCursor(nint.Zero, 32512), // IDC_ARROW
            lpszClassName = _className,
        };
        RegisterClassEx(ref wc);

        var rect = new RECT { left = 0, top = 0, right = w, bottom = h };
        AdjustWindowRect(ref rect, WS_OVERLAPPEDWINDOW, false);

        Hwnd = CreateWindowEx(
            0, _className, title,
            WS_OVERLAPPEDWINDOW | WS_VISIBLE,
            CW_USEDEFAULT, CW_USEDEFAULT,
            rect.right - rect.left, rect.bottom - rect.top,
            nint.Zero, nint.Zero, wc.hInstance, GCHandle.ToIntPtr(_selfHandle));

        // Unify mouse, pen, and touch inputs into raw WM_POINTER events
        EnableMouseInPointer(true);
    }

    public void RunMessageLoop()
    {
        while (GetMessage(out var msg, nint.Zero, 0, 0) > 0)
         {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private static nint WndProcCallback(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        Win32Window? self = null;
        if (msg == WM_CREATE)
        {
            var cs = Marshal.PtrToStructure<CREATESTRUCT>(lParam);
            SetWindowLongPtr(hwnd, GWLP_USERDATA, cs.lpCreateParams);
        }

        nint ptr = GetWindowLongPtr(hwnd, GWLP_USERDATA);
        if (ptr != 0)
        {
            var gh = GCHandle.FromIntPtr(ptr);
            if (gh.IsAllocated) self = gh.Target as Win32Window;
        }

        if (self != null)
            return self.HandleMessage(hwnd, msg, wParam, lParam);

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private nint HandleMessage(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_SIZE:
                ClientWidth = (int)(lParam & 0xFFFF);
                ClientHeight = (int)((lParam >> 16) & 0xFFFF);
                Resized?.Invoke(ClientWidth, ClientHeight);
                return 0;

            case WM_DESTROY:
                CloseRequested?.Invoke();
                PostQuitMessage(0);
                return 0;

            case WM_POINTERDOWN:
                {
                    uint pid = (uint)(wParam & 0xFFFF);
                    GetPointerInfo(pid, out var info);
                    ScreenToClient(hwnd, ref info.ptPixelLocation);

                    _activePoints[pid] = info.ptPixelLocation;

                    if (_inputStateMachine != null)
                    {
                        var matrix = ComputeContactMatrix();
                        // Acquire intent for single or multi-touch dragging/pinching
                        _inputStateMachine.IsTouch = (info.pointerType == PT_TOUCH);
                        _inputStateMachine.AcquireIntent(matrix, IntentMode.Translate, ClientWidth, ClientHeight);
                    }
                    return 0;
                }

            case WM_POINTERUPDATE:
                {
                    uint pid = (uint)(wParam & 0xFFFF);
                    GetPointerInfo(pid, out var info);
                    ScreenToClient(hwnd, ref info.ptPixelLocation);

                    _activePoints[pid] = info.ptPixelLocation;

                    if (_inputStateMachine != null)
                    {
                        var matrix = ComputeContactMatrix();
                        _inputStateMachine.IsTouch = (info.pointerType == PT_TOUCH);
                        _inputStateMachine.MapIntent(matrix, ClientWidth, ClientHeight);
                    }
                    return 0;
                }

            case WM_POINTERUP:
                {
                    uint pid = (uint)(wParam & 0xFFFF);
                    _activePoints.Remove(pid);

                    _inputStateMachine?.ReleaseIntent();
                    return 0;
                }
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private ContactMatrix ComputeContactMatrix()
    {
        var matrix = new ContactMatrix();
        matrix.ActivePoints = _activePoints.Count;
        if (matrix.ActivePoints == 0) return matrix;

        float sumX = 0;
        float sumY = 0;
        foreach (var pt in _activePoints.Values)
        {
            sumX += pt.x;
            sumY += pt.y;
        }

        matrix.Cx = (sumX / matrix.ActivePoints) / ClientWidth;
        matrix.Cy = (sumY / matrix.ActivePoints) / ClientHeight;

        if (matrix.ActivePoints < 2)
        {
            matrix.DeltaZ = 0;
            matrix.DeltaRot = 0;
            return matrix;
        }

        float totalDist = 0;
        float totalAngle = 0;
        int pairs = 0;

        var pts = new List<POINT>(_activePoints.Values);
        for (int i = 0; i < pts.Count; i++)
        {
            for (int j = i + 1; j < pts.Count; j++)
            {
                float dx = pts[i].x - pts[j].x;
                float dy = pts[i].y - pts[j].y;
                totalDist += MathF.Sqrt(dx * dx + dy * dy);
                totalAngle += MathF.Atan2(dy, dx);
                pairs++;
            }
        }

        matrix.DeltaZ = totalDist / pairs;
        matrix.DeltaRot = totalAngle / pairs;

        return matrix;
    }

    public void Dispose()
    {
        if (Hwnd != 0) DestroyWindow(Hwnd);
        if (_selfHandle.IsAllocated) _selfHandle.Free();
    }

    // ── Win32 constants ───────────────────────────────────────────────────────
    private const uint WS_OVERLAPPEDWINDOW = 0xCF0000;
    private const uint WS_VISIBLE = 0x10000000;
    private const int CW_USEDEFAULT = unchecked((int)0x80000000);
    private const uint WM_SIZE = 0x0005;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_CREATE = 0x0001;
    private const uint WM_POINTERDOWN = 0x0246;
    private const uint WM_POINTERUPDATE = 0x0245;
    private const uint WM_POINTERUP = 0x0247;
    private const int GWLP_USERDATA = -21;
    private const uint PT_TOUCH = 2;

    // ── P/Invokes ─────────────────────────────────────────────────────────────
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32")] private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);
    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int X, int Y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);
    [DllImport("user32")] private static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32")] private static extern bool DestroyWindow(nint hWnd);
    [DllImport("user32")] private static extern int GetMessage(out MSG lpMsg, nint hWnd, uint min, uint max);
    [DllImport("user32")] private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32")] private static extern nint DispatchMessage(ref MSG lpMsg);
    [DllImport("user32")] private static extern bool AdjustWindowRect(ref RECT lpRect, uint dwStyle, bool bMenu);
    [DllImport("user32")] private static extern void PostQuitMessage(int nExitCode);
    [DllImport("user32")] private static extern nint GetModuleHandle(string? lpModuleName);
    [DllImport("user32")] private static extern nint LoadCursor(nint hInstance, int lpCursorName);
    [DllImport("user32")] private static extern bool EnableMouseInPointer(bool fEnable);
    [DllImport("user32")] private static extern bool GetPointerInfo(uint pointerId, out POINTER_INFO pointerInfo);
    [DllImport("user32")] private static extern bool ScreenToClient(nint hWnd, ref POINT lpPoint);

    [DllImport("user32", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
    [DllImport("user32", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    // ── Structs ───────────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize, style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public POINT pt; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CREATESTRUCT
    {
        public nint lpCreateParams, hInstance, hMenu, hwndParent;
        public int cy, cx, y, x;
        public uint style;
        public nint lpszName, lpszClass;
        public uint dwExStyle;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_INFO
    {
        public uint pointerType, pointerId, frameId, pointerFlags;
        public nint sourceDevice;
        public nint hwndTarget;
        public POINT ptPixelLocation, ptHimetricLocation;
        public POINT ptPixelLocationRaw, ptHimetricLocationRaw;
        public uint dwTime, historyCount, inputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public uint ButtonChangeType;
    }
}
