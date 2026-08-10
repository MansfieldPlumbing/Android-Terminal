using Android.Content;
using Android.App;
using System.Management.Automation;
using NativePwshConsole.Surface;
using NativePwshConsole.Hardpoints;
using Terminal.Engine;

namespace NativePwshConsole;

// Process authority for the PowerShell runspace. Activities are disposable presenters;
// the foreground service owns the runtime's deliberate start/stop lifecycle.
internal static class TerminalRuntime
{
    private static readonly object Gate = new();
    private static PowerShellSession? _session;
    private static TerminalEngine? _terminal;
    private static SurfaceHost? _surfaces;
    private static HardpointCatalog? _hardpoints;

    public static PowerShellSession GetOrCreate(Context context)
    {
        lock (Gate)
        {
            if (_session != null) return _session;
            string home = context.FilesDir!.AbsolutePath;
            string system = SeedSystemScripts(context, home);
            _hardpoints = HardpointCatalog.HydrateAndLoad(context, home);
            _session = new PowerShellSession(home, system);
            _terminal = new TerminalEngine();
            _session.Output += _terminal.Feed;
            foreach (string diagnostic in _session.StartupDiagnostics)
                _terminal.Feed($"\x1b[91m{diagnostic}\x1b[0m\r\n");
            _terminal.Feed($"PowerShell {PowerShellSession.EngineVersion}\r\n");
            _terminal.Feed(_session.GetPromptAsync().GetAwaiter().GetResult());
            _surfaces = new SurfaceHost(_session);
            return _session;
        }
    }

    public static TerminalEngine GetTerminalEngine(Context context)
    {
        lock (Gate)
        {
            _ = GetOrCreate(context);
            return _terminal ?? throw new InvalidOperationException("Terminal engine is unavailable.");
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            _surfaces?.Dispose();
            _surfaces = null;
            _session?.Dispose();
            _session = null;
            _terminal = null;
            _hardpoints = null;
        }
    }

    public static void AttachActivity(Activity activity)
    {
        lock (Gate) _surfaces?.Attach(activity);
    }

    public static void DetachActivity(Activity activity)
    {
        lock (Gate) _surfaces?.Detach(activity);
    }

    public static PSObject ShowSurface(SurfaceDocument document)
    {
        lock (Gate) return (_surfaces ?? throw new InvalidOperationException("Terminal Surface host is unavailable.")).Show(document);
    }

    public static void CloseSurface()
    {
        lock (Gate) _surfaces?.Close();
    }

    public static IReadOnlyCollection<TerminalHardpoint> GetHardpoints()
    {
        lock (Gate) return (_hardpoints?.All ?? []).ToArray();
    }

    public static TerminalHardpoint GetHardpoint(string id)
    {
        lock (Gate) return (_hardpoints ?? throw new InvalidOperationException("Hardpoint catalog is unavailable.")).Get(id);
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
