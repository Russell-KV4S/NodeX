using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace NodeX.Core;

/// <summary>
/// Classic IAX2 registration (RFC 5456 §6.? REGREQ / REGAUTH / REGACK) sent over UDP, as an alternative to the
/// HTTPS registration in <see cref="AslRegistrationService"/>. It registers from this object's own UDP socket,
/// so the registry records the public address and source port of that socket.
/// Sequence: REGREQ(username, refresh) → REGAUTH(challenge) → REGREQ(username, MD5(challenge+password), refresh)
/// → REGACK. Servers that require call tokens first answer with a CALLTOKEN frame, which is echoed.
/// </summary>
public sealed class Iax2Registrar : IAsyncDisposable, IDisposable
{
    public const string DefaultHost = "register.allstarlink.org";

    private readonly string _node;
    private readonly string _password;
    private readonly Func<CancellationToken, Task<IPEndPoint?>> _resolveServer;
    private readonly Socket _socket;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    public bool IsRegistered { get; private set; }
    public string LastStatusMessage { get; private set; } = "Not started";
    public IPAddress? PerceivedAddress { get; private set; }
    public int PerceivedPort { get; private set; }

    /// <summary>Refresh interval in seconds requested of (and possibly changed by) the server.</summary>
    public int RefreshSeconds { get; set; } = 60;

    /// <summary>How long to wait for each reply.</summary>
    public TimeSpan ReplyTimeout { get; set; } = TimeSpan.FromSeconds(3);

    public event Action<bool, string>? RegistrationStatusChanged;

    /// <param name="server">Registry endpoint; null resolves <see cref="DefaultHost"/>:4569.</param>
    public Iax2Registrar(string nodeNumber, string password, IPEndPoint? server = null)
    {
        _node = nodeNumber;
        _password = password;
        _resolveServer = async ct =>
        {
            if (server != null) return server;
            var addrs = await Dns.GetHostAddressesAsync(DefaultHost, ct);
            var v4 = Array.Find(addrs, a => a.AddressFamily == AddressFamily.InterNetwork);
            return v4 == null ? null : new IPEndPoint(v4, Iax2Protocol.DefaultPort);
        };
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
    }

