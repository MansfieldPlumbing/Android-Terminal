using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using System.Text.Json;
using Subsystem.Vom;
using static Subsystem.Vom.Vom;

namespace Subsystem;

// Rs — Remote Sessions: the PSRP subsystem (the [MS-PSRP] endpoint from docs/STATUS.md's roadmap).
//
// Two halves, one mechanism:
//   1. The ENDPOINT — a real PowerShell-remoting listener on the named pipe "Subsystem.Psrp"
//      (a Unix domain socket in the app's private cache dir). This is the SAME server path
//      `pwsh -CustomPipeName` uses — full MS-PSRP: fragmentation, CLIXML, runspace-pool
//      negotiation — via the PUBLIC SMA surface (RemoteSessionNamedPipeServer), no reflection.
//   2. The BROKER — presenter-facing sessions. Each session is ONE remote runspace connected over
//      that pipe (NamedPipeConnectionInfo loopback), owned by a VOM owner under \Sessions\Psrp\{id}
//      so the kernel's termination token applies: Close = Stop pipeline → close runspace →
//      Terminate(owner) (cancel + DropPrefix). State ($PWD, variables) persists per session.
//
// Commands are STRUCTURED (AddCommand/AddParameter): parameters cross the seam as DATA, never
// spliced into a script string — the string-interpolation injection class dies at this boundary.
//
// The capability record \Capability\Remoting\Psrp (Registrar 3d) is the ONE truth for whether this
// surface is reachable — the /psrp routes consult .Enabled, exactly like /clixml.
//
// KNOWN BOUND (public-API constraint, parked): SMA's custom-pipe server is a singleton per pipe
// name and serves ONE connection at a time (it re-listens after a disconnect; creating a second
// name disposes the first listener). So Rs brokers ONE live pipe session, with owner-reclaim:
// re-opening with the same owner tag closes the stale predecessor (the WebView-reload case heals
// itself), a different owner gets a typed "busy" refusal, and idle sessions are reaped on access
// (no standing timer thread). N concurrent sessions = a parking space: per-session listeners need
// SMA's internal ctor or an upstream API; revisit when needed.
public sealed class PsrpSession : IDisposable
{
    public string   Id       { get; }
    public string   OwnerTag { get; }
    public DateTime Created  { get; } = DateTime.Now;
    public DateTime LastUsed { get; internal set; } = DateTime.Now;
    public bool     Busy     { get; internal set; }

    // VOM-SPEC §4: the session is an Owner; Remove/Close runs the kernel termination token over
    // \Sessions\Psrp\{Id} (cancel + DropPrefix) — same discipline as ManagedSession.
    public Owner VomOwner { get; }

    internal Runspace    Runspace { get; }
    internal object      Gate     { get; } = new();
    internal PowerShell? Current  { get; set; }   // the in-flight pipeline, so Close can Stop() it

    public string State => Runspace.RunspaceStateInfo.State.ToString();

    internal PsrpSession(string id, string ownerTag, Runspace runspace)
    {
        Id       = id;
        OwnerTag = ownerTag;
        Runspace = runspace;
        VomOwner = CreateOwner($"\\Sessions\\Psrp\\{id}");
    }

    public void Dispose()
    {
        // CoreCLR cannot force-abort a wedged managed thread; the deterministic kill here is
        // Stop → close the runspace → drop the pipe transport (the server side cleans up with it).
        try { Current?.Stop(); } catch (Exception ex) { Dg.Warn("rs", ex); }
        try { Runspace.Close(); Runspace.Dispose(); } catch (Exception ex) { Dg.Warn("rs", ex); }
    }
}

public static class Rs
{
    public const string PipeName       = "Subsystem.Psrp";
    public const string CapabilityPath = "\\Capability\\Remoting\\Psrp";

    // The SHARED lane: one multiplexed session for stateless presenters (files, edit, …) — they pass
    // absolute paths and carry no runspace state, so they serialize safely over ONE remote runspace
    // and the single-connection pipe bound never bites them. Exclusive sessions (Open) remain for
    // stateful consumers and EVICT the shared lane (it reconnects lazily on the next shared invoke).
    public const string SharedId = "shared";

