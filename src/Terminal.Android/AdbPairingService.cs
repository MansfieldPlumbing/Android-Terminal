using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace NativePwshConsole;

[Service(Name = "dev.mansfieldplumbing.terminal.AdbPairingService", Exported = false,
    ForegroundServiceType = ForegroundService.TypeSpecialUse)]
public sealed class AdbPairingService : Service
{
    private const string ChannelId = "self_adb_v2";
    private const int NotificationId = 4201;
    private const string ActionReady = "dev.mansfieldplumbing.terminal.ADB_READY";
    private const string ActionStatus = "dev.mansfieldplumbing.terminal.ADB_STATUS";
    private const string ActionStop = "dev.mansfieldplumbing.terminal.ADB_STOP";
    private const string ActionRenotify = "dev.mansfieldplumbing.terminal.ADB_RENOTIFY";
    private string _text = "Waiting for Android's pairing-code screen...";
    private bool _ready;

    public override IBinder? OnBind(Intent? intent) => null;

    public static void Start(Context context) =>
        SessionGuardianService.SetAdbStatus(context, "Self-ADB · Waiting for pairing screen");

    public static void PairingReady(Context context, int port)
    {
        SessionGuardianService.SetAdbStatus(context, "Self-ADB · Pairing screen detected", true);
    }

    public static void SetStatus(Context context, string text, bool ready = false)
    {
        SessionGuardianService.SetAdbStatus(context, "Self-ADB · " + text, ready);
    }

    public static void Stop(Context context)
    {
        SessionGuardianService.SetAdbStatus(context, null);
    }

    private static void Send(Context context, Intent intent)
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O) context.StartForegroundService(intent);
        else context.StartService(intent);
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        EnsureChannel();
        if (intent?.Action == ActionStop)
        {
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
            return StartCommandResult.NotSticky;
        }
        if (intent?.Action == ActionReady)
        {
            _ready = true;
            _text = intent.GetStringExtra("text") ?? "Pairing page detected";
        }
        else if (intent?.Action == ActionStatus)
        {
            _text = intent.GetStringExtra("text") ?? _text;
            _ready = intent.GetBooleanExtra("ready", false);
        }
        StartForeground(NotificationId, BuildNotification(), ForegroundService.TypeSpecialUse);
        return StartCommandResult.Sticky;
    }

    private void EnsureChannel()
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        var channel = new NotificationChannel(ChannelId, "Self-ADB pairing", NotificationImportance.Default)
        {
            Description = "Durable wireless-debugging pairing controls"
        };
        channel.SetSound(null, null);
        manager.CreateNotificationChannel(channel);
        manager.DeleteNotificationChannel("self_adb_v1");
    }

    private Notification BuildNotification()
    {
        var open = new Intent(this, typeof(MainActivity));
        open.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        var openPending = PendingIntent.GetActivity(this, 4201, open,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var renotify = new Intent(this, typeof(AdbPairingService));
        renotify.SetAction(ActionRenotify);
        var renotifyPending = PendingIntent.GetService(this, 4202, renotify,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var builder = new Notification.Builder(this, ChannelId)
            .SetSmallIcon(Resource.Drawable.notification_terminal)
            .SetColor(Android.Graphics.Color.ParseColor("#012456").ToArgb())
            .SetContentTitle("Terminal · self-ADB")
            .SetContentText(_text)
            .SetContentIntent(openPending)
            .SetDeleteIntent(renotifyPending)
            .SetOngoing(true)
            .SetOnlyAlertOnce(false)
            .SetCategory(Notification.CategoryService);

        if (_ready)
        {
            var input = new RemoteInput.Builder(AdbPairingReceiver.ResultKey)
                .SetLabel("Enter 6-digit code")
                .Build();
            var reply = new Intent(this, typeof(AdbPairingReceiver));
            reply.SetAction(AdbPairingReceiver.ActionPair);
            var replyPending = PendingIntent.GetBroadcast(this, 4203, reply,
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Mutable);
            builder.AddAction(new Notification.Action.Builder(null, "Enter code", replyPending)
                .AddRemoteInput(input).Build());
        }
        return builder.Build();
    }
}

[BroadcastReceiver(Name = "dev.mansfieldplumbing.terminal.AdbPairingReceiver", Exported = false)]
[IntentFilter([AdbPairingReceiver.ActionPair])]
public sealed class AdbPairingReceiver : BroadcastReceiver
{
    public const string ActionPair = "dev.mansfieldplumbing.terminal.PAIR_ADB";
    public const string ResultKey = "pairing_code";

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context == null || intent?.Action != ActionPair) return;
        string code = RemoteInput.GetResultsFromIntent(intent)?.GetCharSequence(ResultKey)?
            .Replace(" ", "").Trim() ?? string.Empty;
        if (code.Length != 6)
        {
            AdbPairingService.SetStatus(context, "Enter Android's six-digit pairing code", true);
            return;
        }
        // A BroadcastReceiver runs on Android's main thread and may be reclaimed as soon as
        // OnReceive returns. GoAsync owns the receipt; SubmitPinAsync owns a worker thread.
        PendingResult pending = GoAsync();
        _ = CompletePairingAsync(context.ApplicationContext!, code, pending);
    }

    private static async Task CompletePairingAsync(Context context, string code, PendingResult pending)
    {
        try
        {
            AdbPairingService.SetStatus(context, "Pairing...");
            await AdbLoopback.SubmitPinAsync(code).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AdbPairingService.SetStatus(context, "Pairing failed: " + ex.Message, true);
        }
        finally { pending.Finish(); }
    }
}
