namespace Subsystem;

internal static class AdbLog
{
    public static void Debug(string tag, string message) =>
        Android.Util.Log.Debug("Terminal." + tag, message);

    public static void Warning(string tag, Exception error) =>
        Android.Util.Log.Warn("Terminal." + tag, error.ToString());
}
