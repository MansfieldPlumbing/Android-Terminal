using Android.App;
using Android.Content;
using Android.Hardware.Camera2;
using Android.OS;
using Android.Provider;

namespace NativePwshConsole;

public static class AndroidBridge
{
    private static Activity? _activity;
    private static bool _torch;

    public static void Configure(Activity activity) => _activity = activity;

    public static void Vibrate(int durationMs)
    {
        var manager = (VibratorManager?)_activity?.GetSystemService(Context.VibratorManagerService);
        var vibrator = manager?.DefaultVibrator;
        if (vibrator?.HasVibrator == true)
            vibrator.Vibrate(VibrationEffect.CreateOneShot(durationMs, VibrationEffect.DefaultAmplitude));
    }

    public static void SetFlashlight(string state = "Toggle")
    {
        if (_activity == null) throw new InvalidOperationException("Android activity is unavailable.");
        var manager = (CameraManager?)_activity.GetSystemService(Context.CameraService)
            ?? throw new InvalidOperationException("Camera service is unavailable.");
        string? cameraId = manager.GetCameraIdList().FirstOrDefault(id =>
            manager.GetCameraCharacteristics(id).Get(CameraCharacteristics.FlashInfoAvailable) is Java.Lang.Boolean available
            && available.BooleanValue());
        if (cameraId == null) throw new InvalidOperationException("No flashlight is available.");
        _torch = state.Equals("On", StringComparison.OrdinalIgnoreCase) ||
                 (!state.Equals("Off", StringComparison.OrdinalIgnoreCase) && !_torch);
        manager.SetTorchMode(cameraId, _torch);
    }

    public static object GetAdbStatus() => AdbLoopback.GetStatus();

    public static string RequestFileManagerAccess()
    {
        if (_activity == null) throw new InvalidOperationException("Android activity is unavailable.");
        if (Build.VERSION.SdkInt < BuildVersionCodes.R) return "Broad file access is not required on this Android version.";
        var intent = new Intent(Settings.ActionManageAppAllFilesAccessPermission);
        intent.SetData(Android.Net.Uri.Parse("package:" + _activity.PackageName));
        _activity.StartActivity(intent);
        return "Opened Android's file access settings. This never grants access to other apps' private data.";
    }

    public static void ShowSettings()
    {
        if (_activity is not MainActivity main) throw new InvalidOperationException("Console activity is unavailable.");
        main.RunOnUiThread(main.ShowSettingsMenu);
    }

    public static void StartSessionGuardian(string name, string endpoint)
    {
        if (_activity == null) throw new InvalidOperationException("Android activity is unavailable.");
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu &&
            _activity.CheckSelfPermission("android.permission.POST_NOTIFICATIONS") != Android.Content.PM.Permission.Granted)
            _activity.RequestPermissions(["android.permission.POST_NOTIFICATIONS"], 7001);

        var intent = new Intent(_activity, typeof(SessionGuardianService));
        intent.SetAction(SessionGuardianService.ActionStart);
        intent.PutExtra(SessionGuardianService.ExtraName, name);
        intent.PutExtra(SessionGuardianService.ExtraEndpoint, endpoint);
        _activity.StartForegroundService(intent);
    }

    public static SessionGuardianStatus GetSessionGuardian() => new(
        SessionGuardianService.IsRunning,
        SessionGuardianService.SessionName,
        SessionGuardianService.Endpoint);

    public static void RequestStopSessionGuardian()
    {
        if (_activity == null) throw new InvalidOperationException("Android activity is unavailable.");
        if (!SessionGuardianService.IsRunning) return;
        var intent = new Intent(_activity, typeof(ConfirmStopActivity));
        intent.PutExtra(SessionGuardianService.ExtraName, SessionGuardianService.SessionName);
        _activity.RunOnUiThread(() => _activity.StartActivity(intent));
    }

    public static string? ShowMenu(string title, string[] items)
    {
        if (_activity == null) throw new InvalidOperationException("Android activity is unavailable.");
        if (items.Length == 0) return null;
        using var done = new ManualResetEventSlim(false);
        string? selected = null;
        _activity.RunOnUiThread(() =>
        {
            new AlertDialog.Builder(_activity)
                .SetTitle(title)
                .SetItems(items, (_, e) => { selected = items[e.Which]; done.Set(); })
                .SetOnCancelListener(new CancelListener(done))
                .Show();
        });
        done.Wait();
        return selected;
    }

    public static bool EditFile(string path)
    {
        if (_activity is not MainActivity main) throw new InvalidOperationException("Console activity is unavailable.");
        return main.EditFile(path);
    }

    private sealed class CancelListener(ManualResetEventSlim done) : Java.Lang.Object, IDialogInterfaceOnCancelListener
    {
        public void OnCancel(IDialogInterface? dialog) => done.Set();
    }
}
