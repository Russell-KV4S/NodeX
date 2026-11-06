using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NodeX.Core;

/// <summary>
/// Watches the device's network path. An IAX2 call is tied to our source address, so when the address
/// changes (Wi-Fi to cellular, new Wi-Fi network, VPN on/off) the call and our NAT mapping are gone
/// and the link has to be re-established.
/// </summary>
public interface INetworkMonitor : IDisposable
{
    /// <summary>Raised (possibly several times in a burst, on any thread) when addresses may have changed.</summary>
    event Action? Changed;

    /// <summary>True when there is a usable IPv4 route (an operational, non-loopback interface with an address).</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// A stable description of the current usable addresses. Equal signatures mean the path did not
    /// really change (for example a DHCP renewal), so nothing needs to be redone.
    /// </summary>
    string GetSignature();
}

/// <summary>
/// <see cref="INetworkMonitor"/> backed by <see cref="NetworkChange"/> and <see cref="NetworkInterface"/>.
/// Where the platform restricts these APIs (recent Android versions limit network enumeration) it degrades
/// to "assume available, signature unknown"; link loss is then still caught by the keep-alive watchdog.
/// </summary>
public sealed class SystemNetworkMonitor : INetworkMonitor
{
    private bool _subscribed;

    public SystemNetworkMonitor()
    {
        try
        {
            NetworkChange.NetworkAddressChanged += OnChanged;
            NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
            _subscribed = true;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NetworkInformationException)
        {
            // No change notifications on this platform; polling callers still get signatures.
        }
    }

    public event Action? Changed;

    public bool IsAvailable
    {
        get
        {
            try
            {
                return UsableAddresses().Any();
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException or NetworkInformationException)
            {
                return true;
            }
        }
    }

    public string GetSignature()
    {
        try
        {
            return string.Join('|', UsableAddresses().Order(StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NetworkInformationException)
        {
            return "unknown";
        }
    }

    private static IEnumerable<string> UsableAddresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork) continue;   // the session socket is IPv4

                byte[] b = address.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) continue;                             // link-local: no real connectivity

                yield return $"{nic.Id}={address}";
            }
        }
    }

    private void OnChanged(object? sender, EventArgs e) => Changed?.Invoke();

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => Changed?.Invoke();

    public void Dispose()
    {
        if (!_subscribed) return;
        _subscribed = false;
        NetworkChange.NetworkAddressChanged -= OnChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
    }
}
