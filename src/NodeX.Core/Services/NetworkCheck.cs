using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace NodeX.Core;

/// <summary>What the network check found about how the call socket appears on the internet.</summary>
public enum NatVerdict
{
    /// <summary>The check could not run (no STUN server answered).</summary>
    Unknown,
    /// <summary>The socket keeps one public address for every destination: registry and far nodes see the same IP.</summary>
    Consistent,
    /// <summary>
    /// The carrier gives the socket a different public address per destination, so a far node sees a different IP
    /// than the registry recorded and app_rpt drops the link ("does not match link IP").
    /// </summary>
    AddressPerDestination,
    /// <summary>The registry recorded a different address than the one the call socket shows.</summary>
    RegistryMismatch
}

/// <summary>Result of <see cref="NetworkCheck.RunAsync"/>.</summary>
/// <param name="Mapped">Public address and port each STUN server saw the call socket at (null = no answer).</param>
/// <param name="RegistryAddress">Address the registry recorded for this node, if known.</param>
/// <param name="RegistryTransport">How that registration was made ("IAX2" or "HTTPS").</param>
public sealed record NetworkCheckResult(
    NatVerdict Verdict,
    IReadOnlyList<(string Server, IPEndPoint? Mapped)> Mapped,
    IPAddress? RegistryAddress,
    string RegistryTransport)
{
    /// <summary>One line for the troubleshooting log.</summary>
    public string Describe()
    {
        string seen = string.Join(", ", Mapped.Select(m => $"{m.Server} sees {m.Mapped?.ToString() ?? "no answer"}"));
        string reg = RegistryAddress == null ? "registry address unknown" : $"registry ({RegistryTransport}) recorded {RegistryAddress}";
        return $"verdict={Verdict}; {seen}; {reg}";
    }

    /// <summary>What to tell the user, or null when there is nothing wrong to report.</summary>
    public string? UserWarning => Verdict switch
    {
        NatVerdict.AddressPerDestination =>
            "This network gives NodeX a different public address for each destination (carrier NAT). AllStar nodes " +
            "check that a link comes from the address this node registered from, so they may refuse links on this " +
            "network. Wi-Fi usually works.",
        NatVerdict.RegistryMismatch =>
            $"AllStarLink recorded this node at {RegistryAddress}, but calls leave from " +
            $"{Mapped.FirstOrDefault(m => m.Mapped != null).Mapped?.Address}. Nodes may refuse links until the two match.",
        _ => null
    };
}

/// <summary>
/// Asks public STUN servers (RFC 5389 Binding requests) what address and port the shared call socket appears at.
/// Two servers on different hosts show whether the carrier keeps one public address per socket (then registering
/// from the call socket makes far nodes and the registry agree) or changes it per destination (then nothing done
/// on the phone can make them agree). Sent from the same <see cref="Iax2Endpoint"/> as calls and registration.
/// </summary>
public static class NetworkCheck
{
    /// <summary>Two independent public STUN servers.</summary>
    public static readonly (string Host, int Port)[] DefaultServers =
    [
        ("stun.l.google.com", 19302),
        ("stun.cloudflare.com", 3478)
    ];

    public static async Task<NetworkCheckResult> RunAsync(
        Iax2Endpoint endpoint,
        IPAddress? registryAddress,
        string registryTransport,
        IReadOnlyList<(string Host, int Port)>? servers = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        servers ??= DefaultServers;
        var perTry = timeout ?? TimeSpan.FromSeconds(1.5);

        var lookups = servers.Select(async s =>
        {
            IPEndPoint? server = await ResolveAsync(s.Host, s.Port, cancellationToken);
            IPEndPoint? mapped = server == null ? null : await QueryAsync(endpoint, server, perTry, cancellationToken);
            return ($"{s.Host}:{s.Port}", mapped);
        });
        var mappedList = await Task.WhenAll(lookups);

        return new NetworkCheckResult(Judge(mappedList.Select(m => m.mapped).ToArray(), registryAddress, registryTransport),
            mappedList, registryAddress, registryTransport);
    }

    /// <summary>
    /// The verdict from what the STUN servers and the registry saw. app_rpt compares only the IP (not the port),
    /// so a port that differs per destination is harmless.
    /// </summary>
    internal static NatVerdict Judge(IReadOnlyList<IPEndPoint?> mapped, IPAddress? registryAddress, string registryTransport)
    {
        var addresses = mapped.Where(m => m != null).Select(m => m!.Address).ToArray();
        if (addresses.Length == 0) return NatVerdict.Unknown;
        if (addresses.Distinct().Count() > 1) return NatVerdict.AddressPerDestination;

        // Only an IAX2 registration was sent from this socket; an HTTPS one going out from a different address is
        // the very problem this check exists to show, so it counts as a mismatch too.
        if (registryAddress != null && !registryAddress.Equals(addresses[0])) return NatVerdict.RegistryMismatch;
        return NatVerdict.Consistent;
    }

