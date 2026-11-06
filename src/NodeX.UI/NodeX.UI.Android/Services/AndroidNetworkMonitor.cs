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
/// addresses, so a Wi-Fi/cellular switch or a new address is reported but a DHCP renewal is not.
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
            var v4 = props.LinkAddresses
                .Select(a => a.Address)
                .Where(a => a is Java.Net.Inet4Address && !a.IsLinkLocalAddress && !a.IsLoopbackAddress)
                .Select(a => a!.HostAddress)
                .Order(StringComparer.Ordinal)
                .ToArray();
            available = v4.Length > 0;
            if (available) signature = $"{network.NetworkHandle}={string.Join(',', v4)}";
        }

        bool changed;
        lock (_gate)
        {
            changed = signature != _signature || available != _available;
            _signature = signature;
            _available = available;
        }
        if (changed) Changed?.Invoke();
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
