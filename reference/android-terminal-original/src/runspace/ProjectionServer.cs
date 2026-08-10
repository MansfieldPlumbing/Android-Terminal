using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Android.App;

namespace TerminalApp;

public class ProjectionServer
{
    private HttpListener? _listener;
    private readonly MainActivity _mainActivity;
    private readonly ConcurrentDictionary<WebSocket, ClientState> _clients = new();

    private sealed class ClientState
    {
        public WebSocket Ws = null!;
        public Channel<(long TabId, byte[] Data)> Channel = null!;
        public CancellationTokenSource Cts = null!;
    }

    public ProjectionServer(MainActivity mainActivity)
    {
        _mainActivity = mainActivity;
    }

    public void Start(int port = 8080)
    {
        try {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://*:{port}/");
            _listener.Start();
            Task.Run(ListenLoop);
        } catch (Exception ex) {
            System.Diagnostics.Debug.WriteLine("ProjectionServer Error: " + ex.Message);
        }
    }

    private async Task ListenLoop()
    {
        if (_listener == null) return;

        while (true)
        {
            try {
                var context = await _listener.GetContextAsync();
                if (context.Request.IsWebSocketRequest)
                    _ = ProcessWebSocket(context);
                else
                    ServeStaticFile(context);
            } catch { }
        }
    }

    private async Task ProcessWebSocket(HttpListenerContext context)
    {
        WebSocket? ws = null;
        ClientState? state = null;
        try {
            var wsContext = await context.AcceptWebSocketAsync(null);
            ws = wsContext.WebSocket;

            var channel = Channel.CreateBounded<(long, byte[])>(new BoundedChannelOptions(512) {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
            state = new ClientState {
                Ws = ws,
                Channel = channel,
                Cts = new CancellationTokenSource(),
            };
            _clients[ws] = state;
            _ = Task.Run(() => SendPump(state));

            var buffer = new byte[8192];
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) break;

                var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                _mainActivity.RouteInputEvent(msg);
            }
        }
        catch { }
        finally {
            if (ws != null && state != null) {
                _clients.TryRemove(ws, out _);
                state.Channel.Writer.TryComplete();
                state.Cts.Cancel();
                try { await Task.Delay(50); } catch { }
                try { ws.Dispose(); } catch { }
                state.Cts.Dispose();
            }
        }
    }

    // Drains the per-client send channel, coalescing consecutive same-tab payloads
    // into a single WebSocket frame. Wire format: "{tabId}:{concatenated bytes}".
    // The JS handler only parses the first ':' for tabId — coalescing must preserve
    // that contract by NOT re-prefixing appended items.
    private async Task SendPump(ClientState state)
    {
        var ws = state.Ws;
        var reader = state.Channel.Reader;
        var token = state.Cts.Token;
        var buf = new MemoryStream(4096);
        (long TabId, byte[] Data)? carried = null;

        try {
            while (!token.IsCancellationRequested)
            {
                (long TabId, byte[] Data) first;
                if (carried.HasValue) {
                    first = carried.Value;
                    carried = null;
                } else {
                    bool more;
                    try { more = await reader.WaitToReadAsync(token).ConfigureAwait(false); }
                    catch { break; }
                    if (!more) break;
                    if (!reader.TryRead(out first)) continue;
                }

                if (ws.State != WebSocketState.Open) break;

                buf.SetLength(0);
                var prefix = Encoding.UTF8.GetBytes($"{first.TabId}:");
                buf.Write(prefix, 0, prefix.Length);
                buf.Write(first.Data, 0, first.Data.Length);

                long currentTab = first.TabId;
                while (reader.TryRead(out var next))
                {
                    if (next.TabId != currentTab) { carried = next; break; }
                    buf.Write(next.Data, 0, next.Data.Length);
                    if (buf.Length > 32 * 1024) break;
                }

                try {
                    var seg = new ArraySegment<byte>(buf.GetBuffer(), 0, (int)buf.Length);
                    await ws.SendAsync(seg, WebSocketMessageType.Text, true, token).ConfigureAwait(false);
                } catch { break; }
            }
        } catch { }
    }

    public void Broadcast(long tabId, byte[] rawAnsiBytes)
    {
        if (_clients.IsEmpty) return;
        foreach (var kv in _clients)
        {
            kv.Value.Channel.Writer.TryWrite((tabId, rawAnsiBytes));
        }
    }

    private void ServeStaticFile(HttpListenerContext context)
    {
        var req = context.Request;
        var res = context.Response;
        var path = req.Url?.AbsolutePath ?? "/";
        if (path == "/") path = "/index.html";

        try
        {
            using var stream = _mainActivity.Assets!.Open("wwwroot" + path);
            if (path.EndsWith(".html"))  res.ContentType = "text/html";
            else if (path.EndsWith(".js"))   res.ContentType = "application/javascript";
            else if (path.EndsWith(".css"))  res.ContentType = "text/css";
            else if (path.EndsWith(".json")) res.ContentType = "application/json";
            else if (path.EndsWith(".svg"))  res.ContentType = "image/svg+xml";

            stream.CopyTo(res.OutputStream);
        }
        catch
        {
            res.StatusCode = 404;
        }
        finally
        {
            res.Close();
        }
    }
}
