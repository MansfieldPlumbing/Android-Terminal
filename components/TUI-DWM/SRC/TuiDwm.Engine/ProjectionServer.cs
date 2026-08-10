using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using TuiDwm.Core;

namespace TuiDwm.Engine;

// Binary WebSocket server — streams terminal windows as raw texture frames.
// Two output paths, both active simultaneously:
//
//   Per-plugin  (texture-of-textures) — one MSG_CELL frame per window per frame.
//   Composite   (simple path)         — one MSG_COMPOSITE of the fully merged frame.
//
// Binary frame layout (all little-endian):
//
//   MSG_COMPOSITE 0x00 | w:u16 | h:u16 | cells:u8[4*w*h]
//   MSG_CELL      0x01 | winIdx:u16 | x:i16 | y:i16 | w:u16 | h:u16 | zSlot:u8 | isAlive:u8 | cells:u8[4*w*h]
//   MSG_SLEEP     0x02 | winIdx:u16
//
// Cell wire encoding — 4 bytes per cell = RGBA8 texel, row-major.
// Reflects LayoutKind.Explicit Cell struct byte layout directly (MemoryMarshal.Cast):
//   [0] Rune low byte  (char & 0xFF — covers full ASCII + Latin-1)
//   [1] Rune high byte (char >> 8  — 0x00 for ASCII; non-zero for BMP Unicode > U+00FF)
//   [2] Fg color index (0–255 xterm palette)
//   [3] Bg color index (0–255 xterm palette)
//
// Style (bold/italic/underline) is NOT on the wire — handled by atlas glyph variants
// on the GPU side. The VtRenderer local console path reads Cell.Style independently.
public sealed class ProjectionServer : IDisposable
{
    private const int Port = 9001;

    private const byte MSG_COMPOSITE = 0x00;
    private const byte MSG_CELL      = 0x01;
    private const byte MSG_SLEEP     = 0x02;
    private const byte MSG_SPATIAL   = 0x03;
    private const byte MSG_ATLAS     = 0x04;

    // Header sizes in bytes
    private const int HDR_COMPOSITE = 5;  // type(1) + w(2) + h(2)
    private const int HDR_CELL      = 13; // type(1) + winIdx(2) + x(2) + y(2) + w(2) + h(2) + zSlot(1) + isAlive(1)
    private const int HDR_SPATIAL   = 1;  // type(1)
    private const int HDR_ATLAS     = 1;  // type(1)

    private readonly HttpListener _http = new();
    private readonly ConcurrentDictionary<int, ClientSlot> _clients = new();
    private readonly Thread _listenThread;
    private volatile bool _running;
    private int _nextClientId;

    private byte[]? _atlasPayload;

    // Encode buffer reused across frames — compositor calls Push* methods serially from one thread
    private byte[] _encodeBuf = new byte[HDR_CELL + 512 * 512 * 4];

    public int ClientCount => _clients.Count;

    public ProjectionServer()
    {
        _http.Prefixes.Add($"http://localhost:{Port}/");
        _listenThread = new Thread(ListenLoop)
        {
            IsBackground = true,
            Name = "ProjSrv-Listen"
        };
    }

    public void Start()
    {
        _running = true;
        _http.Start();
        _listenThread.Start();
    }

    public void Stop()
    {
        _running = false;
        try { _http.Stop(); } catch { }
    }

    public void SetAtlasMetrics(float[] atlas)
    {
        if (atlas == null || atlas.Length == 0) return;
        
        int byteCount = atlas.Length * 4;
        _atlasPayload = new byte[HDR_ATLAS + byteCount];
        _atlasPayload[0] = MSG_ATLAS;
        
        var rawMemory = System.Runtime.InteropServices.MemoryMarshal.Cast<float, byte>(atlas.AsSpan());
        rawMemory.CopyTo(_atlasPayload.AsSpan(1));
    }

    public void BroadcastSpatial(System.Collections.Generic.List<UiElementData> uiData)
    {
        if (_clients.IsEmpty || uiData == null || uiData.Count == 0) return;

        int byteCount = uiData.Count * 64; // 16 floats * 4 bytes each
        int frameSize = HDR_SPATIAL + byteCount;
        EnsureBuf(frameSize);

        var span = _encodeBuf.AsSpan(0, frameSize);
        span[0] = MSG_SPATIAL;

        // Write elements directly without intermediate ToArray()
        int o = 1;
        for (int i = 0; i < uiData.Count; i++)
        {
            var elem = uiData[i];
            var elemBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                new System.ReadOnlySpan<UiElementData>(ref elem));
            elemBytes.CopyTo(span.Slice(o));
            o += 64;
        }

