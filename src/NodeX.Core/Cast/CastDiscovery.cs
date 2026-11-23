using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace NodeX.Core.Cast;

/// <summary>
/// Discovers Chromecast and Google Cast devices on the local LAN using RFC 6762 mDNS
/// (<c>_googlecast._tcp.local.</c> on <c>224.0.0.251:5353</c>) with zero native SDK dependencies.
/// </summary>
public static class CastDiscovery
{
    public const string ServiceName = "_googlecast._tcp.local";
    private const int MdnsPort = 5353;
    private static readonly IPAddress MdnsAddress = IPAddress.Parse("224.0.0.251");

    private const ushort TypeA = 1;
    private const ushort TypePtr = 12;
    private const ushort TypeTxt = 16;
    private const ushort TypeSrv = 33;

    public static async Task<IReadOnlyList<CastDevice>> DiscoverAsync(
        TimeSpan? timeout = null,
        Func<IDisposable?>? multicastLockAcquirer = null,
        CancellationToken cancellationToken = default)
    {
        TimeSpan wait = timeout ?? TimeSpan.FromMilliseconds(2400);
        using var mcastLock = SafeAcquire(multicastLockAcquirer);

        var accumulator = new MdnsAccumulator();
        var unicastSockets = new List<UdpClient>();
        UdpClient? multicastListener = TryCreateMulticastListener();
        var receiveTasks = new List<Task>();

        using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        scanCts.CancelAfter(wait);

        try
        {
            var localAddrs = LocalIpv4Addresses();
            if (localAddrs.Count == 0)
                localAddrs.Add(IPAddress.Any);

            foreach (var localIp in localAddrs)
            {
                try
                {
                    var u = new UdpClient(new IPEndPoint(localIp, 0));
                    unicastSockets.Add(u);
                }
                catch
                {
                    // Skip interfaces that reject UDP bind
                }
            }

            if (multicastListener != null)
                receiveTasks.Add(ReceiveLoopAsync(multicastListener, accumulator, scanCts.Token));
            foreach (var u in unicastSockets)
                receiveTasks.Add(ReceiveLoopAsync(u, accumulator, scanCts.Token));

            byte[] ptrQuery = BuildPtrQuery();
            var mcastEndpoint = new IPEndPoint(MdnsAddress, MdnsPort);

            await BroadcastAsync(unicastSockets, ptrQuery, mcastEndpoint, scanCts.Token).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(650), scanCts.Token).ConfigureAwait(false);
            await BroadcastAsync(unicastSockets, ptrQuery, mcastEndpoint, scanCts.Token).ConfigureAwait(false);

            // Follow up on any PTR instance that answered without SRV/TXT/A in its additional section.
            foreach (string instance in accumulator.PendingInstances())
            {
                byte[] srv = BuildQuery(instance, TypeSrv, unicastResponse: true);
                byte[] txt = BuildQuery(instance, TypeTxt, unicastResponse: true);
                await BroadcastAsync(unicastSockets, srv, mcastEndpoint, scanCts.Token).ConfigureAwait(false);
                await BroadcastAsync(unicastSockets, txt, mcastEndpoint, scanCts.Token).ConfigureAwait(false);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, scanCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Scan window elapsed normally.
        }
        finally
        {
            multicastListener?.Dispose();
            foreach (var u in unicastSockets) u.Dispose();
            try { await Task.WhenAll(receiveTasks).ConfigureAwait(false); } catch { }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return accumulator.BuildDevices();
    }

    internal static byte[] BuildPtrQuery(string service = ServiceName, bool unicastResponse = true) =>
        BuildQuery(service, TypePtr, unicastResponse);

    internal static byte[] BuildQuery(string domainName, ushort qtype, bool unicastResponse = true)
    {
        var bytes = new List<byte>(64)
        {
            0x00, 0x00,             // Transaction ID (0 per RFC 6762)
            0x00, 0x00,             // Flags: standard query
            0x00, 0x01,             // QDCOUNT = 1
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        };

        foreach (string label in domainName.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] encoded = Encoding.UTF8.GetBytes(label);
            if (encoded.Length is 0 or > 63) continue;
            bytes.Add((byte)encoded.Length);
            bytes.AddRange(encoded);
        }
        bytes.Add(0);
        bytes.Add((byte)(qtype >> 8));
        bytes.Add((byte)qtype);
        ushort qclass = (ushort)(unicastResponse ? 0x8001 : 0x0001);
        bytes.Add((byte)(qclass >> 8));
        bytes.Add((byte)qclass);
        return bytes.ToArray();
    }

    /// <summary>Merges partial PTR/SRV/TXT/A records across mDNS packets into deduplicated <see cref="CastDevice"/> entries.</summary>
    internal sealed class MdnsAccumulator
    {
        private sealed class ServiceEntry
        {
            public string InstanceName = "";
            public string? TargetHost;
            public int Port = CastProtocol.DefaultPort;
            public string? FriendlyName;
            public string? Model;
            public string? Id;
            public string? StatusText;
            public bool HasScreen = true;
        }

