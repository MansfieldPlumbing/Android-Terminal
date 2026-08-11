using System.Security.Cryptography;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Content.PM;
using Android.Provider;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using Subsystem;

namespace NativePwshConsole;

public sealed record AdbLoopbackStatus(bool OptedIn, bool WirelessDebugging, bool PairingServiceFound,
    bool Connected, int? PairingPort, int? ConnectPort, string Authority, string? Identity, string? Error);

public static class AdbLoopback
{
    private const string Preferences = "capabilities";
    private const string OptInKey = "self_adb_opt_in";
    private const string PairingChannelId = "adb_pairing";
    private const int PairingNotificationId = 4201;
    private static Activity? _activity;
    private static AdbMdnsDiscoverer? _pairDiscovery;
    private static AdbMdnsDiscoverer? _connectDiscovery;
    private static AdbConnection? _connection;
    private static RSA? _key;
    private static int? _pairingPort;
    private static int? _connectPort;
    private static bool _promptVisible;
    private static bool _connecting;
    private static string? _identity;
    private static string? _error;

    public static bool IsConnected => _connection != null;

    public static void Configure(Activity activity) => _activity = activity;

    public static void BeginSetup()
    {
        Activity activity = _activity ?? throw new InvalidOperationException("Android activity is unavailable.");
        activity.GetSharedPreferences(Preferences, FileCreationMode.Private)!.Edit()!
            .PutBoolean(OptInKey, true)!.Apply();
        AdbPairingService.Start(activity);
        activity.StartActivity(new Intent(Settings.ActionApplicationDevelopmentSettings));
    }

    public static void ResumeDiscovery()
    {
        Activity? activity = _activity;
        if (activity == null || !IsOptedIn(activity) || !WirelessEnabled(activity) || IsConnected) return;
        _pairDiscovery ??= new AdbMdnsDiscoverer(activity, AdbMdnsDiscoverer.PairingService);
        _pairDiscovery.OnPortDiscovered = port =>
        {
            _pairingPort = port;
            activity.RunOnUiThread(() =>
            {
                AdbPairingService.PairingReady(activity, port);
            });
        };
        _pairDiscovery.OnServiceLost = () =>
        {
            _pairingPort = null;
            AdbPairingService.SetStatus(activity,
                "Waiting for Android's pairing-code screen...", false);
        };
        try
        {
            _pairDiscovery.StartDiscovery();
            string keyPath = System.IO.Path.Combine(activity.FilesDir!.AbsolutePath, "adbkey.pk8");
            if (System.IO.File.Exists(keyPath)) StartConnectDiscovery(activity, LoadOrCreateKey(activity));
        }
        catch (Exception ex) { _error = ex.Message; }
    }

    public static AdbLoopbackStatus GetStatus()
    {
        Activity? activity = _activity;
        return new(activity != null && IsOptedIn(activity), activity != null && WirelessEnabled(activity),
            _pairingPort.HasValue, IsConnected, _pairingPort, _connectPort,
            IsConnected ? "Android shell (uid 2000)" : "Application sandbox", _identity, _error);
    }

    public static string Shell(string command) => (_connection ??
        throw new InvalidOperationException("Self-ADB is not connected."))
        .ExecuteShellAsync(command).GetAwaiter().GetResult();

    public static Task SubmitPinAsync(string pin)
    {
        Activity activity = _activity ?? throw new InvalidOperationException("Android activity is unavailable.");
        int port = _pairingPort ?? throw new InvalidOperationException("No active pairing service was discovered.");
        string normalized = pin.Replace(" ", "").Trim();
        return Task.Run(() => PairAndConnectAsync(activity, port, normalized));
    }

    public static void Forget()
    {
        Activity activity = _activity ?? throw new InvalidOperationException("Android activity is unavailable.");
        _pairDiscovery?.StopDiscovery();
        _connectDiscovery?.StopDiscovery();
        _pairDiscovery = null; _connectDiscovery = null;
        CancelPairingNotification(activity);
        AdbPairingService.Stop(activity);
        _connection?.Dispose(); _connection = null;
        _pairingPort = null; _connectPort = null; _identity = null; _error = null; _connecting = false;
        activity.GetSharedPreferences(Preferences, FileCreationMode.Private)!.Edit()!
            .Remove(OptInKey)!.Apply();
        string keyPath = System.IO.Path.Combine(activity.FilesDir!.AbsolutePath, "adbkey.pk8");
        if (System.IO.File.Exists(keyPath)) System.IO.File.Delete(keyPath);
        _key?.Dispose(); _key = null;
    }

    private static void ShowPinPrompt(Activity activity, int port)
    {
        if (_promptVisible || IsConnected) return;
        CancelPairingNotification(activity);
        _promptVisible = true;
        var input = new EditText(activity)
        {
            Hint = "6-digit pairing code",
            InputType = InputTypes.ClassNumber,
            ImeOptions = ImeAction.Done
        };
        input.SetSingleLine(true);
        input.SetFilters([new InputFilterLengthFilter(6)]);
        var dialog = new AlertDialog.Builder(activity)
            .SetTitle("Pair self-ADB")
            .SetMessage($"Wireless debugging found on 127.0.0.1:{port}. Enter Android’s six-digit pairing code. This grants shell authority (uid 2000) until you forget the pairing.")
            .SetView(input)
            .SetNegativeButton("Not now", (_, _) => _promptVisible = false)
            .SetPositiveButton("Pair", (_, _) =>
            {
                string pin = input.Text?.Trim() ?? string.Empty;
                _promptVisible = false;
                if (pin.Length == 6) _ = SubmitPinAsync(pin);
            })
            .SetOnCancelListener(new PromptCancel(() => _promptVisible = false))
            .Create();
        dialog.Show();
        input.RequestFocus();
        dialog.Window?.SetSoftInputMode(SoftInput.StateAlwaysVisible);
    }

