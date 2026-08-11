using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Terminal.Router;

internal sealed class RouterHost
{
    private const int OutboundCapacity = 256;
    private readonly EndpointTransport _endpoint;
    private readonly Dictionary<ulong, RouterRoute> _routes = new();
    private ulong _nextRouteId = 1001;
    private ulong _generation;
    private Channel<Outgoing>? _outbound;
    private Thread? _writer;
    private Exception? _writerFailure;

    internal RouterHost(EndpointTransport endpoint) => _endpoint = endpoint;

    internal async Task<int> RunAsync()
    {
        _endpoint.WriteFrame(WireKind.Ready, 0, 0, Array.Empty<byte>());
        WireFrame admission = _endpoint.ReadFrame();
        if (admission.Kind != WireKind.Request || admission.Correlation == 0 ||
            admission.Generation == 0 || admission.Payload.Length != 0)
            throw new InvalidOperationException("Router admission frame was invalid.");
        _generation = admission.Generation;
        _endpoint.WriteFrame(WireKind.Completion, admission.Correlation, _generation, Array.Empty<byte>());

        _outbound = Channel.CreateBounded<Outgoing>(new BoundedChannelOptions(OutboundCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        _writer = new Thread(() => WriteOutbound(_outbound.Reader))
        {
            IsBackground = false,
            Name = "Terminal router endpoint writer",
        };
        _writer.Start();

        for (;;)
        {
            WireFrame frame = _endpoint.ReadFrame();
            if (frame.Generation != _generation) throw new InvalidOperationException("Wrong worker generation.");
            if (frame.Kind == WireKind.Quiesce)
            {
                await ReachShutdownPointAsync().ConfigureAwait(false);
                _endpoint.WriteFrame(WireKind.QuiesceAcknowledgement, frame.Correlation, _generation, Array.Empty<byte>());
                return 0;
            }
            if (frame.Kind != WireKind.Data) throw new InvalidOperationException("Unexpected worker frame kind.");
            RouterMessage message = EndpointTransport.DecodeRouterMessage(frame.Payload);
            await DispatchAsync(frame.Correlation, message).ConfigureAwait(false);
        }
    }

    private async Task DispatchAsync(ulong correlation, RouterMessage message)
    {
        switch (message.Operation)
        {
            case RouterOperation.Open:
                await OpenAsync(correlation, message).ConfigureAwait(false);
                break;
            case RouterOperation.Input:
                RequireRoute(message.RouteId).Session.Write(message.Payload);
                break;
            case RouterOperation.Resize:
                RequireRoute(message.RouteId).Session.Resize(
                    checked((ushort)message.ValueA), checked((ushort)message.ValueB));
                await QueueAsync(correlation, new RouterMessage(
                    RouterOperation.Resized, message.RouteId, message.ValueA, message.ValueB, Array.Empty<byte>())).ConfigureAwait(false);
                break;
            case RouterOperation.Close:
                await CloseAsync(correlation, message.RouteId).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException("Host sent a response-only router operation.");
        }
    }

    private async Task OpenAsync(ulong correlation, RouterMessage message)
    {
        if (message.RouteId != 0 || message.ValueA == 0 || message.ValueB == 0 || message.Payload.Length == 0)
            throw new InvalidOperationException("Invalid OPEN message.");
        string command = Encoding.UTF8.GetString(message.Payload);
        IPtySession session = PtySessionFactory.Start(
            command,
            checked((ushort)message.ValueA),
            checked((ushort)message.ValueB));
        ulong routeId = _nextRouteId++;
        var route = new RouterRoute(routeId, session, QueueOutputAsync);
        if (!_routes.TryAdd(routeId, route))
        {
            session.Dispose();
            throw new InvalidOperationException("Route identity collision.");
        }
        await QueueAsync(correlation, new RouterMessage(
            RouterOperation.Opened, routeId, session.ProcessId, 0, Array.Empty<byte>())).ConfigureAwait(false);
        route.StartPump();
    }

    private async Task CloseAsync(ulong correlation, ulong routeId)
    {
        if (!_routes.Remove(routeId, out RouterRoute? route)) throw new InvalidOperationException("Unknown route.");
        uint exitCode = await route.CloseAsync().ConfigureAwait(false);
        await QueueAsync(correlation, new RouterMessage(
            RouterOperation.Closed, routeId, exitCode, 0, Array.Empty<byte>())).ConfigureAwait(false);
    }

    private RouterRoute RequireRoute(ulong routeId)
    {
        if (!_routes.TryGetValue(routeId, out RouterRoute? route))
            throw new InvalidOperationException("Unknown route.");
        return route;
    }

    private ValueTask QueueOutputAsync(ulong routeId, byte[] payload) =>
        QueueAsync(0, new RouterMessage(RouterOperation.Output, routeId, 0, 0, payload));

    private ValueTask QueueAsync(ulong correlation, RouterMessage message)
    {
        if (_outbound is null) throw new InvalidOperationException("Outbound queue is unavailable.");
        return _outbound.Writer.WriteAsync(new Outgoing(correlation, message));
    }

    private void WriteOutbound(ChannelReader<Outgoing> reader)
    {
        try
        {
            while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (reader.TryRead(out Outgoing outgoing))
                {
                    byte[] payload = EndpointTransport.EncodeRouterMessage(outgoing.Message);
                    _endpoint.WriteFrameFromProducer(WireKind.Data, outgoing.Correlation, _generation, payload);
                }
            }
        }
        catch (Exception ex)
        {
            _writerFailure = ex;
            _outbound?.Writer.TryComplete(ex);
        }
    }

    private async Task ReachShutdownPointAsync()
    {
        foreach (RouterRoute route in _routes.Values)
            await route.CloseAsync().ConfigureAwait(false);
        _routes.Clear();
        if (_outbound is null || _writer is null) throw new InvalidOperationException("Writer was not initialized.");
        _outbound.Writer.TryComplete();
        if (!_writer.Join(TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException("Endpoint writer did not terminate.");
        if (_writerFailure is not null)
            throw new InvalidOperationException("Endpoint writer failed.", _writerFailure);
    }

    private readonly record struct Outgoing(ulong Correlation, RouterMessage Message);
}

internal sealed class RouterRoute
{
    private readonly ulong _routeId;
    private readonly Func<ulong, byte[], ValueTask> _emit;
    private Task? _pump;
    private bool _closed;
    internal IPtySession Session { get; }

    internal RouterRoute(ulong routeId, IPtySession session, Func<ulong, byte[], ValueTask> emit)
    {
        _routeId = routeId;
        Session = session;
        _emit = emit;
    }

    internal void StartPump()
    {
        if (_pump is not null) throw new InvalidOperationException("Output pump already started.");
        _pump = Task.Run(PumpAsync);
    }

    private async Task PumpAsync()
    {
        byte[] buffer = new byte[4096];
        for (;;)
        {
            int count;
            try { count = Session.Read(buffer); }
            catch (ObjectDisposedException) { return; }
            catch (InvalidOperationException) when (_closed) { return; }
            if (count <= 0) return;
            byte[] payload = new byte[count];
            Buffer.BlockCopy(buffer, 0, payload, 0, count);
            await _emit(_routeId, payload).ConfigureAwait(false);
        }
    }

    internal async Task<uint> CloseAsync()
    {
        if (_closed) throw new InvalidOperationException("Route already closed.");
        _closed = true;
        uint exitCode = Session.Close();
        if (_pump is not null) await _pump.ConfigureAwait(false);
        Session.Dispose();
        return exitCode;
    }
}
