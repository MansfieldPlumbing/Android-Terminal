using System.Management.Automation;
using System.Threading.Channels;

namespace NativePwshConsole.Surface;

internal sealed class SurfaceEventDispatcher : IDisposable
{
    private readonly Channel<PendingSurfaceEvent> _events = Channel.CreateBounded<PendingSurfaceEvent>(
        new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            // Android callbacks never wait. TryWrite reports saturation so the
            // drop is observable instead of silently discarding an event.
            FullMode = BoundedChannelFullMode.Wait
        });
    private readonly CancellationTokenSource _stop = new();
    private readonly PowerShellSession _session;
    private readonly Task _consumer;

    public SurfaceEventDispatcher(PowerShellSession session)
    {
        _session = session;
        _consumer = Task.Run(ConsumeAsync);
    }

    public bool Enqueue(ScriptBlock? handler, SurfaceEvent surfaceEvent)
    {
        if (handler == null) return true;
        bool accepted = _events.Writer.TryWrite(new PendingSurfaceEvent(handler, surfaceEvent));
        if (!accepted) Android.Util.Log.Warn("Terminal.Surface", $"Dropped saturated event {surfaceEvent.Name} from {surfaceEvent.Source.Id}.");
        return accepted;
    }

    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (PendingSurfaceEvent pending in _events.Reader.ReadAllAsync(_stop.Token))
                _session.InvokeSurfaceHandler(pending.Handler, pending.Event);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Android.Util.Log.Error("Terminal.Surface", error.ToString()); }
    }

    public void Dispose()
    {
        _events.Writer.TryComplete();
        _stop.Cancel();
        try { _consumer.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _stop.Dispose();
    }

    private sealed record PendingSurfaceEvent(ScriptBlock Handler, SurfaceEvent Event);
}