    private const string BootstrapAsset = "shell/cli/psrp-bootstrap.ps1";
    private static readonly TimeSpan IdleTtl = TimeSpan.FromMinutes(10);

    private static readonly ConcurrentDictionary<string, PsrpSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _connectGate = new();
    private static string? _bootstrap;

    // Authority is possession: the Cm record is the switch, not this code path.
    public static bool Granted() => Subsystem.Cm.Cm.Get(CapabilityPath) is { Enabled: true };

    public static PsrpSession? Get(string id) => _sessions.TryGetValue(id, out var s) ? s : null;
    public static PsrpSession[] List() => _sessions.Values.ToArray();

    // .NET named pipes on Unix are domain sockets at Path.GetTempPath()/CoreFxPipe_<name>. The
    // .NET-Android runtime points TMPDIR at the app's cache dir, but that is exactly the kind of
    // host-specific assumption the surge-protector doctrine says to ground explicitly: if the temp
    // path is unusable, re-route TMPDIR to the cache dir before any pipe is created.
    private static void EnsureTempDir()
    {
        try
        {
            var tmp = Cm.Cm.Get(@"\System\Config\TempDir")?.ManifestJson ?? AppContext.BaseDirectory;
            if (Directory.Exists(tmp)) return;
#if ANDROID || __ANDROID__
            var cache = MainActivity.Instance?.CacheDir?.AbsolutePath;
            if (!string.IsNullOrEmpty(cache))
            {
                Directory.CreateDirectory(cache);
                Environment.SetEnvironmentVariable("TMPDIR", cache);
                Dg.Log("rs", $"TMPDIR was unusable ({tmp}); grounded to {cache}");
            }
#endif
        }
        catch (Exception ex) { Dg.Log("rs", "EnsureTempDir: " + ex.Message); }
    }

    private static bool _appBaseGrounded;

    // The PSHOME the grounded app base points at. A real dir under files/ so the PSRP server's
    // file-based type/format loader has somewhere to read (see EnsureAppBase).
    private static string PsHome =>
        Path.Combine(MainActivity.Instance?.FilesDir?.AbsolutePath ?? (Cm.Cm.Get(@"\System\Config\TempDir")?.ManifestJson ?? AppContext.BaseDirectory), "pshome");

    // The PS-runtime support files the out-of-proc server's DEFAULT ISS loads from $PSHOME by name.
    // PowerShell 7 ships the CORE ones (types.ps1xml + the base *.format.ps1xml) as EMBEDDED
    // RESOURCES, not files — so they exist NOWHERE on disk (verified: a real 7.7 install has zero
    // root ps1xml). Normally the server loads them from the embedded resource; but once we ground
    // $PSHOME to a real dir (below), the server switches to file-based loading and demands them
    // there. We seed MINIMAL VALID manifests: the shared lane serializes with ConvertTo-Json and
    // never uses console type/format rendering, so empty manifests are correct, not lossy.
    private static readonly string[] TypeManifests =
        { "types.ps1xml", "typesv3.ps1xml", "getevent.types.ps1xml" };
    private static readonly string[] FormatManifests =
    {
        "Certificate.format.ps1xml", "Diagnostics.format.ps1xml", "DotNetTypes.format.ps1xml",
        "Event.format.ps1xml", "FileSystem.format.ps1xml", "Help.format.ps1xml",
        "HelpV3.format.ps1xml", "PowerShellCore.format.ps1xml", "PowerShellTrace.format.ps1xml",
        "Registry.format.ps1xml", "WSMan.format.ps1xml",
    };

