using Android.App;
using Android.OS;
using Android.Views;
using Android.Webkit;
using Android.Window;
using Java.Interop;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Runtime.InteropServices;
using VtNetCore.VirtualTerminal;

using System.Management.Automation.Host;
using System.Management.Automation.Provider;
using VtNetCore.XTermParser;

namespace TerminalApp;

public class ReactInputEvent {
    public string type { get; set; } = "";
    public int cols { get; set; }
    public int rows { get; set; }
    public string key { get; set; } = "";
    public string text { get; set; } = "";
    public long tabId { get; set; }
}

public class TerminalSession : IDisposable {
    public long TabId { get; }
    private readonly MainActivity _main;
    public PowerShell Ps { get; private set; } = null!;
    public AndroidSubsystemHost Host { get; private set; } = null!;
    public VirtualTerminalController VtController { get; private set; }
    public DataConsumer VtConsumer { get; private set; }
    public ReplEngine Repl { get; private set; } = null!;
    public Queue<byte[]> OutputQueue { get; } = new Queue<byte[]>();
    public readonly object VtLock = new object();

    public void Dispose() {
        try {
            Repl?.Stop();
            Ps?.Dispose();
        } catch { }
    }

    public TerminalSession(long tabId, MainActivity main) {
        TabId = tabId;
        _main = main;
        VtController = new VirtualTerminalController();
        VtController.ResizeView(120, 40);
        VtConsumer = new DataConsumer(VtController);
    }

    public void Start(string appBasePath) {
        var iss = InitialSessionState.Create();
        iss.LanguageMode = PSLanguageMode.FullLanguage;
        LoadFromAssembly(iss, typeof(PSObject).Assembly);
        LoadFromAssembly(iss, Assembly.Load("Microsoft.PowerShell.Commands.Utility"));
        LoadFromAssembly(iss, Assembly.Load("Microsoft.PowerShell.Commands.Management"));

        SubsystemAliases.Load(iss);

        Host = new AndroidSubsystemHost(this);
        var rs = RunspaceFactory.CreateRunspace(Host, iss);
        rs.Open();

        Ps = PowerShell.Create();
        Ps.Runspace = rs;

        VirtualObjectManager.Host = _main;
        Ps.AddScript($"$env:HOME = '{appBasePath}'; $env:PHONE_HOME = '/storage/emulated/0'; $Global:VOM = [TerminalApp.VirtualObjectManager]; $Global:ctx = [TerminalApp.VirtualObjectManager]::Host; $env:POWERSHELL_TELEMETRY_OPTOUT = '1'; Set-Location -Path '{appBasePath}'");
        Ps.Invoke();
        Ps.Commands.Clear();

        string profilePath = System.IO.Path.Combine(appBasePath, "profile.ps1");
        Ps.AddScript($"if (Test-Path '{profilePath}') {{ . '{profilePath}' }}");
        Ps.Invoke();
        Ps.Commands.Clear();

        Repl = new ReplEngine(this, Host, rs);
        Repl.Start();
    }

    private void LoadFromAssembly(InitialSessionState iss, Assembly assembly) {
        try {
            foreach (var type in assembly.GetTypes()) {
                var cmdletAttr = type.GetCustomAttribute<CmdletAttribute>();
                if (cmdletAttr != null) iss.Commands.Add(new SessionStateCmdletEntry($"{cmdletAttr.VerbName}-{cmdletAttr.NounName}", type, ""));
                var providerAttr = type.GetCustomAttribute<CmdletProviderAttribute>();
                if (providerAttr != null) iss.Providers.Add(new SessionStateProviderEntry(providerAttr.ProviderName, type, ""));
            }
        } catch { }
    }

    public void FeedTerminal(byte[] rawAnsiBytes) {
        lock (VtLock) {
            VtConsumer.Push(rawAnsiBytes);
            if (VtController.Changed) VtController.ClearChanges();
        }
        _main.SendRawToReact(TabId, rawAnsiBytes);
        _main.BroadcastToProjection(TabId, rawAnsiBytes);
    }

