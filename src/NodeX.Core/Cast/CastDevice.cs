using System.Net;
using System.Net.Sockets;

namespace NodeX.Core.Cast;

/// <summary>
/// A discovered or manually entered Google Cast (Chromecast / Google TV / Nest speaker) target on the local network.
/// </summary>
public sealed record CastDevice(
    string Id,
    string FriendlyName,
    string Model,
    IPAddress Address,
    int Port = CastProtocol.DefaultPort,
    bool HasScreen = true,
    string? StatusText = null)
{
    public string EndpointDisplay => Port == CastProtocol.DefaultPort ? Address.ToString() : $"{Address}:{Port}";

    public string DisplaySubtitle => string.IsNullOrWhiteSpace(Model)
        ? EndpointDisplay
        : $"{Model} · {EndpointDisplay}";

    /// <summary>Parses a manual IPv4 address with optional <c>:port</c> (default 8009).</summary>
    public static bool TryParseManual(string? input, out CastDevice? device, out string? error)
    {
        device = null;
        string raw = (input ?? "").Trim();
        if (raw.Length == 0)
        {
            error = "Enter the Chromecast's local IP address (e.g. 192.168.1.50).";
            return false;
        }

        if (raw.Contains('[') || raw.Contains(']') || raw.Count(c => c == ':') > 1)
        {
            error = "Enter an IPv4 address (e.g. 192.168.1.50 or 192.168.1.50:8009).";
            return false;
        }

        string host = raw;
        int port = CastProtocol.DefaultPort;
        int colon = raw.IndexOf(':');
        if (colon >= 0)
        {
            host = raw[..colon].Trim();
            string portStr = raw[(colon + 1)..].Trim();
            if (!int.TryParse(portStr, out port) || port is < 1 or > 65535)
            {
                error = "Chromecast port must be between 1 and 65535.";
                return false;
            }
        }

        if (host.Count(c => c == '.') != 3
            || !IPAddress.TryParse(host, out var ip)
            || ip.AddressFamily != AddressFamily.InterNetwork)
        {
            error = "Enter a valid local IPv4 address (e.g. 192.168.1.50).";
            return false;
        }

        error = null;
        device = new CastDevice(
            Id: $"manual-{ip}-{port}",
            FriendlyName: ip.ToString(),
            Model: "Chromecast (manual)",
            Address: ip,
            Port: port,
            HasScreen: true);
        return true;
    }
}