    /// <summary>Local UDP port this registrar sends from.</summary>
    public int LocalPort => ((IPEndPoint)_socket.LocalEndPoint!).Port;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_loop != null) return Task.CompletedTask;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts != null) await _cts.CancelAsync();
        if (_loop != null)
        {
            try { await _loop; } catch (OperationCanceledException) { }
            _loop = null;
        }
        if (IsRegistered) Report(false, "Stopped");
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await RegisterOnceAsync(ct);
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(RefreshSeconds, 5, 3600)), ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Performs one full registration exchange.</summary>
    public async Task<bool> RegisterOnceAsync(CancellationToken ct = default)
    {
        try
        {
            var server = await _resolveServer(ct);
            if (server == null) return Report(false, "Could not resolve the registry host");

            ushort local = (ushort)Random.Shared.Next(1, 0x7FFF);
            byte[]? token = null;
            ushort remote = 0;
            byte oseq = 0, iseq = 0;
            byte[]? md5 = null;

            // At most: REGREQ, [CALLTOKEN → REGREQ], REGAUTH → REGREQ, REGACK/REGREJ.
            for (int step = 0; step < 4; step++)
            {
                await SendAsync(server, BuildRegReq(local, remote, oseq, iseq, token, md5), ct);

                var frame = await ReceiveAsync(server, local, ct);
                if (frame == null) return Report(false, "No reply from the registry");
                var f = frame.Value;

                if (f.FrameType != Iax2Protocol.FrameType.IaxControl) continue;
                switch ((Iax2Protocol.IaxControlSubclass)f.Subclass)
                {
                    case Iax2Protocol.IaxControlSubclass.CallToken:
                        foreach (var ie in Iax2Protocol.ParseInformationElements(f.Payload))
                            if (ie.Id == Iax2Protocol.InformationElementId.CallToken) token = ie.Data;
                        remote = 0; oseq = 0; iseq = 0;   // stateless challenge; start over
                        break;

                    case Iax2Protocol.IaxControlSubclass.RegAuth:
                    {
                        remote = f.SourceCallNumber;
                        iseq = (byte)(f.OSeqno + 1);
                        oseq = f.ISeqno;                  // our next sequence number
                        await SendAckAsync(server, local, remote, f, oseq, iseq, ct);

                        string challenge = "";
                        foreach (var ie in Iax2Protocol.ParseInformationElements(f.Payload))
                            if (ie.Id == Iax2Protocol.InformationElementId.Challenge)
                                challenge = Encoding.ASCII.GetString(ie.Data);
                        md5 = Encoding.ASCII.GetBytes(Convert.ToHexString(
                            MD5.HashData(Encoding.UTF8.GetBytes(challenge + _password))).ToLowerInvariant());
                        break;
                    }

                    case Iax2Protocol.IaxControlSubclass.RegAck:
                        await SendAckAsync(server, local, f.SourceCallNumber, f, (byte)(f.ISeqno), (byte)(f.OSeqno + 1), ct);
                        ApplyAck(f.Payload);
                        return Report(true, PerceivedAddress is null
                            ? "Registered (UDP)"
                            : $"Registered (UDP) as {PerceivedAddress}:{PerceivedPort}");

                    case Iax2Protocol.IaxControlSubclass.RegRej:
                        string why = "";
                        foreach (var ie in Iax2Protocol.ParseInformationElements(f.Payload))
                            if (ie.Id == Iax2Protocol.InformationElementId.Cause)
                                why = Encoding.ASCII.GetString(ie.Data);
                        return Report(false, string.IsNullOrWhiteSpace(why)
                            ? "UDP registration rejected" : $"UDP registration rejected: {why}");
                }
            }
            return Report(false, "UDP registration did not complete");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Report(false, $"UDP registration failed: {ex.Message}");
        }
    }

    private void ApplyAck(byte[] payload)
    {
        foreach (var ie in Iax2Protocol.ParseInformationElements(payload))
        {
            if (ie.Id == Iax2Protocol.InformationElementId.Refresh && ie.Data.Length == 2)
            {
                int r = BinaryPrimitives.ReadUInt16BigEndian(ie.Data);
                if (r > 0) RefreshSeconds = Math.Min(RefreshSeconds, r);
            }
            else if (ie.Id == Iax2Protocol.InformationElementId.ApparentAddr && ie.Data.Length >= 8)
            {
                // struct sockaddr_in: family (2, host order), port (2, network order), address (4)
                PerceivedPort = BinaryPrimitives.ReadUInt16BigEndian(ie.Data.AsSpan(2));
                PerceivedAddress = new IPAddress(ie.Data.AsSpan(4, 4));
            }
        }
    }

    private byte[] BuildRegReq(ushort local, ushort remote, byte oseq, byte iseq, byte[]? token, byte[]? md5)
    {
        var ies = new List<Iax2InformationElement>
        {
            Iax2InformationElement.String(Iax2Protocol.InformationElementId.Username, _node),
            Iax2InformationElement.UInt16(Iax2Protocol.InformationElementId.Refresh, (ushort)RefreshSeconds)
        };
        if (md5 != null) ies.Add(Iax2InformationElement.Raw(Iax2Protocol.InformationElementId.Md5Result, md5));
        ies.Add(Iax2InformationElement.Raw(Iax2Protocol.InformationElementId.CallToken, token ?? []));

        byte[] buf = new byte[512];
        int ieLen = Iax2Protocol.WriteInformationElements(buf.AsSpan(Iax2Protocol.FullHeaderSize), ies.ToArray());
        Iax2Protocol.WriteFullFrame(buf, local, remote, (uint)_clock.ElapsedMilliseconds, oseq, iseq,
            Iax2Protocol.FrameType.IaxControl, (byte)Iax2Protocol.IaxControlSubclass.RegReq,
            buf.AsSpan(Iax2Protocol.FullHeaderSize, ieLen));
        return buf[..(Iax2Protocol.FullHeaderSize + ieLen)];
    }

    private Task SendAsync(IPEndPoint server, byte[] packet, CancellationToken ct) =>
        _socket.SendToAsync(packet, SocketFlags.None, server, ct).AsTask();

    private async Task SendAckAsync(IPEndPoint server, ushort local, ushort remote, Iax2Frame to, byte oseq, byte iseq,
        CancellationToken ct)
    {
        byte[] ack = new byte[Iax2Protocol.FullHeaderSize];
        Iax2Protocol.WriteFullFrame(ack, local, remote, to.Timestamp, oseq, iseq,
            Iax2Protocol.FrameType.IaxControl, (byte)Iax2Protocol.IaxControlSubclass.Ack);
        await SendAsync(server, ack, ct);
    }

    private async Task<Iax2Frame?> ReceiveAsync(IPEndPoint server, ushort local, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ReplyTimeout);
        byte[] buf = new byte[1500];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            while (true)
            {
                var r = await _socket.ReceiveFromAsync(buf, SocketFlags.None, from, timeout.Token);
                if (!Iax2Protocol.TryParseFrame(buf.AsSpan(0, r.ReceivedBytes), out var frame)) continue;
                if (!frame.IsFullFrame || frame.DestCallNumber != local) continue;
                if (frame.FrameType == Iax2Protocol.FrameType.IaxControl
                    && frame.Subclass == (byte)Iax2Protocol.IaxControlSubclass.Ack) continue;
                return frame;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private bool Report(bool ok, string message)
    {
        IsRegistered = ok;
        LastStatusMessage = message;
        RegistrationStatusChanged?.Invoke(ok, message);
        return ok;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync();
        _cts?.Dispose();
        _socket.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