    private static void NotifyPairingReady(Activity activity)
    {
        Toast.MakeText(activity, "Pairing service found - return to Terminal to enter the code.", ToastLength.Long)?.Show();
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu &&
            activity.CheckSelfPermission("android.permission.POST_NOTIFICATIONS") != Permission.Granted) return;

        var manager = (NotificationManager)activity.GetSystemService(Context.NotificationService)!;
        var channel = new NotificationChannel(PairingChannelId, "ADB pairing", NotificationImportance.High)
        {
            Description = "One-time prompt when Android wireless-debugging pairing is ready"
        };
        manager.CreateNotificationChannel(channel);
        var open = new Intent(activity, typeof(MainActivity));
        open.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        var pending = PendingIntent.GetActivity(activity, PairingNotificationId, open,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        var notification = new Notification.Builder(activity, PairingChannelId)
            .SetSmallIcon(Resource.Drawable.notification_terminal)
            .SetColor(Android.Graphics.Color.ParseColor("#012456").ToArgb())
            .SetContentTitle("Enter wireless-debugging code")
            .SetContentText("Tap to finish self-ADB pairing in Terminal")
            .SetContentIntent(pending)
            .SetAutoCancel(true)
            .SetCategory(Notification.CategoryStatus)
            .Build();
        manager.Notify(PairingNotificationId, notification);
    }

    private static void CancelPairingNotification(Activity activity) =>
        ((NotificationManager)activity.GetSystemService(Context.NotificationService)!).Cancel(PairingNotificationId);

    private static async Task PairAndConnectAsync(Activity activity, int port, string pin)
    {
        try
        {
            _pairDiscovery?.StopDiscovery(); _pairDiscovery = null;
            RSA key = LoadOrCreateKey(activity);
            using var pairing = new AdbPairingClient("127.0.0.1", port, pin, key);
            if (!await pairing.PairAsync()) throw new InvalidOperationException("Android rejected the pairing.");
            AdbPairingService.SetStatus(activity, "Paired; connecting to Android shell...");
            StartConnectDiscovery(activity, key);
        }
        catch (Exception ex) { _error = ex.Message; AdbPairingService.SetStatus(activity, "Pairing failed: " + ex.Message, true); }
    }

    private static async Task ConnectAsync(int port, RSA key)
    {
        if (_connection != null || _connecting) return;
        _connecting = true;
        try
        {
            var connection = new AdbConnection(key, new ConscryptAdbTransport());
            await connection.ConnectAsync("127.0.0.1", port);
            string identity = (await connection.ExecuteShellAsync("id")).Trim();
            if (!identity.Contains("uid=2000", StringComparison.Ordinal))
            {
                connection.Dispose();
                throw new InvalidOperationException("ADB connected but did not prove shell authority: " + identity);
            }
            _connection = connection; _connectPort = port; _identity = identity; _error = null;
            if (_activity != null) SessionGuardianService.SetAdbStatus(_activity, "ADB connected");
            _connectDiscovery?.StopDiscovery(); _connectDiscovery = null;
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _connecting = false; }
    }

    private static void StartConnectDiscovery(Activity activity, RSA key)
    {
        if (_connectDiscovery != null || IsConnected) return;
        _connectDiscovery = new AdbMdnsDiscoverer(activity, AdbMdnsDiscoverer.ConnectService);
        _connectDiscovery.OnPortDiscovered = connectPort => _ = Task.Run(() => ConnectAsync(connectPort, key));
        _connectDiscovery.StartDiscovery();
    }

    private static RSA LoadOrCreateKey(Activity activity)
    {
        if (_key != null) return _key;
        string path = System.IO.Path.Combine(activity.FilesDir!.AbsolutePath, "adbkey.pk8");
        var key = RSA.Create(2048);
        if (System.IO.File.Exists(path)) key.ImportPkcs8PrivateKey(System.IO.File.ReadAllBytes(path), out _);
        else System.IO.File.WriteAllBytes(path, key.ExportPkcs8PrivateKey());
        return _key = key;
    }

    private static bool IsOptedIn(Activity activity) =>
        activity.GetSharedPreferences(Preferences, FileCreationMode.Private)?.GetBoolean(OptInKey, false) == true;
    private static bool WirelessEnabled(Activity activity) =>
        Settings.Global.GetInt(activity.ContentResolver, "adb_wifi_enabled", 0) != 0;

    private sealed class PromptCancel(Action action) : Java.Lang.Object, IDialogInterfaceOnCancelListener
    {
        public void OnCancel(IDialogInterface? dialog) => action();
    }
}