    public void RouteRawInput(string payload) {
        if (string.IsNullOrEmpty(payload)) return;
        try {
            var rawUi = (AndroidSubsystemRawUserInterface)Host.UI.RawUI;
            for (int i = 0; i < payload.Length; i++) {
                char ch = payload[i]; ConsoleKey key = (ConsoleKey)0;
                if ((ch == '\x03' || ch == '\x1b') && Repl != null && Repl.IsRunning) {
                    Repl.StopActiveCommand();
                    continue;
                }
                if (ch == '\x1b' && i + 2 < payload.Length && payload[i + 1] == '[') {
                    switch (payload[i + 2]) {
                        case 'A': key = ConsoleKey.UpArrow;    break;
                        case 'B': key = ConsoleKey.DownArrow;  break;
                        case 'C': key = ConsoleKey.RightArrow; break;
                        case 'D': key = ConsoleKey.LeftArrow;  break;
                    }
                    if (key != (ConsoleKey)0) {
                        rawUi.InputQueue.Add(new KeyInfo((int)key, '\0', (ControlKeyStates)0, true));
                        i += 2; continue;
                    }
                }
                if      (ch == '\r' || ch == '\n') key = ConsoleKey.Enter;
                else if (ch == '\b' || ch == '\x7F') key = ConsoleKey.Backspace;
                else if (ch == '\t')  key = ConsoleKey.Tab;
                else if (ch == '\x1b') key = ConsoleKey.Escape;

                char keyChar = key switch { ConsoleKey.Enter => '\r', ConsoleKey.Backspace => '\b', ConsoleKey.Tab => '\t', ConsoleKey.Escape => '\x1b', _ => ch };
                rawUi.InputQueue.Add(new KeyInfo((int)key, keyChar, (ControlKeyStates)0, true));
            }
        } catch { }
    }

    public void ExecuteCommand(string command) {
        Task.Run(() => {
            try {
                FeedTerminal(Encoding.UTF8.GetBytes($"{command}\r\n"));
                Ps.Commands.Clear();
                Ps.AddScript(command);
                Ps.Invoke();
                if (Ps.HadErrors) foreach (var error in Ps.Streams.Error) FeedTerminal(Encoding.UTF8.GetBytes($"\x1b[31m{error}\x1b[0m\r\n"));
                FeedTerminal(Encoding.UTF8.GetBytes("\x1b[34mPS>\x1b[0m "));
            } catch (Exception ex) { FeedTerminal(Encoding.UTF8.GetBytes($"\x1b[31mFatal Exec Error: {ex.Message}\x1b[0m\r\n")); }
        });
    }

    public Coordinates GetCursorPosition() { lock (VtLock) { return new Coordinates(VtController.CursorState.CurrentColumn, VtController.CursorState.CurrentRow); } }
    public Size GetWindowSize() { lock (VtLock) { return new Size(VtController.VisibleColumns, VtController.VisibleRows); } }
}

[Activity(Label = "Terminal", MainLauncher = true, Theme = "@android:style/Theme.DeviceDefault.NoActionBar", WindowSoftInputMode = Android.Views.SoftInput.AdjustResize, ConfigurationChanges = Android.Content.PM.ConfigChanges.Orientation | Android.Content.PM.ConfigChanges.ScreenSize | Android.Content.PM.ConfigChanges.KeyboardHidden | Android.Content.PM.ConfigChanges.ScreenLayout)]
public class MainActivity : Activity
{
    private WebView _webView = null!;
    public bool IsReactReady { get; private set; } = false;
    private ProjectionServer? _projectionServer;
    public ConcurrentDictionary<long, TerminalSession> Sessions { get; } = new();

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _webView = new WebView(this);
        _webView.Settings.JavaScriptEnabled = true;
        _webView.Settings.DomStorageEnabled = true;
        _webView.Settings.AllowFileAccess = true;
        _webView.Settings.AllowFileAccessFromFileURLs = true;
        _webView.Settings.AllowUniversalAccessFromFileURLs = true;
        _webView.Settings.SetSupportZoom(false);
        _webView.Settings.UseWideViewPort = true;
        _webView.Settings.LoadWithOverviewMode = true;
        _webView.OverScrollMode = OverScrollMode.Never;
        _webView.SetWebViewClient(new WebViewClient());
        _webView.SetWebChromeClient(new WebChromeClient());

