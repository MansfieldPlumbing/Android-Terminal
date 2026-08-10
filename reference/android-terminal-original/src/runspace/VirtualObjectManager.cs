using System;
using System.Collections.Concurrent;

namespace TerminalApp;

public static class VirtualObjectManager
{
    private static MainActivity? _host;

    public static MainActivity Host
    {
        get => _host ?? throw new InvalidOperationException("VOM Host not mapped. Native interop cannot proceed.");
        set => _host = value;
    }

    public static ConcurrentDictionary<string, object> Registry { get; } = new();

    public static ProjectionServer SpawnMicroserver(int port)
    {
        if (_host == null)
            throw new InvalidOperationException("VOM Host not mapped.");

        var server = new ProjectionServer(_host);
        Registry[$"HKOM\\Mailboxes\\Dawn_CommandQueue_Port{port}.ini"] = server;
        server.Start(port);
        return server;
    }

    // --- SAFE NATIVE HARDWARE BRIDGES ---

    public static void ShowToast(string message, bool isLong)
    {
        if (_host == null || _host.IsFinishing || _host.IsDestroyed) return;
        if (string.IsNullOrEmpty(message)) return;
        
        _host.RunOnUiThread(() => {
            try {
                if (_host.IsFinishing || _host.IsDestroyed) return;
                var length = isLong ? Android.Widget.ToastLength.Long : Android.Widget.ToastLength.Short;
                Android.Widget.Toast.MakeText(_host, message, length)?.Show();
            } catch { }
        });
    }

    public static void Vibrate(int durationMs)
    {
        if (_host == null) return;
        try {
            var vm = (Android.OS.VibratorManager?)_host.GetSystemService(Android.Content.Context.VibratorManagerService);
            var vib = vm?.DefaultVibrator;
            if (vib != null && vib.HasVibrator) {
                vib.Vibrate(Android.OS.VibrationEffect.CreateOneShot(durationMs, Android.OS.VibrationEffect.DefaultAmplitude));
            }
        } catch { }
    }

    private static bool _torchState = false;
    public static void SetFlashlight(string state = "Toggle")
    {
        if (_host == null) return;
        try {
            var cm = (Android.Hardware.Camera2.CameraManager?)_host.GetSystemService(Android.Content.Context.CameraService);
            if (cm != null) {
                var camId = cm.GetCameraIdList()[0];
                if (string.Equals(state, "On", StringComparison.OrdinalIgnoreCase)) {
                    _torchState = true;
                } else if (string.Equals(state, "Off", StringComparison.OrdinalIgnoreCase)) {
                    _torchState = false;
                } else {
                    _torchState = !_torchState;
                }
                cm.SetTorchMode(camId, _torchState);
            }
        } catch { }
    }
}
