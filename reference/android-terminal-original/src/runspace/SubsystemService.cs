using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace TerminalApp;

[Service(ForegroundServiceType = ForegroundService.TypeDataSync, Exported = false)]
public class SubsystemService : Service
{
    private const int NotificationId = 1;
    private const string ChannelId  = "terminal_bg";

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        EnsureChannel();
        StartForeground(NotificationId, BuildNotification(), ForegroundService.TypeDataSync);
        return StartCommandResult.Sticky;
    }

    private void EnsureChannel()
    {
        var channel = new NotificationChannel(ChannelId, "Terminal", NotificationImportance.Low)
        {
            Description = "PowerShell is running"
        };
        ((NotificationManager)GetSystemService(NotificationService)!).CreateNotificationChannel(channel);
    }

    private Notification BuildNotification()
    {
        var reopen = PendingIntent.GetActivity(
            this, 0,
            new Intent(this, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop),
            PendingIntentFlags.Immutable)!;

        return new Notification.Builder(this, ChannelId)
            .SetContentTitle("Terminal")
            .SetContentText("PowerShell Subsystem Running")
            .SetSmallIcon(this.ApplicationInfo!.Icon)
            .SetContentIntent(reopen)
            .SetOngoing(true)
            .Build()!;
    }
}

