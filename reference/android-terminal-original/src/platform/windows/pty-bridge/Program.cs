// pty-bridge — ConPTY HTTP/SSE bridge for xterm-webgpu.html
// GET  /        → xterm-webgpu.html
// GET  /output  → SSE stream of raw PTY output (JSON-encoded strings)
// POST /input   → raw bytes → PTY stdin
// POST /resize  → { cols, rows } → ResizePseudoConsole

using System.Net;
using System.Text;
using System.Text.Json;
using ConPtyComfy;

const int   PORT = 7681;
const short COLS = 220;
const short ROWS = 50;

// xterm-webgpu.html lives at src/terminal/public/applets/ relative to repo root.
// Path from BaseDirectory (src/platform/windows/pty-bridge/bin/Debug/net11.0.../): up 6 → repo root
var htmlPath = Path.GetFullPath(
    Path.Combine(AppContext.BaseDirectory, "../../../../../../src/terminal/public/applets/xterm-webgpu.html"));

// Fallback: CWD-relative (CWD = src/platform/windows/pty-bridge/): up 3 → src/
if (!File.Exists(htmlPath))
    htmlPath = Path.GetFullPath("../../../react/public/applets/xterm-webgpu.html");

if (!File.Exists(htmlPath))
{
    Console.Error.WriteLine($"Cannot find xterm-webgpu.html. Tried:\n  {htmlPath}");
    Console.Error.WriteLine("Run from src/platform/windows/pty-bridge/ or repo root.");
    return 1;
}

Console.WriteLine($"Serving: {htmlPath}");

using var session = PtySession.Start("pwsh -NoLogo -NoProfile", COLS, ROWS);
Console.WriteLine($"PTY spawned (pid {session.ProcessId})");

// When the shell exits, close the PTY — flushes conhost's buffer and sends EOF to pump
_ = session.WaitForExitAsync().ContinueWith(t =>
{
    Console.WriteLine($"[exit] shell exited code={session.ExitCode}, closing PTY");
    session.ClosePty();
});

// SSE client registry + replay buffer
var clients    = new HashSet<HttpListenerResponse>();
var clientLock = new object();
var replayBuf  = new List<byte[]>();        // every SSE message ever sent
const int MAX_REPLAY = 512;                 // keep last N messages

static void Broadcast(
    HashSet<HttpListenerResponse> clients, object lck,
    List<byte[]> replay, byte[] msg)
{
    lock (lck)
    {
        replay.Add(msg);
        if (replay.Count > MAX_REPLAY) replay.RemoveAt(0);

        List<HttpListenerResponse>? dead = null;
        foreach (var r in clients)
        {
            try   { r.OutputStream.Write(msg); r.OutputStream.Flush(); }
            catch { dead ??= new(); dead.Add(r); }
        }
        if (dead != null) foreach (var r in dead) clients.Remove(r);
    }
}

// Pump PTY output → all SSE clients (blocking read on dedicated thread)
new Thread(() =>
{
    Console.WriteLine("[pump] started");
    var buf = new byte[4096];
    var stream = session.OutputStream;
    while (true)
    {
        int n;
        try   { n = stream.Read(buf, 0, buf.Length); }
        catch (Exception ex) { Console.Error.WriteLine($"[pump] read error: {ex.Message}"); break; }
        if (n == 0) { Console.WriteLine("[pump] EOF"); break; }
        Console.WriteLine($"[pump] {n} bytes");
        var text = Encoding.UTF8.GetString(buf, 0, n);
        var msg  = Encoding.UTF8.GetBytes($"data: {JsonSerializer.Serialize(text)}\n\n");
        Broadcast(clients, clientLock, replayBuf, msg);
    }
    Console.WriteLine("[pump] done");
    var exit = Encoding.UTF8.GetBytes(
        $"data: {JsonSerializer.Serialize("\r\n[shell exited]\r\n")}\n\n");
    Broadcast(clients, clientLock, replayBuf, exit);
}) { IsBackground = true }.Start();

// HttpListener
var listener = new HttpListener();
listener.Prefixes.Add($"http://127.0.0.1:{PORT}/");
listener.Start();