        Broadcast(span.Slice(0, frameSize).ToArray());
    }

    // Broadcast the fully composited frame — simple single-texture path for WebView.
    // Called from compositor thread after VtRenderer, before buffer swap.
    public void BroadcastComposite(CellBuffer buf)
    {
        if (_clients.IsEmpty) return;

        int cellCount = buf.Width * buf.Height;
        int frameSize = HDR_COMPOSITE + cellCount * 4;
        EnsureBuf(frameSize);

        var span = _encodeBuf.AsSpan(0, frameSize);
        int o = 0;
        span[o++] = MSG_COMPOSITE;
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(o, 2), (ushort)buf.Width);  o += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(o, 2), (ushort)buf.Height); o += 2;
        EncodeCells(buf, span, ref o);

        Broadcast(span.ToArray());
    }

    // Stream a single plugin window — texture-of-textures path.
    // Must be called inside p.BufferLock so FrontBuffer is stable.
    public void PushWindow(int winIdx, CellBuffer buf, int x, int y, int zSlot, bool isAlive)
    {
        if (_clients.IsEmpty) return;

        int cellCount = buf.Width * buf.Height;
        int frameSize = HDR_CELL + cellCount * 4;
        EnsureBuf(frameSize);

        var span = _encodeBuf.AsSpan(0, frameSize);
        int o = 0;
        span[o++] = MSG_CELL;
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(o, 2), (ushort)winIdx); o += 2;
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(o, 2), (short)x);        o += 2;
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(o, 2), (short)y);        o += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(o, 2), (ushort)buf.Width);  o += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(o, 2), (ushort)buf.Height); o += 2;
        span[o++] = (byte)Math.Clamp(zSlot, 0, 255);
        span[o++] = isAlive ? (byte)1 : (byte)0;
        EncodeCells(buf, span, ref o);

        Broadcast(span.Slice(0, frameSize).ToArray());
    }

    public void PushSleep(int winIdx)
    {
        if (_clients.IsEmpty) return;

        byte[] frame = new byte[3];
        frame[0] = MSG_SLEEP;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1, 2), (ushort)winIdx);
        Broadcast(frame);
    }

    // Encode Cell[] into 4-bytes-per-cell wire format.
    // Row-major order is preserved — CellBuffer guarantees index = y*Width + x.
    private static void EncodeCells(CellBuffer buf, Span<byte> dst, ref int o)
    {
        var rawMemory = System.Runtime.InteropServices.MemoryMarshal.Cast<Cell, byte>(buf.Cells.AsSpan());
        rawMemory.CopyTo(dst.Slice(o));
        o += rawMemory.Length;
    }

    private void EnsureBuf(int size)
    {
        if (_encodeBuf.Length < size)
            _encodeBuf = new byte[size + 4096];
    }

    // Push-based directport: compositor thread hits each socket directly.
    // Slow client → frame dropped (TryEnter returns false).
    // Dead client → removed on next push.
    private void Broadcast(byte[] frame)
    {
        foreach (var (id, client) in _clients)
        {
            var result = client.TryPush(frame);
            if (result == PushResult.Dead)
                _clients.TryRemove(id, out _);
        }
    }

    private void ListenLoop()
    {
        while (_running)
        {
            HttpListenerContext ctx;
            try { ctx = _http.GetContext(); }
            catch { break; }

            if (!ctx.Request.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                continue;
            }

            int id = Interlocked.Increment(ref _nextClientId);
            new Thread(() => ServeClient(ctx, id))
            {
                IsBackground = true,
                Name = $"ProjSrv-Client-{id}"
            }.Start();
        }
    }

    private void ServeClient(HttpListenerContext ctx, int id)
    {
        WebSocket ws;
        try
        {
            var wsCtx = ctx.AcceptWebSocketAsync(subProtocol: null).GetAwaiter().GetResult();
            ws = wsCtx.WebSocket;
        }
        catch { return; }

        var slot = new ClientSlot(ws);
        _clients[id] = slot;

        if (_atlasPayload != null)
        {
            slot.TryPush(_atlasPayload);
        }

        try   { slot.DrainReceives(); } // blocks until client disconnects
        finally
        {
            _clients.TryRemove(id, out _);
            slot.Close();
        }
    }

    public void Dispose() => Stop();
}

// Push-based directport client slot.
//
// The compositor thread calls TryPush() directly — it acquires the send lock,
// writes to the socket, and releases. No queue. No drain thread. No indirection.
//
// Slow client: Monitor.TryEnter returns false → frame is dropped, compositor continues.
// Dead client: SendAsync throws → TryPush returns Dead → Broadcast removes the slot.
//
// The per-slot background thread (DrainReceives) exists only to detect the WebSocket
// close handshake. It reads and discards incoming bytes until the socket closes, which
// lets the client perform a clean shutdown. It does not participate in sending.
internal sealed class ClientSlot
{
    private readonly WebSocket _ws;
    private readonly object _sendLock = new();

    internal ClientSlot(WebSocket ws) => _ws = ws;

    internal PushResult TryPush(byte[] frame)
    {
        if (_ws.State != WebSocketState.Open) return PushResult.Dead;

        // Non-blocking try: if another send is in flight (shouldn't happen — single
        // compositor thread — but guards against concurrent close), drop the frame.
        if (!Monitor.TryEnter(_sendLock)) return PushResult.Dropped;
        try
        {
            _ws.SendAsync(
                new ArraySegment<byte>(frame),
                WebSocketMessageType.Binary,
                endOfMessage: true,
                cancellationToken: default
            ).GetAwaiter().GetResult();
            return PushResult.Sent;
        }
        catch { return PushResult.Dead; }
        finally { Monitor.Exit(_sendLock); }
    }

    // Blocks on this slot's background thread until the client closes.
    // Discards any incoming data — WebView only sends input events via a
    // separate HTTP POST or future MSG_INPUT frame, not on this socket.
    internal void DrainReceives()
    {
        var buf = new byte[256];
        while (_ws.State == WebSocketState.Open)
        {
            try
            {
                var result = _ws.ReceiveAsync(new ArraySegment<byte>(buf), default)
                                .GetAwaiter().GetResult();
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
            catch { break; }
        }
    }

    internal void Close()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
                _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, default)
                   .GetAwaiter().GetResult();
        }
        catch { }
    }
}

internal enum PushResult { Sent, Dropped, Dead }