    // Ground the application base (surge-protector doctrine). SMA's out-of-proc PSRP SERVER path
    // (the side our loopback client connects to) builds its server runspace via
    // Utils.GetApplicationBase → AppContext.BaseDirectory, which is null/empty in a .NET-Android
    // single-file APK (assemblies embedded, no PSHOME on disk). Empty base → "applicationBase is
    // null"; grounding it to a dir without the ps1xml → "Could not find file …/types.ps1xml". So we
    // both POINT the base at a real dir AND seed the manifests it will look for there. The in-proc
    // runspaces (terminal, API pool) use a hand-built empty ISS and never hit this — only PSRP does.
    private static void EnsureAppBase()
    {
        if (_appBaseGrounded) return;
        try
        {
            var baseDir = PsHome;
            Directory.CreateDirectory(baseDir);

            const string typesStub  = "<?xml version=\"1.0\" encoding=\"utf-8\" ?>\n<Types>\n</Types>\n";
            const string formatStub = "<?xml version=\"1.0\" encoding=\"utf-8\" ?>\n<Configuration>\n</Configuration>\n";
            foreach (var f in TypeManifests)
            {
                var p = Path.Combine(baseDir, f);
                if (!File.Exists(p)) File.WriteAllText(p, typesStub);
            }
            foreach (var f in FormatManifests)
            {
                var p = Path.Combine(baseDir, f);
                if (!File.Exists(p)) File.WriteAllText(p, formatStub);
            }

            var withSep = baseDir.EndsWith(Path.DirectorySeparatorChar) ? baseDir : baseDir + Path.DirectorySeparatorChar;
            AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", withSep);
            Environment.SetEnvironmentVariable("PSModulePath", Path.Combine(baseDir, "Modules"));
            _appBaseGrounded = true;
            Dg.Log("rs", $"PSHOME grounded to {baseDir} ({TypeManifests.Length + FormatManifests.Length} ps1xml stubs seeded)");
        }
        catch (Exception ex) { Dg.Log("rs", "EnsureAppBase: " + ex.Message); }
    }

    // Idempotent: SMA keeps one custom-pipe server per name and silently no-ops while it is alive;
    // after a client disconnects it re-listens via its ListenerEnded handler. Calling this again
    // inside the connect-retry loop covers the relisten race window.
    public static void EnsureEndpoint()
    {
        EnsureAppBase();
        EnsureTempDir();
        System.Management.Automation.Remoting.RemoteSessionNamedPipeServer
            .CreateCustomNamedPipeServer(PipeName);
    }

    public static PsrpSession Open(string ownerTag)
    {
        if (!Granted())
            throw new RsException("not-granted",
                $"PowerShell remoting is not granted ({CapabilityPath} is disabled).");
        if (string.IsNullOrWhiteSpace(ownerTag)) ownerTag = "presenter";

        lock (_connectGate)
        {
            ReapIdle();

            // Owner-reclaim: a fresh open by the same owner supersedes its stale predecessor
            // (the presenter-reload case), and an exclusive open evicts the SHARED lane (stateless by
            // contract — it reconnects lazily). A live session held by ANOTHER owner is a typed
            // refusal — the pipe serves one connection at a time (see the KNOWN BOUND note above).
            foreach (var stale in _sessions.Values.Where(s =>
                         s.Id == SharedId ||
                         s.OwnerTag.Equals(ownerTag, StringComparison.OrdinalIgnoreCase)).ToArray())
                Close(stale.Id);
            var holder = _sessions.Values.FirstOrDefault();
            if (holder != null)
                throw new RsException("busy",
                    $"The PSRP pipe is held by session '{holder.Id}' (owner '{holder.OwnerTag}').");

            var runspace = Connect();
            Bootstrap(runspace);

            var session = new PsrpSession(Guid.NewGuid().ToString("N")[..8], ownerTag, runspace);
            _sessions[session.Id] = session;
            Dg.Log("rs", $"SESSION + {session.Id} (owner '{ownerTag}') over {PipeName}");
            return session;
        }
    }