        private readonly Lock _gate = new();
        private readonly HashSet<string> _instances = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ServiceEntry> _services = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IPAddress> _hosts = new(StringComparer.OrdinalIgnoreCase);

        public void AddPacket(ReadOnlySpan<byte> msg)
        {
            if (msg.Length < 12) return;
            ushort flags = BinaryPrimitives.ReadUInt16BigEndian(msg.Slice(2, 2));
            if ((flags & 0x8000) == 0) return;   // ignore queries

            int qd = BinaryPrimitives.ReadUInt16BigEndian(msg.Slice(4, 2));
            int an = BinaryPrimitives.ReadUInt16BigEndian(msg.Slice(6, 2));
            int ns = BinaryPrimitives.ReadUInt16BigEndian(msg.Slice(8, 2));
            int ar = BinaryPrimitives.ReadUInt16BigEndian(msg.Slice(10, 2));

            int offset = 12;
            for (int i = 0; i < qd; i++)
            {
                if (!TryReadName(msg, ref offset, out _) || msg.Length - offset < 4) return;
                offset += 4;
            }

            int totalRr = an + ns + ar;
            lock (_gate)
            {
                for (int i = 0; i < totalRr && offset < msg.Length; i++)
                {
                    if (!TryReadName(msg, ref offset, out string rrName) || msg.Length - offset < 10)
                        return;

                    ushort rtype = BinaryPrimitives.ReadUInt16BigEndian(msg.Slice(offset, 2));
                    offset += 8; // type(2) + class(2) + ttl(4)
                    ushort rdLength = BinaryPrimitives.ReadUInt16BigEndian(msg.Slice(offset, 2));
                    offset += 2;
                    if (msg.Length - offset < rdLength) return;

                    int rdataStart = offset;
                    ReadOnlySpan<byte> rdata = msg.Slice(rdataStart, rdLength);
                    offset += rdLength;

                    if (rtype == TypePtr && IsGoogleCastService(rrName))
                    {
                        int p = rdataStart;
                        if (TryReadName(msg, ref p, out string instance) && instance.Length > 0)
                        {
                            _instances.Add(instance);
                            GetOrCreateService(instance);
                        }
                    }
                    else if (rtype == TypeSrv && IsGoogleCastInstance(rrName) && rdLength >= 6)
                    {
                        ushort port = BinaryPrimitives.ReadUInt16BigEndian(rdata.Slice(4, 2));
                        int p = rdataStart + 6;
                        if (TryReadName(msg, ref p, out string host) && host.Length > 0 && port > 0)
                        {
                            _instances.Add(rrName);
                            var entry = GetOrCreateService(rrName);
                            entry.TargetHost = host;
                            entry.Port = port;
                        }
                    }
                    else if (rtype == TypeTxt && IsGoogleCastInstance(rrName))
                    {
                        _instances.Add(rrName);
                        var entry = GetOrCreateService(rrName);
                        ParseTxtRecord(rdata, entry);
                    }
                    else if (rtype == TypeA && rdLength == 4)
                    {
                        var ip = new IPAddress(rdata);
                        if (ip.AddressFamily == AddressFamily.InterNetwork)
                            _hosts[rrName.TrimEnd('.')] = ip;
                    }
                }
            }
        }

        public IReadOnlyList<string> PendingInstances()
        {
            lock (_gate)
            {
                var pending = new List<string>();
                foreach (string inst in _instances)
                {
                    if (!_services.TryGetValue(inst, out var s)
                        || string.IsNullOrEmpty(s.TargetHost)
                        || !_hosts.ContainsKey(s.TargetHost.TrimEnd('.')))
                    {
                        pending.Add(inst);
                    }
                }
                return pending;
            }
        }

        public IReadOnlyList<CastDevice> BuildDevices()
        {
            lock (_gate)
            {
                var byKey = new Dictionary<string, CastDevice>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in _services.Values)
                {
                    if (string.IsNullOrWhiteSpace(entry.TargetHost)) continue;
                    string hostKey = entry.TargetHost.TrimEnd('.');
                    if (!_hosts.TryGetValue(hostKey, out var ip)) continue;

                    string fallbackName = FallbackFriendlyName(entry.InstanceName, ip);
                    string name = !string.IsNullOrWhiteSpace(entry.FriendlyName) ? entry.FriendlyName! : fallbackName;
                    string id = !string.IsNullOrWhiteSpace(entry.Id) ? entry.Id! : $"{ip}:{entry.Port}";
                    string dedupeKey = !string.IsNullOrWhiteSpace(entry.Id) ? $"id:{entry.Id}" : $"ep:{ip}:{entry.Port}";

                    byKey[dedupeKey] = new CastDevice(
                        Id: id,
                        FriendlyName: name,
                        Model: !string.IsNullOrWhiteSpace(entry.Model) ? entry.Model! : "Chromecast",
                        Address: ip,
                        Port: entry.Port,
                        HasScreen: entry.HasScreen,
                        StatusText: entry.StatusText);
                }

                return byKey.Values
                    .OrderBy(d => d.FriendlyName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(d => d.Address.ToString(), StringComparer.Ordinal)
                    .ToList();
            }
        }

