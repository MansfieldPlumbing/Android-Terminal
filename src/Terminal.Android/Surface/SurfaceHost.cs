using Android.App;
using System.Management.Automation;

namespace NativePwshConsole.Surface;

internal sealed class SurfaceHost : IDisposable
{
    private readonly object _gate = new();
    private readonly SurfaceEventDispatcher _dispatcher;
    private WeakReference<Activity>? _activity;
    private SurfaceDocument? _document;
    private SurfaceAndroidRenderer? _renderer;

    public SurfaceHost(PowerShellSession session) => _dispatcher = new SurfaceEventDispatcher(session);

    public PSObject Show(SurfaceDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        lock (_gate)
        {
            _renderer?.Dispose();
            _renderer = null;
            _document = document;
            AttachRendererLocked();
            return document.UI;
        }
    }

    public void Attach(Activity activity)
    {
        lock (_gate)
        {
            if (_activity?.TryGetTarget(out Activity? current) == true && !ReferenceEquals(current, activity))
            {
                _renderer?.Dispose();
                _renderer = null;
            }
            _activity = new WeakReference<Activity>(activity);
            AttachRendererLocked();
        }
    }

    public void Detach(Activity activity)
    {
        lock (_gate)
        {
            if (_activity?.TryGetTarget(out Activity? current) == true && ReferenceEquals(current, activity))
            {
                _renderer?.Dispose();
                _renderer = null;
                _activity = null;
            }
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            _renderer?.Dispose();
            _renderer = null;
            _document = null;
        }
    }

    private void AttachRendererLocked()
    {
        if (_renderer != null || _document == null ||
            _activity?.TryGetTarget(out Activity? activity) != true || activity.IsFinishing || activity.IsDestroyed) return;
        SurfaceDocument expected = _document;
        activity.RunOnUiThread(() =>
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_document, expected) || _renderer != null) return;
                var renderer = new SurfaceAndroidRenderer(activity, expected, _dispatcher, () => Dismissed(expected));
                _renderer = renderer;
                renderer.Show();
            }
        });
    }

    private void Dismissed(SurfaceDocument expected)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_document, expected)) return;
            _renderer = null;
            _document = null;
        }
    }

    public void Dispose()
    {
        Close();
        _dispatcher.Dispose();
    }
}