    // Runs the structured command chain in the session's remote runspace and returns the remote
    // ConvertTo-Json output (always a JSON array — -AsArray). Serialized per session (a runspace is
    // single-threaded; that's correct shell semantics, mirroring ManagedSession.Invoke).
    public static string Invoke(string id, JsonElement commands, int depth)
    {
        var session = string.IsNullOrEmpty(id) || id.Equals(SharedId, StringComparison.OrdinalIgnoreCase)
            ? GetOrCreateShared()
            : Get(id) ?? throw new RsException("no-session", $"No PSRP session '{id}'.");
        if (commands.ValueKind != JsonValueKind.Array || commands.GetArrayLength() == 0)
            throw new RsException("bad-request", "Body must carry a non-empty 'commands' array.");
        if (depth is < 1 or > 16) depth = 4;

        lock (session.Gate)
        {
            session.Busy = true;
            session.LastUsed = DateTime.Now;
            try
            {
                using var ps = PowerShell.Create();
                ps.Runspace = session.Runspace;
                session.Current = ps;

                foreach (var c in commands.EnumerateArray())
                {
                    if (!c.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String)
                        throw new RsException("bad-request", "Each command needs a string 'name'.");
                    ps.AddCommand(n.GetString()!);   // a command NAME — pipes/semicolons can't ride along
                    if (c.TryGetProperty("parameters", out var pars) && pars.ValueKind == JsonValueKind.Object)
                        foreach (var p in pars.EnumerateObject())
                            ps.AddParameter(p.Name, ToClr(p.Value));
                }
                ps.AddCommand("ConvertTo-Json")
                  .AddParameter("Depth", depth)
                  .AddParameter("Compress", true)
                  .AddParameter("AsArray", true);

                var results = ps.Invoke();

                if (ps.HadErrors)
                {
                    var sb = new StringBuilder();
                    foreach (var e in ps.Streams.Error) sb.AppendLine(e.ToString());
                    throw new RsException("command-error", sb.ToString().TrimEnd());
                }

                if (results.Count == 0) return "[]";
                var json = new StringBuilder();
                foreach (var r in results) json.Append(r?.ToString());
                return json.Length == 0 ? "[]" : json.ToString();
            }
            finally
            {
                session.Current = null;
                session.Busy = false;
                session.LastUsed = DateTime.Now;
            }
        }
    }

    // The shared lane materializes on first use and self-heals: a dead runspace (backend hiccup,
    // pipe drop) is closed and reconnected instead of erroring forever. Refused while an exclusive
    // session holds the pipe — the caller sees the typed "busy" and surfaces it.
    private static PsrpSession GetOrCreateShared()
    {
        if (!Granted())
            throw new RsException("not-granted",
                $"PowerShell remoting is not granted ({CapabilityPath} is disabled).");

        lock (_connectGate)
        {
            if (_sessions.TryGetValue(SharedId, out var existing))
            {
                if (existing.Runspace.RunspaceStateInfo.State == RunspaceState.Opened) return existing;
                Close(SharedId);   // dead transport — rebuild below
            }
            var holder = _sessions.Values.FirstOrDefault();
            if (holder != null)
                throw new RsException("busy",
                    $"The PSRP pipe is held by exclusive session '{holder.Id}' (owner '{holder.OwnerTag}').");

            var runspace = Connect();
            Bootstrap(runspace);
            var session = new PsrpSession(SharedId, SharedId, runspace);
            _sessions[SharedId] = session;
            Dg.Log("rs", $"SESSION + {SharedId} (multiplexed lane) over {PipeName}");
            return session;
        }
    }

    // Script execution — the TERMINAL lane. Only an EXCLUSIVE session may run raw script: the
    // shared lane stays structured-only (parameters cross as data), but an exclusive session
    // belongs to the human typing at it — the script IS their intent, not a seam crossing.
    // Returns combined output + error text (Out-String semantics, mirroring ManagedSession.Invoke).
    public static string InvokeScript(string id, string script)
    {
        if (string.IsNullOrEmpty(id) || id.Equals(SharedId, StringComparison.OrdinalIgnoreCase))
            throw new RsException("structured-only",
                "The shared lane does not run script. Open an exclusive session for a REPL.");
        var session = Get(id) ?? throw new RsException("no-session", $"No PSRP session '{id}'.");

        lock (session.Gate)
        {
            session.Busy = true;
            session.LastUsed = DateTime.Now;
            try
            {
                using var ps = PowerShell.Create();
                ps.Runspace = session.Runspace;
                session.Current = ps;
                ps.AddScript(script).AddCommand("Out-String");
                var results = ps.Invoke();

                var sb = new StringBuilder();
                foreach (var r in results) if (r != null) sb.Append(r.ToString());
                foreach (var e in ps.Streams.Error) sb.AppendLine("ERROR: " + e);
                return sb.ToString().TrimEnd('\r', '\n');
            }
            finally
            {
                session.Current = null;
                session.Busy = false;
                session.LastUsed = DateTime.Now;
            }
        }
    }

