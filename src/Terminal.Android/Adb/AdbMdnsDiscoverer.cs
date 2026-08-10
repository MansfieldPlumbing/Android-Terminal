using Android.Content;
using Android.Net.Nsd;
using System;
using Android.Util;

namespace Subsystem;

public class AdbMdnsDiscoverer
{
    // "_adb-tls-pairing._tcp." (pairing, ephemeral) or "_adb-tls-connect._tcp." (connect, persistent)
    public const string PairingService = "_adb-tls-pairing._tcp.";
    public const string ConnectService = "_adb-tls-connect._tcp.";

    private const string Tag = "SubsystemMdns";
    private readonly string _serviceType;
    private readonly NsdManager _nsdManager;
    private DiscoverySink? _discoveryListener;

    public Action<int>? OnPortDiscovered;
    public Action? OnServiceLost;

    public AdbMdnsDiscoverer(Context context, string serviceType = PairingService)
    {
        _serviceType = serviceType;
        _nsdManager = (NsdManager)context.GetSystemService(Context.NsdService)!;
    }

    public void StartDiscovery()
    {
        if (_discoveryListener != null) return;
        _discoveryListener = new DiscoverySink(this);
        _nsdManager.DiscoverServices(_serviceType, NsdProtocol.DnsSd, _discoveryListener);
        Log.Debug(Tag, $"Started mDNS discovery for {_serviceType}");
    }

    public void StopDiscovery()
    {
        if (_discoveryListener != null)
        {
            _nsdManager.StopServiceDiscovery(_discoveryListener);
            _discoveryListener = null;
            Log.Debug(Tag, "Stopped mDNS discovery");
        }
    }

    private class DiscoverySink : Java.Lang.Object, NsdManager.IDiscoveryListener
    {
        private readonly AdbMdnsDiscoverer _parent;

        public DiscoverySink(AdbMdnsDiscoverer parent)
        {
            _parent = parent;
        }

        public void OnDiscoveryStarted(string regType) { Log.Debug(Tag, "Discovery Started"); }
        public void OnDiscoveryStopped(string serviceType) { Log.Debug(Tag, "Discovery Stopped"); }
        public void OnStartDiscoveryFailed(string serviceType, NsdFailure errorCode) { Log.Error(Tag, $"Start Discovery Failed: {errorCode}"); }
        public void OnStopDiscoveryFailed(string serviceType, NsdFailure errorCode) { Log.Error(Tag, $"Stop Discovery Failed: {errorCode}"); }

        public void OnServiceFound(NsdServiceInfo serviceInfo)
        {
            // NsdManager already filters to the requested service type; resolve to get the port.
            Log.Debug(Tag, $"Service Found: {serviceInfo.ServiceName} ({serviceInfo.ServiceType})");
            _parent._nsdManager.ResolveService(serviceInfo, new ResolveSink(_parent));
        }

        public void OnServiceLost(NsdServiceInfo serviceInfo)
        {
            Log.Debug(Tag, $"Service Lost: {serviceInfo.ServiceName}");
            _parent.OnServiceLost?.Invoke();
        }
    }

    private class ResolveSink : Java.Lang.Object, NsdManager.IResolveListener
    {
        private readonly AdbMdnsDiscoverer _parent;

        public ResolveSink(AdbMdnsDiscoverer parent)
        {
            _parent = parent;
        }

        public void OnResolveFailed(NsdServiceInfo serviceInfo, NsdFailure errorCode)
        {
            Log.Error(Tag, $"Resolve Failed: {errorCode}");
        }

        public void OnServiceResolved(NsdServiceInfo serviceInfo)
        {
            int port = serviceInfo.Port;
            Log.Debug(Tag, $"Service Resolved! Port: {port}");
            _parent.OnPortDiscovered?.Invoke(port);
        }
    }
}
