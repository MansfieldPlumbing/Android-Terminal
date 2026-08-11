using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace NativePwshConsole;

[Service(Name = "dev.mansfieldplumbing.nativepwshconsole.SessionGuardianService",
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeSpecialUse)]
public sealed class SessionGuardianService : Service
{
    public const string ChannelId = "terminal_session_v2";
    public const int NotificationId = 4001;
    public const string ActionStart = "dev.mansfieldplumbing.nativepwshconsole.START_GUARDIAN";
    public const string ActionRenotify = "dev.mansfieldplumbing.nativepwshconsole.RENOTIFY_GUARDIAN";
    public const string ActionAdbStatus = "dev.mansfieldplumbing.terminal.ADB_GUARDIAN_STATUS";
    public const string ActionStopConfirmed = "dev.mansfieldplumbing.nativepwshconsole.STOP_GUARDIAN_CONFIRMED";
    public const string ExtraName = "session_name";
    public const string ExtraEndpoint = "session_endpoint";

    public static bool IsRunning { get; private set; }
    public static string SessionName { get; private set; } = "PowerShell session";
    public static string Endpoint { get; private set; } = "Local session";
    private static bool _shuttingDown;
    private static string? _adbStatus;
    private static bool _adbReady;

    public static void SetAdbStatus(Context context, string? text, bool ready = false)
    {
        var intent = new Intent(context, typeof(SessionGuardianService));
        intent.SetAction(ActionAdbStatus);
        intent.PutExtra("text", text);
        intent.PutExtra("ready", ready);
        context.StartForegroundService(intent);
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionRenotify)
        {
            if (_shuttingDown)
            {
                StopSelf();
                return StartCommandResult.NotSticky;
            }
            CreateChannel();
            StartForeground(NotificationId, BuildNotification(), ForegroundService.TypeSpecialUse);
            IsRunning = true;
            return StartCommandResult.Sticky;
        }

        if (intent?.Action == ActionAdbStatus)
        {
            _adbStatus = intent.GetStringExtra("text");
            _adbReady = intent.GetBooleanExtra("ready", false);
            CreateChannel();
            StartForeground(NotificationId, BuildNotification(), ForegroundService.TypeSpecialUse);
            IsRunning = true;
            return StartCommandResult.Sticky;
        }

        if (intent?.Action == ActionStopConfirmed)
        {
            _shuttingDown = true;
            TerminalRuntime.Stop();
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
            IsRunning = false;
            return StartCommandResult.NotSticky;
        }

        SessionName = intent?.GetStringExtra(ExtraName) ?? SessionName;
        Endpoint = intent?.GetStringExtra(ExtraEndpoint) ?? Endpoint;
        _shuttingDown = false;
        CreateChannel();
        StartForeground(NotificationId, BuildNotification(), ForegroundService.TypeSpecialUse);
        IsRunning = true;
        _ = Task.Run(() =>
        {
            try { TerminalRuntime.GetOrCreate(this); }
            catch (Exception exception) { Android.Util.Log.Error("TerminalRuntime", exception.ToString()); }
        });
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        IsRunning = false;
        base.OnDestroy();
    }

    private void CreateChannel()
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        var channel = new NotificationChannel(ChannelId, "Active sessions", NotificationImportance.Low)
        {
            Description = "Durable controls for PowerShell and local microserver sessions"
        };
        channel.SetShowBadge(false);
        manager.CreateNotificationChannel(channel);
        manager.DeleteNotificationChannel("active_sessions");
        manager.DeleteNotificationChannel("self_adb_v1");
        manager.DeleteNotificationChannel("self_adb_v2");
    }

    private Notification BuildNotification()
    {
        var openIntent = new Intent(this, typeof(MainActivity));
        openIntent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        var openPending = PendingIntent.GetActivity(this, 4101, openIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var stopIntent = new Intent(this, typeof(ConfirmStopActivity));
        stopIntent.PutExtra(ExtraName, SessionName);
        var stopPending = PendingIntent.GetActivity(this, 4102, stopIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var renotifyIntent = new Intent(this, typeof(SessionGuardianService));
        renotifyIntent.SetAction(ActionRenotify);
        var renotifyPending = PendingIntent.GetService(this, 4103, renotifyIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var builder = new Notification.Builder(this, ChannelId)
            .SetSmallIcon(Resource.Drawable.notification_terminal)
            .SetColor(Android.Graphics.Color.ParseColor("#012456").ToArgb())
            .SetContentTitle("Terminal")
            .SetContentText(_adbStatus)
            .SetContentIntent(openPending)
            .SetOngoing(true)
            .SetAutoCancel(false)
            .SetDeleteIntent(renotifyPending)
            .SetCategory(Notification.CategoryService)
            .SetOnlyAlertOnce(true)
            .AddAction(new Notification.Action.Builder(null, "Open", openPending).Build())
            .AddAction(new Notification.Action.Builder(null, "Stop…", stopPending).Build())
            ;
        if (_adbReady)
        {
            var input = new RemoteInput.Builder(AdbPairingReceiver.ResultKey)
                .SetLabel("Enter 6-digit code").Build();
            var reply = new Intent(this, typeof(AdbPairingReceiver));
            reply.SetAction(AdbPairingReceiver.ActionPair);
            var replyPending = PendingIntent.GetBroadcast(this, 4104, reply,
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Mutable);
            builder.AddAction(new Notification.Action.Builder(null, "Enter code", replyPending)
                .AddRemoteInput(input).Build());
        }
        return builder.Build();
    }
}

[Activity(Name = "dev.mansfieldplumbing.nativepwshconsole.ConfirmStopActivity",
    Exported = false, ExcludeFromRecents = true,
    Theme = "@android:style/Theme.Material.Dialog.Alert")]
public sealed class ConfirmStopActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        string name = Intent?.GetStringExtra(SessionGuardianService.ExtraName) ?? "this session";
        new AlertDialog.Builder(this)
            .SetTitle("Stop protected session?")
            .SetMessage($"Stop {name}? Any server or remote session attached to it will become unavailable.")
            .SetNegativeButton("Keep running", (_, _) => Finish())
            .SetPositiveButton("Stop", (_, _) =>
            {
                var stop = new Intent(this, typeof(SessionGuardianService));
                stop.SetAction(SessionGuardianService.ActionStopConfirmed);
                StartService(stop);
                Finish();
            })
            .SetOnCancelListener(new CancelListener(this))
            .Show();
    }

    private sealed class CancelListener(Activity owner) : Java.Lang.Object, IDialogInterfaceOnCancelListener
    {
        public void OnCancel(IDialogInterface? dialog) => owner.Finish();
    }
}

public sealed record SessionGuardianStatus(bool Running, string Name, string Endpoint);
