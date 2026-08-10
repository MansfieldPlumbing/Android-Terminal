using Android.Content;

namespace NativePwshConsole;

// Process authority for the PowerShell runspace. Activities are disposable presenters;
// the foreground service owns the runtime's deliberate start/stop lifecycle.
internal static class TerminalRuntime
{
    private static readonly object Gate = new();
    private static PowerShellSession? _session;

    public static PowerShellSession GetOrCreate(Context context)
    {
        lock (Gate)
        {
            if (_session != null) return _session;
            string home = context.FilesDir!.AbsolutePath;
            string system = SeedSystemScripts(context, home);
            _session = new PowerShellSession(home, system);
            return _session;
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            _session?.Dispose();
            _session = null;
        }
    }

    private static string SeedSystemScripts(Context context, string home)
    {
        string directory = System.IO.Path.Combine(home, ".System");
        System.IO.Directory.CreateDirectory(directory);
        string[] scripts =
        {
            "ConvertFrom-DumpsysTree.ps1", "ConvertFrom-KeyValue.ps1",
            "ConvertFrom-Settings.ps1", "ConvertFrom-Table.ps1", "Test-Parsers.ps1"
        };
        foreach (string name in scripts)
        {
            string targetPath = System.IO.Path.Combine(directory, name);
            if (System.IO.File.Exists(targetPath)) continue;
            using var source = context.Assets!.Open("System/" + name);
            using var target = System.IO.File.Create(targetPath);
            source.CopyTo(target);
        }
        return directory;
    }
}
