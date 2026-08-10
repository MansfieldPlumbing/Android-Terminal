using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;

namespace NativePwshConsole;

public static class Oobe
{
    private const string PreferencesName = "onboarding";
    private const string CompletedKey = "welcome_completed_v1";

    public static bool IsCompleted(Activity activity) =>
        activity.GetSharedPreferences(PreferencesName, FileCreationMode.Private)?.GetBoolean(CompletedKey, false) == true;

    public static void ShowIfNeeded(Activity activity)
    {
        if (!IsCompleted(activity)) Show(activity);
    }

    public static void Show(Activity activity)
    {
        int Dp(int n) => (int)(n * activity.Resources!.DisplayMetrics!.Density + .5f);
        TextView Text(string value, float size, string color)
        {
            var view = new TextView(activity) { Text = value, TextSize = size };
            view.SetTextColor(Color.ParseColor(color));
            view.SetPadding(Dp(18), Dp(9), Dp(18), Dp(9));
            return view;
        }
        LinearLayout Card(string title, string explanation)
        {
            var card = new LinearLayout(activity) { Orientation = Orientation.Vertical };
            var background = new GradientDrawable();
            background.SetColor(Color.ParseColor("#262626"));
            background.SetCornerRadius(Dp(10));
            background.SetStroke(Dp(1), Color.ParseColor("#454545"));
            card.Background = background;
            card.AddView(Text(title, 17, "#F5F5F5"));
            card.AddView(Text(explanation, 13, "#B5B5B5"));
            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            lp.SetMargins(Dp(16), Dp(7), Dp(16), Dp(7));
            card.LayoutParameters = lp;
            return card;
        }
        Button Action(string label, Action action)
        {
            var button = new Button(activity) { Text = label };
            button.SetAllCaps(false);
            button.Click += (_, _) => action();
            return button;
        }

        var body = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        body.SetPadding(0, Dp(10), 0, Dp(24));
        body.AddView(Text("Welcome to Terminal", 27, "#F5F5F5"));
        body.AddView(Text("A native CoreCLR + PowerShell workspace. The console works immediately; every capability below is optional and can be changed later in Settings.", 14, "#B5B5B5"));

        var notifications = Card("Session notifications", "Shows durable controls while a PowerShell, PSRP, or microserver session is active. Android may hide these controls if notifications are denied. This does not read anybody else’s notifications.");
        notifications.AddView(Action("Allow session notifications", () =>
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
                activity.RequestPermissions(["android.permission.POST_NOTIFICATIONS"], 7001);
        }));
        body.AddView(notifications);

        var files = Card("File manager access", "Lets PowerShell work with shared storage you choose. Broad access is powerful and is not required for the app’s private sandbox. It cannot open another app’s private files.");
        files.AddView(Action("Review file access in Android Settings", () =>
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            {
                var intent = new Intent(Settings.ActionManageAppAllFilesAccessPermission);
                intent.SetData(Android.Net.Uri.Parse("package:" + activity.PackageName));
                activity.StartActivity(intent);
            }
        }));
        body.AddView(files);

        var device = Card("Device controls", "Camera permission is used only for flashlight cmdlets. Vibration uses the phone’s vibration service. Denying either leaves PowerShell and the console fully usable.");
        device.AddView(Action("Allow flashlight", () => activity.RequestPermissions(["android.permission.CAMERA"], 7002)));
        body.AddView(device);

        var adb = Card("Wireless debugging / self-ADB", "Optional elevated Android shell access, similar in practice to Shizuku. Pairing uses Android’s six-digit code and an app-private key. It can inspect and change substantially more of the device, so it is never part of a bulk-enable action.");
        adb.AddView(Action("Set up self-ADB", AdbLoopback.BeginSetup));
        body.AddView(adb);

        body.AddView(Text("Recommended means notifications only. File access, camera, and self-ADB remain separate decisions.", 12, "#8FA7B8"));

        var scroll = new ScrollView(activity);
        scroll.AddView(body);
        AlertDialog? dialog = null;
        dialog = new AlertDialog.Builder(activity)
            .SetView(scroll)
            .SetNegativeButton("Use sandbox only", (_, _) => Complete(activity))
            .SetPositiveButton("Continue", (_, _) => Complete(activity))
            .Create();
        dialog.SetCanceledOnTouchOutside(false);
        dialog.Show();
        dialog.Window?.SetLayout(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
    }

    private static void Complete(Activity activity) =>
        activity.GetSharedPreferences(PreferencesName, FileCreationMode.Private)!
            .Edit()!.PutBoolean(CompletedKey, true)!.Apply();
}