        private ServiceEntry GetOrCreateService(string instance)
        {
            string key = instance.TrimEnd('.');
            if (!_services.TryGetValue(key, out var entry))
            {
                entry = new ServiceEntry { InstanceName = key };
                _services[key] = entry;
            }
            return entry;
        }

        private static bool IsGoogleCastService(string rrName) =>
            rrName.TrimEnd('.').Equals(ServiceName, StringComparison.OrdinalIgnoreCase);

        private static bool IsGoogleCastInstance(string rrName) =>
            rrName.TrimEnd('.').EndsWith("." + ServiceName, StringComparison.OrdinalIgnoreCase);

        private static string FallbackFriendlyName(string instanceName, IPAddress ip)
        {
            int idx = instanceName.IndexOf("._googlecast", StringComparison.OrdinalIgnoreCase);
            if (idx > 0) return instanceName[..idx];
            return ip.ToString();
        }

        private static void ParseTxtRecord(ReadOnlySpan<byte> rdata, ServiceEntry entry)
        {
            int pos = 0;
            while (pos < rdata.Length)
            {
                int len = rdata[pos++];
                if (len == 0 || pos + len > rdata.Length) break;
                string kv = Encoding.UTF8.GetString(rdata.Slice(pos, len));
                pos += len;

                int eq = kv.IndexOf('=');
                if (eq <= 0) continue;
                string key = kv[..eq];
                string val = kv[(eq + 1)..];
                switch (key)
                {
                    case "fn" when val.Length > 0:
                        entry.FriendlyName = val;
                        break;
                    case "md" when val.Length > 0:
                        entry.Model = val;
                        break;
                    case "id" when val.Length > 0:
                        entry.Id = val;
                        break;
                    case "rs":
                        entry.StatusText = string.IsNullOrWhiteSpace(val) ? null : val.Trim();
                        break;
                    case "ca" when int.TryParse(val, out int ca):
                        entry.HasScreen = (ca & 1) != 0;
                        break;
                }
            }
        }
    }

    internal static bool TryReadName(ReadOnlySpan<byte> msg, ref int offset, out string name)
    {
        var sb = new StringBuilder();
        int pos = offset;
        int jumpedAfter = -1;
        for (int hops = 0; hops < 16;)
        {
            if ((uint)pos >= (uint)msg.Length) { name = ""; return false; }
            byte len = msg[pos];
            if (len == 0)
            {
                offset = jumpedAfter >= 0 ? jumpedAfter : pos + 1;
                name = sb.ToString();
                return true;
            }
            if ((len & 0xC0) == 0xC0)
            {
                if (pos + 1 >= msg.Length) { name = ""; return false; }
                int ptr = ((len & 0x3F) << 8) | msg[pos + 1];
                if (jumpedAfter < 0) jumpedAfter = pos + 2;
                pos = ptr;
                hops++;
                continue;
            }
            if ((len & 0xC0) != 0 || pos + 1 + len > msg.Length) { name = ""; return false; }
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.UTF8.GetString(msg.Slice(pos + 1, len)));
            pos += 1 + len;
        }
        name = "";
        return false;
    }

    private static IDisposable? SafeAcquire(Func<IDisposable?>? acquirer)
    {
        try { return acquirer?.Invoke(); }
        catch { return null; }
    }

    private static UdpClient? TryCreateMulticastListener()
    {
        try
        {
            var client = new UdpClient(AddressFamily.InterNetwork);
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            client.Client.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
            client.JoinMulticastGroup(MdnsAddress);
            return client;
        }
        catch
        {
            return null;
        }
    }

    private static async Task BroadcastAsync(List<UdpClient> sockets, byte[] packet, IPEndPoint target, CancellationToken ct)
    {
        foreach (var socket in sockets)
        {
            try { await socket.SendAsync(packet, target, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }
        }
    }

    private static async Task ReceiveLoopAsync(UdpClient socket, MdnsAccumulator acc, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(ct).ConfigureAwait(false);
                acc.AddPacket(result.Buffer);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    private static List<IPAddress> LocalIpv4Addresses()
    {
        var result = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                        result.Add(ua.Address);
                }
            }
        }
        catch
        {
            // Fall back to IPAddress.Any in caller
        }
        return result;
    }
}
