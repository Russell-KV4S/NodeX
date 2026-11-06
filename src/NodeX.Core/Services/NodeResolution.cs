using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace NodeX.Core;

/// <summary>
/// Resolved AllStarLink node connection information.
/// </summary>
public record NodeInfo(
    string NodeNumber,
    string Callsign,
    IPEndPoint EndPoint,
    string Description,
    DateTime ResolvedAt
);

/// <summary>
/// Service for resolving AllStarLink node numbers to network IP endpoints.
/// Supports REST API lookups, local static cache overrides, and DNS fallback.
/// </summary>
public sealed class NodeResolution
{
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, (NodeInfo Info, DateTime ExpiresAt)> _cache = new();
    private readonly ConcurrentDictionary<string, IPEndPoint> _staticOverrides = new();
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _dnsResolver;
    private readonly Func<DateTime> _utcNow;

    /// <summary>How long successful directory/DNS lookups are cached.</summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Base URL for the AllStarLink stats directory API.</summary>
    public string DirectoryBaseUrl { get; set; } = "https://stats.allstarlink.org/api/stats/";

    /// <param name="httpClient">HTTP client for directory lookups (inject a stub handler for tests).</param>
    /// <param name="dnsResolver">Host-name resolver (defaults to <see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/>).</param>
    /// <param name="utcNow">Clock used for cache expiry (defaults to <see cref="_utcNow()"/>).</param>
    public NodeResolution(
        HttpClient? httpClient = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? dnsResolver = null,
        Func<DateTime>? utcNow = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        _dnsResolver = dnsResolver ?? Dns.GetHostAddressesAsync;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Checks whether an IAX endpoint answers (used to pick between a node's listed port and the standard 4569).
    /// Null disables the check. The default sends an IAX POKE and waits briefly for any reply.
    /// </summary>
    public Func<IPEndPoint, CancellationToken, Task<bool>>? PortProbe { get; set; } = IaxPokeAsync;

    /// <summary>How long the default probe waits for a reply.</summary>
    public static TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromMilliseconds(1500);

    private static async Task<bool> IaxPokeAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using var udp = new System.Net.Sockets.UdpClient(endpoint.AddressFamily);
            byte[] poke = new byte[Iax2Protocol.FullHeaderSize];
            Iax2Protocol.WriteFullFrame(poke, (ushort)Random.Shared.Next(1, 0x7FFF), 0, 0, 0, 0,
                Iax2Protocol.FrameType.IaxControl, (byte)Iax2Protocol.IaxControlSubclass.Poke);
            await udp.SendAsync(poke, endpoint, cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);
            await udp.ReceiveAsync(timeout.Token);   // any datagram back means something is listening
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Clears all cached lookups (static overrides are kept).</summary>
    public void ClearCache() => _cache.Clear();

    /// <summary>
    /// Sets a manual IP endpoint override for a specific node (useful for testing, private nodes, or LAN).
    /// </summary>
    public void SetStaticOverride(string nodeNumber, IPEndPoint endPoint)
    {
        _staticOverrides[nodeNumber] = endPoint;
    }

    /// <summary>
    /// Clears a manual static override.
    /// </summary>
    public void RemoveStaticOverride(string nodeNumber)
    {
        _staticOverrides.TryRemove(nodeNumber, out _);
    }

    /// <summary>
    /// Resolves an AllStarLink node number to its active IPEndPoint and metadata.
    /// </summary>
    public async Task<NodeInfo?> ResolveNodeAsync(string nodeNumber, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(nodeNumber)) return null;
        nodeNumber = nodeNumber.Trim();

        // Check static overrides first
        if (_staticOverrides.TryGetValue(nodeNumber, out var overrideEp))
        {
            return new NodeInfo(nodeNumber, "STATIC", overrideEp, "Local Override", _utcNow());
        }

        // Check cache
        if (_cache.TryGetValue(nodeNumber, out var cachedEntry))
        {
            if (_utcNow() < cachedEntry.ExpiresAt)
            {
                return cachedEntry.Info;
            }
            _cache.TryRemove(nodeNumber, out _);
        }

        // 1. Try AllStarLink JSON / HTTP directory lookup
        try
        {
            string url = DirectoryBaseUrl + Uri.EscapeDataString(nodeNumber);
            using var response = await _httpClient.GetAsync(url, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                using var jsonDoc = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken),
                    cancellationToken: cancellationToken);

                var root = jsonDoc.RootElement;
                // Verified live 2026-11-05 (GET /api/stats/2530): the node's own registry record is the
                // top-level "node" object ({ipaddr, port, callsign, node_frequency, Status, ...}); the
                // "stats.data.linkedNodes" entries describe the node's links, not the node itself.
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("node", out var data)
                    && data.ValueKind == JsonValueKind.Object)
                {
                    string ipStr = data.TryGetProperty("ipaddr", out var ipEl) && ipEl.ValueKind == JsonValueKind.String
                        ? ipEl.GetString() ?? "" : "";
                    int port = data.TryGetProperty("port", out var portEl) && portEl.TryGetInt32(out int p) && p is > 0 and <= 65535
                        ? p : Iax2Protocol.DefaultPort;
                    string callsign = data.TryGetProperty("callsign", out var callEl) && callEl.ValueKind == JsonValueKind.String
                        ? callEl.GetString() ?? "" : "";
                    string desc = data.TryGetProperty("node_frequency", out var descEl) && descEl.ValueKind == JsonValueKind.String
                        ? descEl.GetString() ?? "" : "";

                    if (IPAddress.TryParse(ipStr, out var ip))
                    {
                        var endpoint = new IPEndPoint(ip, port);

                        // The directory's port is where the node registered from, which is not always where it
                        // listens: node 45192 is listed on 4579 but only answers on 4569 (the port its AllStarLink
                        // DNS record gives, and the one DVSwitch Mobile uses). If the listed port is not the
                        // standard one and does not answer an IAX POKE, try the standard port.
                        if (port != Iax2Protocol.DefaultPort && PortProbe != null
                            && !await PortProbe(endpoint, cancellationToken))
                        {
                            var standard = new IPEndPoint(ip, Iax2Protocol.DefaultPort);
                            if (await PortProbe(standard, cancellationToken)) endpoint = standard;
                        }

                        var info = new NodeInfo(nodeNumber, callsign, endpoint, desc, _utcNow());
                        _cache[nodeNumber] = (info, _utcNow().Add(CacheTtl));
                        return info;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Fall through to DNS resolution
        }

        // 2. DNS fallback: <node>.nodes.allstarlink.org (ASL3), then legacy node<node>.allstarlink.org
        foreach (string host in new[] { $"{nodeNumber}.nodes.allstarlink.org", $"node{nodeNumber}.allstarlink.org" })
        {
            try
            {
                var addresses = await _dnsResolver(host, cancellationToken);
                var ipv4 = Array.Find(addresses, a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                           ?? (addresses.Length > 0 ? addresses[0] : null);
                if (ipv4 != null)
                {
                    var endpoint = new IPEndPoint(ipv4, Iax2Protocol.DefaultPort);
                    var info = new NodeInfo(nodeNumber, string.Empty, endpoint, "DNS Fallback", _utcNow());
                    _cache[nodeNumber] = (info, _utcNow().Add(CacheTtl));
                    return info;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Try next host
            }
        }

        return null;
    }
}
