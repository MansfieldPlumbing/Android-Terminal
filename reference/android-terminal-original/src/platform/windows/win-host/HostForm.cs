using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using ConPtyComfy;

namespace WinHost;

public sealed class HostForm : Form
{
    [DllImport("kernel32.dll")] static extern bool FreeConsole();

    private readonly WebView2    _web    = new();
    private PtySession?          _pty;
    private readonly VtParser    _vt     = new(220, 50);
    private static readonly string _log = Path.Combine(
        AppContext.BaseDirectory, "win-host.log");

    private static void Log(string line)
    {
        var msg = $"[{DateTime.Now:HH:mm:ss.fff}] {line}";
        Console.WriteLine(msg);
        try { File.AppendAllText(_log, msg + "\n"); } catch { }
    }

    public HostForm()
    {
        Text        = "Terminal";
        WindowState = FormWindowState.Maximized;
        BackColor   = System.Drawing.Color.Black;

        _web.Dock = DockStyle.Fill;
        Controls.Add(_web);

        Load += async (_, _) =>
        {
            try   { await InitAsync(); }
            catch (Exception ex) { MessageBox.Show(ex.ToString(), "Init failed", MessageBoxButtons.OK, MessageBoxIcon.Error); Close(); }
        };
        FormClosed += (_, _) => _pty?.Dispose();
    }

    // ── WebView2 init ────────────────────────────────────────────────────────

    private async Task InitAsync()
    {
        var opts = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = "--enable-unsafe-webgpu --enable-features=Vulkan,UseSkiaRenderer"
        };
        var env = await CoreWebView2Environment.CreateAsync(null, null, opts);
        await _web.EnsureCoreWebView2Async(env);

        // Map virtual hostname → applets folder so applets load without file:// CORS issues
        var appletsPath = ResolveApplets();
        _web.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "terminal.local", appletsPath,
            CoreWebView2HostResourceAccessKind.Allow);

        // Intercept JS console.* → postMessage → stdout (NO JSON.stringify — WebView2 encodes objects itself)
        await _web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