        NativeLibrary.SetDllImportResolver(typeof(System.Management.Automation.PowerShell).Assembly, (libraryName, assembly, searchPath) => {
            if (libraryName.Contains("libpsl-native")) return NativeLibrary.Load("libpsl-android.so", assembly, searchPath);
            return IntPtr.Zero;
        });

        _webView.AddJavascriptInterface(new PwshBridge(this), "AndroidBridge");
        _webView.LoadUrl("file:///android_asset/wwwroot/index.html");

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu) this.OnBackInvokedDispatcher.RegisterOnBackInvokedCallback(0, new TerminalBackCallback(this));

        SetContentView(_webView);

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu) RequestPermissions(new[] { Android.Manifest.Permission.PostNotifications }, 0);
        StartForegroundService(new Android.Content.Intent(this, typeof(SubsystemService)));

        SeedAssets();
        System.Environment.SetEnvironmentVariable("POWERSHELL_TELEMETRY_OPTOUT", "1");
    }

    public void CreateSession(long tabId) {
        if (Sessions.ContainsKey(tabId)) return;
        var session = new TerminalSession(tabId, this);
        Sessions[tabId] = session;
        Task.Run(() => session.Start(this.FilesDir!.AbsolutePath));
    }

    public void CloseSession(long tabId) {
        if (Sessions.TryRemove(tabId, out var session)) {
            session.Dispose();
        }
    }

    private void SeedAssets() {
        void SeedAsset(string assetName, string destPath) {
            if (!System.IO.File.Exists(destPath)) {
                try { using var s = this.Assets!.Open(assetName); using var d = System.IO.File.Create(destPath); s.CopyTo(d); } catch { }
            }
        }
        SeedAsset("wwwroot/home/profile.ps1",  System.IO.Path.Combine(this.FilesDir!.AbsolutePath, "profile.ps1"));
        SeedAsset("wwwroot/home/settings.ps1", System.IO.Path.Combine(this.FilesDir!.AbsolutePath, "settings.ps1"));
    }

    public void SendRawToReact(long tabId, byte[] rawAnsiBytes) {
        if (!IsReactReady) {
            if (Sessions.TryGetValue(tabId, out var s)) s.OutputQueue.Enqueue(rawAnsiBytes);
            return;
        }
        string text = Encoding.UTF8.GetString(rawAnsiBytes);
        RunOnUiThread(() => {
            _webView.PostWebMessage(new WebMessage($"{tabId}:{text}"), Android.Net.Uri.Parse("*")!);
        });
    }

    public void RouteInputEvent(string json) {
        try {
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var typeProp)) {
                string type = typeProp.GetString() ?? "";
                if (root.TryGetProperty("tabId", out var tabIdProp)) {
                    long tabId = tabIdProp.GetInt64();
                    if (type == "createSession") {
                        CreateSession(tabId);
                    }
                    else if (type == "input" || type == "resize" || type == "text") {
                        new PwshBridge(this).SendInput(tabId, json);
                    }
                }
            }
        } catch { }
    }

    public void BroadcastToProjection(long tabId, byte[] rawAnsiBytes) { _projectionServer?.Broadcast(tabId, rawAnsiBytes); }

    public void NotifyReactReady() {
        IsReactReady = true;
        foreach (var session in Sessions.Values) {
            while (session.OutputQueue.Count > 0) SendRawToReact(session.TabId, session.OutputQueue.Dequeue());
        }
    }

    public void StartProjection() {
        if (_projectionServer == null) {
            _projectionServer = new ProjectionServer(this);
            _projectionServer.Start(8080);
        }
    }
}

