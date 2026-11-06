using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace NodeX.Core;

/// <summary>
/// Reads a node's IAX2 UDP port from AllStarLink's DNS records: the TXT record at
/// <c>&lt;node&gt;.nodes.allstarlink.org</c> ("NN=…", "IP=…", "PT=4569") and the SRV record at
/// <c>_iax._udp.&lt;node&gt;.nodes.allstarlink.org</c>. .NET has no SRV/TXT resolver, so this speaks a minimal
/// DNS query over UDP to the system's DNS servers (falling back to public resolvers).
/// </summary>
public static class DnsPortLookup
{
    private const ushort TypeTxt = 16;
    private const ushort TypeSrv = 33;

    /// <summary>How long to wait for each DNS server.</summary>
    public static TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(1.5);

    /// <summary>Returns the port from the node's TXT (PT=) or SRV record, or null when neither gives one.</summary>
    public static async Task<int?> LookupAsync(string nodeNumber, CancellationToken cancellationToken = default)
    {
        string host = $"{nodeNumber}.nodes.allstarlink.org";
        var servers = DnsServers();

        foreach (var (name, type) in new[] { ($"_iax._udp.{host}", TypeSrv), (host, TypeTxt) })
        {
            foreach (var server in servers)
            {
                byte[]? reply = await QueryAsync(server, name, type, cancellationToken);
                if (reply == null) continue;
                int? port = ParsePort(reply, type);
                if (port != null) return port;
                break;   // this server answered; no port in it — try the next record type
            }
        }
        return null;
    }

    private static List<IPAddress> DnsServers()
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var a in nic.GetIPProperties().DnsAddresses)
                {
                    if (a.AddressFamily == AddressFamily.InterNetwork && !list.Contains(a)) list.Add(a);
                }
            }
        }
        catch
        {
            // fall through to public resolvers
        }
        foreach (string s in new[] { "1.1.1.1", "8.8.8.8" })
        {
            var a = IPAddress.Parse(s);
            if (!list.Contains(a)) list.Add(a);
        }
        return list;
    }

    private static async Task<byte[]?> QueryAsync(IPAddress server, string name, ushort type, CancellationToken ct)
    {
        try
        {
            byte[] query = BuildQuery(name, type);
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            await udp.SendAsync(query, new IPEndPoint(server, 53), ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(QueryTimeout);
            while (true)
            {
                var result = await udp.ReceiveAsync(timeout.Token);
                if (result.Buffer.Length >= 12 && result.Buffer[0] == query[0] && result.Buffer[1] == query[1])
                    return result.Buffer;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    internal static byte[] BuildQuery(string name, ushort type)
    {
        var bytes = new List<byte>();
        ushort id = (ushort)Random.Shared.Next(1, 0xFFFF);
        bytes.Add((byte)(id >> 8)); bytes.Add((byte)id);
        bytes.AddRange(new byte[] { 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 });   // RD, QDCOUNT=1
        foreach (string label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] l = Encoding.ASCII.GetBytes(label);
            bytes.Add((byte)l.Length);
            bytes.AddRange(l);
        }
        bytes.Add(0);
        bytes.Add((byte)(type >> 8)); bytes.Add((byte)type);
        bytes.Add(0); bytes.Add(1);   // class IN
        return bytes.ToArray();
    }

    /// <summary>Extracts the port from a DNS reply holding SRV or TXT answers; null if absent.</summary>
    internal static int? ParsePort(byte[] msg, ushort wantedType)
    {
        try
        {
            if ((msg[3] & 0x0F) != 0) return null;   // RCODE != NOERROR
            int qd = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(4));
            int an = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(6));
            int pos = 12;
            for (int i = 0; i < qd; i++) pos = SkipName(msg, pos) + 4;

            for (int i = 0; i < an; i++)
            {
                pos = SkipName(msg, pos);
                ushort type = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(pos));
                int rdLen = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(pos + 8));
                int rd = pos + 10;
                pos = rd + rdLen;

                if (type != wantedType) continue;
                if (type == TypeSrv && rdLen >= 6)
                {
                    int port = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(rd + 4));
                    if (port is > 0 and <= 65535) return port;
                }
                else if (type == TypeTxt)
                {
                    int p = rd;
                    while (p < rd + rdLen)
                    {
                        int len = msg[p++];
                        string s = Encoding.ASCII.GetString(msg, p, Math.Min(len, rd + rdLen - p));
                        p += len;
                        // Entries may be separate strings or comma-joined: "PT=4569".
                        foreach (string part in s.Split(','))
                        {
                            string t = part.Trim();
                            if (t.StartsWith("PT=", StringComparison.OrdinalIgnoreCase)
                                && int.TryParse(t.AsSpan(3), out int port) && port is > 0 and <= 65535)
                                return port;
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            // Truncated or malformed reply.
        }
        return null;
    }

    private static int SkipName(byte[] msg, int pos)
    {
        while (true)
        {
            byte b = msg[pos];
            if (b == 0) return pos + 1;
            if ((b & 0xC0) == 0xC0) return pos + 2;   // compression pointer ends the name
            pos += b + 1;
        }
    }
}