(function() {
  const _send = msg => window.chrome.webview.postMessage({ type: '__log', data: msg });
  ['log','warn','error','info'].forEach(k => {
    const orig = console[k].bind(console);
    console[k] = (...a) => { orig(...a); _send('[' + k.toUpperCase() + '] ' + a.map(x => typeof x === 'object' ? JSON.stringify(x) : String(x)).join(' ')); };
  });
  window.addEventListener('error', e => _send('[JSERR] ' + e.message + ' @ ' + e.filename + ':' + e.lineno));
  window.addEventListener('unhandledrejection', e => _send('[REJECT] ' + (e.reason?.stack || e.reason)));
})();
");
        _web.CoreWebView2.WebMessageReceived += OnMessage;
        _web.CoreWebView2.Settings.AreDevToolsEnabled = true;
        _web.CoreWebView2.NavigationCompleted += async (_, e) =>
        {
            if (!e.IsSuccess)
            {
                Log($"[NAV ERROR] {e.WebErrorStatus}");
                MessageBox.Show($"Navigation failed: {e.WebErrorStatus}", "Nav error");
            }
            else
            {
                Log("[NAV] page loaded — opening DevTools");
                await Task.Delay(800);
                BeginInvoke(() => _web.CoreWebView2?.OpenDevToolsWindow());
            }
        };

        Text = $"Terminal — {appletsPath}";
        _web.Source = new Uri("https://terminal.local/terminal-shader.html");
    }

    private static string ResolveApplets()
    {
        // Running via `dotnet run` from win-host/ → BaseDirectory is bin/Debug/net11.0.../
        // Path: src/platform/windows/win-host/bin/Debug/net11.0.../ → up 6 → repo root → src/terminal/public/applets
        var fromBase = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../../../src/terminal/public/applets"));
        if (Directory.Exists(fromBase)) return fromBase;

        // Running from project dir directly (CWD = src/platform/windows/win-host/)
        // Up 3 lands in src/, then react/public/applets
        var fromCwd = Path.GetFullPath("../../../react/public/applets");
        if (Directory.Exists(fromCwd)) return fromCwd;

        throw new DirectoryNotFoundException(
            $"Cannot find applets folder. Tried:\n  {fromBase}\n  {fromCwd}");
    }

    // ── JS → C# messages ────────────────────────────────────────────────────

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc  = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            switch (type)
            {
                case "__log":
                    Log("[JS] " + root.GetProperty("data").GetString());
                    return;

                case "devtools":
                    BeginInvoke(() => _web.CoreWebView2?.OpenDevToolsWindow());
                    return;

                case "ready":
                    StartPty();
                    break;

                case "input":
                    if (_pty is { } pty)
                    {
                        var bytes = Encoding.UTF8.GetBytes(root.GetProperty("data").GetString() ?? "");
                        if (bytes.Length > 0) { pty.InputStream.Write(bytes); pty.InputStream.Flush(); }
                    }
                    break;

                case "resize":
                    var rcols = (short)root.GetProperty("cols").GetInt32();
                    var rrows = (short)root.GetProperty("rows").GetInt32();
                    _pty?.Resize(rcols, rrows);
                    _vt.Resize(rcols, rrows);
                    break;
            }
        }
        catch (Exception ex) { MessageBox.Show(ex.ToString(), "OnMessage error"); }
    }

    // ── PTY ─────────────────────────────────────────────────────────────────

    private void StartPty()
    {
        if (_pty != null) return;

        // Detach from dotnet run's inherited console handles before creating the PTY.
        // Without this, pwsh crashes with 0xC0000142 (dual-console PEB contamination).
        FreeConsole();
        Log("StartPty: launching pwsh");
        _pty = PtySession.Start("pwsh -NoLogo -NoProfile", 220, 50);
        Log($"StartPty: PTY pid {_pty.ProcessId}");

        // Pre-emptive CPR: beat the DSR handshake window before the pump thread starts.
        // pwsh sends \x1b[6n during init and blocks waiting for \x1b[<r>;<c>R.
        // Writing the response here (before any read loop) covers the earliest possible window.
        byte[] cprEarly = Encoding.UTF8.GetBytes("\x1b[1;1R");
        try { _pty.InputStream.Write(cprEarly); _pty.InputStream.Flush(); Log("[StartPty] pre-emptive CPR sent"); }
        catch (Exception ex) { Log($"[StartPty] pre-emptive CPR failed: {ex.Message}"); }
        Text = $"Terminal — PTY pid {_pty.ProcessId}";

        // Liveness check — log pwsh state after 2s
        var snapPty = _pty;
        _ = Task.Delay(2000).ContinueWith(_ =>
            Log($"[diag] 2s: HasExited={snapPty.HasExited} ExitCode=0x{snapPty.ExitCode:X}"));

        // Close PTY when shell exits — flushes conhost buffer, sends EOF to pump
        _ = _pty.WaitForExitAsync().ContinueWith(t => {
            Log($"[exit] pwsh exited code=0x{t.Result:X}");
            _pty?.ClosePty();
        });

        // Pump PTY output → PostWebMessageAsString on UI thread
        new Thread(() =>
        {
            var buf  = new byte[4096];
            var pty  = _pty!;

            // DSR = Device Status Report: pwsh sends \x1b[6n during init and blocks
            // waiting for a CPR (Cursor Position Report) \x1b[<row>;<col>R before it
            // will finish loading. Without this response it times out → 0xC0000142.
            byte[] dsr = { 0x1B, 0x5B, 0x36, 0x6E };           // ESC [ 6 n
            byte[] cpr = Encoding.UTF8.GetBytes("\x1b[1;1R");   // ESC [ 1 ; 1 R

            while (true)
            {
                int n;
                try   { n = pty.OutputStream.Read(buf, 0, buf.Length); }
                catch { break; }
                if (n == 0) break;

                // Intercept DSR — reply with CPR before forwarding output to VtParser.
                // This unblocks pwsh's console-init handshake.
                if (IndexOf(buf, n, dsr) >= 0)
                {
                    Log("[pump] DSR \\x1b[6n detected — sending CPR \\x1b[1;1R");
                    try { pty.InputStream.Write(cpr); pty.InputStream.Flush(); }
                    catch (Exception ex) { Log($"[pump] CPR write failed: {ex.Message}"); }
                }

                Log($"[pump] {n} bytes → VtParser");
                _vt.Write(buf.AsSpan(0, n));
                PostGrid();
            }

            _vt.Write("\r\n[shell exited]\r\n");
            PostGrid();
        }) { IsBackground = true }.Start();
    }

    // Scan the first `len` bytes of `buf` for `pattern`. Returns index or -1.
    private static int IndexOf(byte[] buf, int len, byte[] pattern)
    {
        int limit = len - pattern.Length;
        for (int i = 0; i <= limit; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
                if (buf[i + j] != pattern[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private void PostGrid()
    {
        var b64 = Convert.ToBase64String(MemoryMarshal.AsBytes(_vt.Grid));
        Post(new {
            type = "grid",
            cols = _vt.Cols, rows = _vt.Rows,
            cc   = _vt.CursorCol, cr = _vt.CursorRow,
            cv   = _vt.CursorVisible,
            d    = b64,
        });
    }

    private void Post(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        if (IsHandleCreated)
            BeginInvoke(() => _web.CoreWebView2?.PostWebMessageAsString(json));
    }
}