var url = $"http://localhost:{PORT}";
Console.WriteLine($"\n  ConPTY bridge ready → {url}\n");
System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
    "cmd", $"/c start {url}") { CreateNoWindow = true });

while (true)
{
    HttpListenerContext ctx;
    try   { ctx = await listener.GetContextAsync(); }
    catch { break; }
    _ = HandleAsync(ctx, htmlPath, session, clients, clientLock, replayBuf);
}

return 0;

// ── request handler ──────────────────────────────────────────────────────────

static async Task HandleAsync(
    HttpListenerContext ctx,
    string             htmlPath,
    PtySession         session,
    HashSet<HttpListenerResponse> clients,
    object             clientLock,
    List<byte[]>       replayBuf)
{
    var req = ctx.Request;
    var res = ctx.Response;

    res.AddHeader("Access-Control-Allow-Origin",  "*");
    res.AddHeader("Access-Control-Allow-Methods", "GET,POST,OPTIONS");

    try
    {
        if (req.HttpMethod == "OPTIONS")
        {
            res.StatusCode = 204;
            return;
        }

        var path = req.Url?.AbsolutePath ?? "/";

        // ── GET /output  (SSE) ──────────────────────────────────────────────
        if (req.HttpMethod == "GET" && path == "/output")
        {
            res.StatusCode   = 200;
            res.ContentType  = "text/event-stream";
            res.SendChunked  = true;
            res.AddHeader("Cache-Control", "no-cache");
            res.AddHeader("Connection",    "keep-alive");

            // Flush headers immediately with a comment, then replay + register
            try
            {
                var hello = ": connected\n\n"u8.ToArray();
                res.OutputStream.Write(hello);
                res.OutputStream.Flush();
            }
            catch { return; }

            lock (clientLock)
            {
                foreach (var msg in replayBuf)
                    try { res.OutputStream.Write(msg); } catch { return; }
                try { res.OutputStream.Flush(); } catch { return; }
                clients.Add(res);
            }

            // Hold the connection by draining the request stream and sending
            // periodic heartbeat comments. IOException == client gone.
            try
            {
                while (true)
                {
                    await Task.Delay(10_000);
                    var hb = ": heartbeat\n\n"u8.ToArray();
                    res.OutputStream.Write(hb);
                    res.OutputStream.Flush();
                }
            }
            catch { /* client disconnected */ }
            finally
            {
                lock (clientLock) clients.Remove(res);
            }
            return;
        }

        // ── POST /input ─────────────────────────────────────────────────────
        if (req.HttpMethod == "POST" && path == "/input")
        {
            using var ms = new MemoryStream();
            await req.InputStream.CopyToAsync(ms);
            var bytes = ms.ToArray();
            if (bytes.Length > 0)
            {
                session.InputStream.Write(bytes);
                session.InputStream.Flush();
            }
            res.StatusCode = 204;
            return;
        }

        // ── POST /resize ────────────────────────────────────────────────────
        if (req.HttpMethod == "POST" && path == "/resize")
        {
            using var ms = new MemoryStream();
            await req.InputStream.CopyToAsync(ms);
            try
            {
                var doc  = JsonDocument.Parse(ms.ToArray());
                var cols = (short)doc.RootElement.GetProperty("cols").GetInt32();
                var rows = (short)doc.RootElement.GetProperty("rows").GetInt32();
                session.Resize(cols, rows);
            }
            catch { /* ignore malformed resize */ }
            res.StatusCode = 204;
            return;
        }

        // ── GET /  (serve xterm-webgpu.html) ───────────────────────────────────
        if (req.HttpMethod == "GET" && (path == "/" || path == "/index.html"))
        {
            var html = await File.ReadAllBytesAsync(htmlPath);
            res.StatusCode   = 200;
            res.ContentType  = "text/html; charset=utf-8";
            res.ContentLength64 = html.Length;
            await res.OutputStream.WriteAsync(html);
            return;
        }

        res.StatusCode = 404;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Handler error: {ex.Message}");
        try { res.StatusCode = 500; } catch { }
    }
    finally
    {
        try { res.Close(); } catch { }
    }
}