    public static bool Close(string id)
    {
        if (!_sessions.TryRemove(id, out var s)) return false;
        Terminate(s.VomOwner);   // kernel termination token: cancel + DropPrefix(\Sessions\Psrp\{id})
        s.Dispose();             // Stop in-flight pipeline, close the remote runspace, drop the pipe
        Dg.Log("rs", $"SESSION - {id} (owner '{s.OwnerTag}')");
        return true;
    }

    // Lazy reaper — runs on Open (no standing timer thread to babysit). At most one session exists,
    // so "on the next open" is exactly when a stale holder matters.
    private static void ReapIdle()
    {
        foreach (var s in _sessions.Values.Where(s => !s.Busy && DateTime.Now - s.LastUsed > IdleTtl).ToArray())
        {
            Dg.Log("rs", $"SESSION ~ {s.Id} idle > {IdleTtl.TotalMinutes:0}m → reaped");
            Close(s.Id);
        }
    }

    // PSRP loopback connect. After a previous session drops, SMA re-creates the listener from its
    // ListenerEnded callback — a short race window the retry loop absorbs.
    private static Runspace Connect()
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 12; attempt++)
        {
            EnsureEndpoint();
            try
            {
                var ci = new NamedPipeConnectionInfo(PipeName, 10_000);   // (customPipeName, openTimeout ms)
                var rs = RunspaceFactory.CreateRunspace(ci);
                rs.Open();
                return rs;
            }
            catch (Exception ex)
            {
                last = ex;
                System.Threading.Thread.Sleep(250);
            }
        }
        throw new RsException("connect-failed",
            $"Could not open a PSRP runspace over '{PipeName}': {last?.Message}");
    }

    // Hydrate the fresh server session from the shipped bootstrap asset (DATA, not a C# string —
    // SS001; same home as shell-functions.ps1). Degrades: an unhydrated session still has the
    // engine surface; the failure is recorded, not fatal.
    private static void Bootstrap(Runspace runspace)
    {
        try
        {
            _bootstrap ??= ObpHost.ReadAllText(BootstrapAsset)
                ?? throw new FileNotFoundException(BootstrapAsset);
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;
            ps.AddScript(_bootstrap);
            ps.Invoke();
            if (ps.HadErrors)
                Dg.Log("rs", "bootstrap errors: " + string.Join("; ", ps.Streams.Error.Select(e => e.ToString())));
        }
        catch (Exception ex) { Dg.Log("rs", "bootstrap failed: " + ex.Message); }
    }

    // JSON → CLR for AddParameter: parameters stay typed DATA end to end.
    private static object? ToClr(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.True   => true,
        JsonValueKind.False  => false,
        JsonValueKind.Number => v.TryGetInt64(out var l) ? l : v.GetDouble(),
        JsonValueKind.Array  => v.EnumerateArray().Select(ToClr).ToArray(),
        JsonValueKind.Object => ToHashtable(v),
        _                    => null,
    };

    private static System.Collections.Hashtable ToHashtable(JsonElement obj)
    {
        var h = new System.Collections.Hashtable();
        foreach (var p in obj.EnumerateObject()) h[p.Name] = ToClr(p.Value);
        return h;
    }
}

// Typed refusals for the /psrp seam: the route maps Code into the graceful error envelope
// (never a 404/500), and "no-session" tells the presenter client to reopen + retry once.
public sealed class RsException : Exception
{
    public string Code { get; }
    public RsException(string code, string message) : base(message) => Code = code;
}
