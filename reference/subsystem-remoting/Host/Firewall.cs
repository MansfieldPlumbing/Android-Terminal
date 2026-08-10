using System;
using System.Net;
using Android.Content;
using Android.Net;

namespace Subsystem;

// Firewall — the connection-zone gate in front of the control plane (Se). INDEPENDENT of BindGuard:
//   BindGuard  controls WHAT may bind  (loopback unless https + auth) — bind time.
//   Firewall   controls WHO may connect once bound, by NETWORK ZONE  — connection time.
// Even a 0.0.0.0 bind is useless to an unauthorized zone whose zone has no allow rule. Router-style: default-deny,
// per-zone rules. SAFE-BY-DEFAULT and FAIL-CLOSED — anything we can't positively classify-and-allow is dropped.
//
// Trust tiers (least -> most): Mobile < WifiPublic < WifiPrivate < Usb < Loopback.
//   Loopback/Usb : always allowed (physical tether / in-process; adb-forward arrives as loopback).
//   WifiPrivate  : allowed ONLY with an opt-in rule (trusted SSID).
//   WifiPublic   : denied unless an explicit rule.
//   Mobile       : HARD-DENIED — the cellular interface never faces the control plane unless a separate,
//                  explicit, *warned* acknowledgment is set (the loudest gate). "Never, without a warning."
// Rules are OBJECTS in Cm (\Capability\Firewall\Zone\<zone>, enabled flag) — registry-backed, NT-faithful.
public enum FirewallZone { Loopback, Usb, WifiPrivate, WifiPublic, Mobile, Unknown }

public static class Firewall
{
    private static Context? _ctx;
    public static void Attach(Context ctx) => _ctx = ctx;

    // Evaluate a freshly-accepted connection. true = ALLOW, false = DROP. Fails CLOSED on any error.
    public static bool Allow(IPEndPoint? remote, IPEndPoint? local)
    {
        try
        {
            var zone = Classify(remote);
            bool ok = PolicyAllows(zone);
            Subsystem.Dg.Log("firewall", (ok ? "ALLOW " : "DENY  ") + (remote?.Address?.ToString() ?? "?") + " zone=" + zone);
            return ok;
        }
        catch (Exception ex)
        {
            Subsystem.Dg.Log("firewall", "eval error -> DENY: " + ex.Message);   // fail closed
            return false;
        }
    }

    // The tier policy. Loopback/Usb always; the rest by Cm rule; Mobile gated by a separate warned ack.
    private static bool PolicyAllows(FirewallZone zone)
    {
        switch (zone)
        {
            case FirewallZone.Loopback:    return true;
            case FirewallZone.Usb:         return true;   // physical tether — trusted
            case FirewallZone.WifiPrivate: return ZoneEnabled("WifiPrivate");
            case FirewallZone.WifiPublic:  return ZoneEnabled("WifiPublic");
            case FirewallZone.Mobile:      return ZoneEnabled("Mobile") && MobileExposureAcknowledged();
            default:                       return false;   // Unknown -> deny
        }
    }

    private static bool ZoneEnabled(string zone)
    {
        var rec = Subsystem.Cm.Cm.Get("\\Capability\\Firewall\\Zone\\" + zone);
        return rec != null && rec.Enabled;                  // absent -> deny (safe default; no seeding needed)
    }

    // Mobile exposure is the loudest gate: a SEPARATE explicit acknowledgment beyond the zone rule, so the
    // cellular interface can only be faced after a deliberate, warned opt-in.
    private static bool MobileExposureAcknowledged()
    {
        var rec = Subsystem.Cm.Cm.Get("\\Capability\\Firewall\\MobileExposureAcknowledged");
        return rec != null && rec.Enabled;
    }

    // Zone classification. Loopback by IP; otherwise by the active network's transport (ConnectivityManager).
    // WiFi splits private/public by a trusted-SSID list (default: public). Anything unclear -> Unknown (deny).
    private static FirewallZone Classify(IPEndPoint? remote)
    {
        var ip = remote?.Address;
        if (ip != null && IPAddress.IsLoopback(ip)) return FirewallZone.Loopback;   // incl. adb-forward over USB

        if (_ctx == null) return FirewallZone.Unknown;
        var cm = (ConnectivityManager?)_ctx.GetSystemService(Context.ConnectivityService);
        var net = cm?.ActiveNetwork;
        var caps = net != null ? cm!.GetNetworkCapabilities(net) : null;
        if (caps == null) return FirewallZone.Unknown;

        if (caps.HasTransport(Android.Net.TransportType.Cellular)) return FirewallZone.Mobile;
        if (OperatingSystem.IsAndroidVersionAtLeast(31) && caps.HasTransport(Android.Net.TransportType.Usb)) return FirewallZone.Usb;
        if (caps.HasTransport(Android.Net.TransportType.Ethernet))  return FirewallZone.WifiPrivate;   // wired LAN ~ private
        if (caps.HasTransport(Android.Net.TransportType.Wifi))      return WifiTrusted() ? FirewallZone.WifiPrivate : FirewallZone.WifiPublic;
        return FirewallZone.Unknown;
    }

    // A WiFi network is "private" only if its SSID is in the user's trusted list (Cm:
    // \Capability\Firewall\TrustedSsid\<ssid>). Reading the SSID needs location perms on modern Android; until
    // that's wired, default to PUBLIC (the safe, stricter classification).
    private static bool WifiTrusted() => false;
}
