using System;
using System.Linq;
using Android.Content;
using Android.Net;
using NodeX.Core;

namespace NodeX.UI.Android;

/// <summary>
/// <see cref="INetworkMonitor"/> backed by <see cref="ConnectivityManager"/>'s default-network callback.
/// Event-driven (no polling, no wake-ups of its own), and it works on recent Android where
/// <c>NetworkInterface</c> enumeration is restricted. The signature is the default network plus its IPv4
/// addresses (or just the network on IPv6-only mobile data), so a Wi-Fi/cellular switch or a new address is reported but a
/// DHCP renewal is not.
/// </summary>
public sealed class AndroidNetworkMonitor : INetworkMonitor
{
    private readonly ConnectivityManager? _cm;
    private readonly Callback? _callback;
    private readonly object _gate = new();
    private string _signature = "none";
    private bool _available;

    public AndroidNetworkMonitor(Context context)
    {
        _cm = (context.ApplicationContext ?? context).GetSystemService(Context.ConnectivityService) as ConnectivityManager;
        if (_cm == null)
        {
            _available = true;   // cannot tell; the keep-alive watchdog still catches link loss
            _signature = "unknown";
            return;
        }

        Refresh(_cm.ActiveNetwork);
        _callback = new Callback(this);
        _cm.RegisterDefaultNetworkCallback(_callback);   // API 24+; minimum here is 26
    }

    public event Action? Changed;

    public bool IsAvailable { get { lock (_gate) return _available; } }

    public string GetSignature() { lock (_gate) return _signature; }

    private void Refresh(Network? network)
    {
        string signature = "none";
        bool available = false;

        if (network != null && _cm?.GetLinkProperties(network) is { } props)
        {
            var usable = props.LinkAddresses
                .Select(a => a.Address)
                .Where(a => a != null && !a.IsLinkLocalAddress && !a.IsLoopbackAddress)
                .ToArray();
            var v4 = usable
                .Where(a => a is Java.Net.Inet4Address)
                .Select(a => a!.HostAddress)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (v4.Length > 0)
            {
                available = true;
                signature = $"{network.NetworkHandle}={string.Join(',', v4)}";
            }
            else if (usable.Any(a => a is Java.Net.Inet6Address))
            {
                // IPv6-only mobile data (T-Mobile US and others): IPv4 reaches the internet through 464XLAT on a
                // stacked interface that LinkAddresses does not list, so "no IPv4 address" is not "offline"
                // (2026-11-21 review: such phones sat at "Waiting for a network connection"). The signature is the
                // network alone: IPv6 privacy addresses rotate on their own and must not look like a handoff.
                available = true;
                signature = $"{network.NetworkHandle}=ipv6";
            }
        }

        bool changed;
        string before;
        lock (_gate)
        {
            before = _signature;
            changed = signature != _signature || available != _available;
            _signature = signature;
            _available = available;
        }

        if (changed)
        {
            NodeX.Core.Diagnostics.DiagnosticLog.Default.Info("net",
                $"Android default network: {Describe(network)} \"{before}\" -> \"{signature}\" available={available}");
            Changed?.Invoke();
        }
    }

    /// <summary>"wifi", "cellular"... for the log; a Wi-Fi to cellular handoff is exactly what is being diagnosed.</summary>
    private string Describe(Network? network)
    {
        if (network == null) return "none";
        try
        {
            var caps = _cm?.GetNetworkCapabilities(network);
            if (caps == null) return "unknown";
            if (caps.HasTransport(TransportType.Wifi)) return "wifi";
            if (caps.HasTransport(TransportType.Cellular)) return "cellular";
            if (caps.HasTransport(TransportType.Ethernet)) return "ethernet";
            if (caps.HasTransport(TransportType.Vpn)) return "vpn";
            if (caps.HasTransport(TransportType.Bluetooth)) return "bluetooth";
            return "other";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    public void Dispose()
    {
        if (_cm != null && _callback != null)
        {
            try { _cm.UnregisterNetworkCallback(_callback); } catch (Exception) { /* already gone */ }
            _callback.Dispose();
        }
    }

    private sealed class Callback(AndroidNetworkMonitor owner) : ConnectivityManager.NetworkCallback
    {
        public override void OnAvailable(Network network) => owner.Refresh(network);
        public override void OnLinkPropertiesChanged(Network network, LinkProperties linkProperties) => owner.Refresh(network);
        public override void OnLost(Network network) => owner.Refresh(null);
    }
}