public class PwshBridge : Java.Lang.Object {
    private readonly MainActivity _activity;
    public PwshBridge(MainActivity activity) { _activity = activity; }

    [Export("createSession")] [JavascriptInterface] public void CreateSession(long tabId) { _activity.CreateSession(tabId); }
    [Export("closeSession")]  [JavascriptInterface] public void CloseSession(long tabId)  { _activity.CloseSession(tabId); }
    [Export("invokeCommand")] [JavascriptInterface] public void InvokeCommand(long tabId, string cmd) { if (_activity.Sessions.TryGetValue(tabId, out var s)) s.ExecuteCommand(cmd); }
    [Export("sendRawInput")]  [JavascriptInterface] public void SendRawInput(long tabId, string payload) { if (_activity.Sessions.TryGetValue(tabId, out var s)) s.RouteRawInput(payload); }
    [Export("sendInput")]     [JavascriptInterface] public void SendInput(long tabId, string json) {
        try {
            var ev = JsonSerializer.Deserialize<ReactInputEvent>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (ev?.type == "resize" && _activity.Sessions.TryGetValue(tabId, out var sr)) { lock(sr.VtLock) { sr.VtController.ResizeView(ev.cols, ev.rows); sr.VtController.ClearChanges(); } }
            else if (ev?.type == "text" && !string.IsNullOrEmpty(ev.text) && _activity.Sessions.TryGetValue(tabId, out var st)) {
                st.RouteRawInput(ev.text);
            }
            else if (ev?.type == "input" && !string.IsNullOrEmpty(ev.key) && _activity.Sessions.TryGetValue(tabId, out var si)) {
                ConsoleKey consoleKey = ev.key switch { "Enter" => ConsoleKey.Enter, "Backspace" => ConsoleKey.Backspace, "Escape" => ConsoleKey.Escape, "Tab" => ConsoleKey.Tab, "ArrowUp" => ConsoleKey.UpArrow, "ArrowDown" => ConsoleKey.DownArrow, "ArrowLeft" => ConsoleKey.LeftArrow, "ArrowRight" => ConsoleKey.RightArrow, _ => (ConsoleKey)0 };
                char ch = ev.key.Length == 1 ? ev.key[0] : '\0';
                if (consoleKey == ConsoleKey.Enter) ch = '\r'; else if (consoleKey == ConsoleKey.Backspace) ch = '\b'; else if (consoleKey == ConsoleKey.Escape) ch = '\x1b'; else if (consoleKey == ConsoleKey.Tab) ch = '\t';
                ((AndroidSubsystemRawUserInterface)si.Host.UI.RawUI).InputQueue.Add(new KeyInfo((int)consoleKey, ch, (ControlKeyStates)0, true));
            }
        } catch { }
    }
    [Export("notifyReady")]     [JavascriptInterface] public void NotifyReady()     { _activity.NotifyReactReady(); }
    [Export("minimizeApp")]     [JavascriptInterface] public void MinimizeApp()     { _activity.MoveTaskToBack(true); }
    [Export("startProjection")] [JavascriptInterface] public void StartProjection() { _activity.StartProjection(); }
    [Export("exitApp")]         [JavascriptInterface] public void ExitApp()         { _activity.FinishAffinity(); Java.Lang.JavaSystem.Exit(0); }
    [Export("getScripts")]      [JavascriptInterface] public string GetScripts() {
        try {
            var files = _activity.Assets!.List("wwwroot/scripts");
            return JsonSerializer.Serialize(files ?? Array.Empty<string>());
        } catch { return "[]"; }
    }
}

public class TerminalBackCallback : Java.Lang.Object, IOnBackInvokedCallback {
    private readonly MainActivity _activity;
    public TerminalBackCallback(MainActivity activity) { _activity = activity; }
    public void OnBackInvoked() {
        foreach (var session in _activity.Sessions.Values) session.RouteRawInput("\x1b");
    }
}