    private static async Task<IPEndPoint?> ResolveAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            if (IPAddress.TryParse(host, out var literal)) return new IPEndPoint(literal, port);
            var addrs = await Dns.GetHostAddressesAsync(host, ct);
            var v4 = Array.Find(addrs, a => a.AddressFamily == AddressFamily.InterNetwork);
            return v4 == null ? null : new IPEndPoint(v4, port);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException) { return null; }
    }

    /// <summary>One STUN Binding transaction (sent up to three times); null when there is no usable answer.</summary>
    public static async Task<IPEndPoint?> QueryAsync(Iax2Endpoint endpoint, IPEndPoint server, TimeSpan perTry, CancellationToken ct)
    {
        byte[] txn = RandomNumberGenerator.GetBytes(12);
        byte[] request = BuildBindingRequest(txn);
        var inbox = Channel.CreateUnbounded<byte[]>();
        IDisposable subscription;
        try { subscription = endpoint.Subscribe(server, p => inbox.Writer.TryWrite(p)); }
        catch (ObjectDisposedException) { return null; }

        using (subscription)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try { await endpoint.SendToAsync(request, server, ct); }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { return null; }

                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(perTry);
                try
                {
                    while (true)
                    {
                        byte[] reply = await inbox.Reader.ReadAsync(wait.Token);
                        if (TryParseBindingResponse(reply, txn, out var mapped)) return mapped;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* try again */ }
            }
        }
        return null;
    }

    private const uint MagicCookie = 0x2112A442;

    internal static byte[] BuildBindingRequest(byte[] transactionId)
    {
        byte[] msg = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(msg, 0x0001);              // Binding Request
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(2), 0);         // no attributes
        BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(4), MagicCookie);
        transactionId.CopyTo(msg, 8);
        return msg;
    }

    /// <summary>Reads XOR-MAPPED-ADDRESS (or plain MAPPED-ADDRESS) from a Binding success response for our transaction.</summary>
    internal static bool TryParseBindingResponse(ReadOnlySpan<byte> msg, ReadOnlySpan<byte> transactionId, out IPEndPoint? mapped)
    {
        mapped = null;
        if (msg.Length < 20) return false;
        if (BinaryPrimitives.ReadUInt16BigEndian(msg) != 0x0101) return false;           // Binding Success Response
        if (BinaryPrimitives.ReadUInt32BigEndian(msg[4..]) != MagicCookie) return false;
        if (!msg.Slice(8, 12).SequenceEqual(transactionId)) return false;

        int length = BinaryPrimitives.ReadUInt16BigEndian(msg[2..]);
        var attrs = msg.Slice(20, Math.Min(length, msg.Length - 20));
        IPEndPoint? plain = null;
        while (attrs.Length >= 4)
        {
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(attrs);
            int len = BinaryPrimitives.ReadUInt16BigEndian(attrs[2..]);
            if (attrs.Length < 4 + len) break;
            var value = attrs.Slice(4, len);

            if (len >= 8 && value[1] == 0x01)                                    // IPv4
            {
                ushort port = BinaryPrimitives.ReadUInt16BigEndian(value[2..]);
                uint addr = BinaryPrimitives.ReadUInt32BigEndian(value[4..]);
                if (type == 0x0020)                                              // XOR-MAPPED-ADDRESS
                {
                    port ^= (ushort)(MagicCookie >> 16);
                    addr ^= MagicCookie;
                    byte[] b = new byte[4];
                    BinaryPrimitives.WriteUInt32BigEndian(b, addr);
                    mapped = new IPEndPoint(new IPAddress(b), port);
                    return true;
                }
                if (type == 0x0001)                                              // MAPPED-ADDRESS
                {
                    byte[] b = new byte[4];
                    BinaryPrimitives.WriteUInt32BigEndian(b, addr);
                    plain = new IPEndPoint(new IPAddress(b), port);
                }
            }
            int padded = (len + 3) & ~3;
            if (attrs.Length < 4 + padded) break;
            attrs = attrs[(4 + padded)..];
        }

        mapped = plain;
        return plain != null;
    }
}
