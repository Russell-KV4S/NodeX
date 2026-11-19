using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace NodeX.Core;

/// <summary>
/// Classic IAX2 registration (RFC 5456 REGREQ / REGAUTH / REGACK) with register.allstarlink.org over UDP, the
/// way an Asterisk node registers with <c>register =&gt; node:password@register.allstarlink.org</c>.
/// Sequence: REGREQ(username, refresh) → REGAUTH(challenge) → REGREQ(username, MD5(challenge+password), refresh)
/// → REGACK. Servers that require call tokens first answer with a CALLTOKEN frame, which is echoed.
/// <para>
/// Run on the shared <see cref="Iax2Endpoint"/> that the calls use, the registry records exactly the public
/// address and port the calls leave from. That is the point: AllStarLink nodes drop a link whose source IP differs
/// from the registered one, which is what happens on carrier NAT when registration (HTTPS, over TCP) and calls (UDP)
/// take different paths. The registry's REGACK reports that address (APPARENT ADDR), shown as
/// <see cref="PerceivedAddress"/>.
/// </para>
/// </summary>
public sealed class Iax2Registrar : INodeRegistration, IDisposable
{
    public const string DefaultHost = "register.allstarlink.org";

    private readonly string _node;
    private readonly string _password;
    private readonly Func<CancellationToken, Task<IPEndPoint?>> _resolveServer;
    private readonly Iax2Endpoint _endpoint;
    private readonly bool _ownsEndpoint;
    private readonly bool _endpointUser;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly SemaphoreSlim _exchangeLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;
    private int _noReplyFailures;
    private TaskCompletionSource _reschedule = NewSignal();

    /// <inheritdoc/>
    public string Transport => "IAX2";

    public bool IsRegistered { get; private set; }
    public DateTime? LastSuccessfulHeartbeat { get; private set; }
    public string LastStatusMessage { get; private set; } = "Not started";
    public IPAddress? PerceivedAddress { get; private set; }
    public int PerceivedPort { get; private set; }

    /// <summary>Refresh interval in seconds requested of (and possibly shortened by) the server.</summary>
    public int RefreshSeconds { get; set; } = 60;

    /// <inheritdoc/>
    public TimeSpan CurrentInterval => TimeSpan.FromSeconds(Math.Clamp(RefreshSeconds, 5, 3600));

    /// <summary>How long to wait for each reply before re-sending the request.</summary>
    public TimeSpan ReplyTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Re-sends of an unanswered request before the attempt counts as "no reply" (UDP loses packets).</summary>
    public int Retries { get; set; } = 2;

    /// <summary>First wait before retrying after no reply; doubles per failure, never longer than the interval.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// True when the last failed attempt was the registry saying no (REGREJ: wrong password, unknown node), as
    /// opposed to getting no answer at all.
    /// </summary>
    public bool LastFailureWasRejection { get; private set; }

    /// <summary>Attempts in a row that got no reply from the registry (reset by any reply).</summary>
    public int ConsecutiveNoReply => Volatile.Read(ref _noReplyFailures);

    /// <summary>While registered, this many attempts in a row may go unanswered before it is reported as not registered.</summary>
    public const int ToleratedNoReplies = 1;

    public event Action<bool, string>? RegistrationStatusChanged;

    /// <summary>Registers from a socket of its own (tests, the live-check tool).</summary>
    /// <param name="server">Registry endpoint; null resolves <see cref="DefaultHost"/>:4569.</param>
    public Iax2Registrar(string nodeNumber, string password, IPEndPoint? server = null)
        : this(nodeNumber, password, new Iax2Endpoint(), server, ownsEndpoint: true) { }

    /// <summary>Registers from <paramref name="endpoint"/>, the socket the calls use.</summary>
    public Iax2Registrar(string nodeNumber, string password, Iax2Endpoint endpoint, IPEndPoint? server = null)
        : this(nodeNumber, password, endpoint, server, ownsEndpoint: false) { }

    private Iax2Registrar(string nodeNumber, string password, Iax2Endpoint endpoint, IPEndPoint? server, bool ownsEndpoint)
    {
        _node = nodeNumber;
        _password = password;
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _ownsEndpoint = ownsEndpoint;
        _endpointUser = !ownsEndpoint && endpoint.AddUser();
        _resolveServer = async ct =>
        {
            if (server != null) return server;
            var addrs = await Dns.GetHostAddressesAsync(DefaultHost, ct);
            var v4 = Array.Find(addrs, a => a.AddressFamily == AddressFamily.InterNetwork);
            return v4 == null ? null : new IPEndPoint(v4, Iax2Protocol.DefaultPort);
        };
    }

    /// <summary>Local UDP port this registrar sends from.</summary>
    public int LocalPort => _endpoint.LocalPort;

    /// <summary>The socket this registrar sends from.</summary>
    public Iax2Endpoint Endpoint => _endpoint;

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

    /// <inheritdoc/>
    public async Task<bool> SendHeartbeatAsync(CancellationToken cancellationToken = default)
    {
        bool ok = await RegisterOnceAsync(cancellationToken);
        Interlocked.Exchange(ref _reschedule, NewSignal()).TrySetResult();
        return ok;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Next wait: the interval, or a short doubling retry after attempts that got no reply.</summary>
    internal TimeSpan NextDelay()
    {
        var interval = CurrentInterval;
        int failures = ConsecutiveNoReply;
        if (failures <= 0) return interval;
        double ticks = RetryDelay.Ticks * Math.Pow(2, Math.Min(failures - 1, 16));
        return ticks < interval.Ticks ? TimeSpan.FromTicks((long)ticks) : interval;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await RegisterOnceAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception) { /* only a listener can throw here; keep registering */ }

                // An out-of-band SendHeartbeatAsync restarts the wait (same scheme as the HTTPS registration).
                while (true)
                {
                    var rescheduled = Volatile.Read(ref _reschedule).Task;
                    using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var due = Task.Delay(NextDelay(), waitCts.Token);
                    var first = await Task.WhenAny(due, rescheduled);
                    waitCts.Cancel();
                    ct.ThrowIfCancellationRequested();
                    if (first == due) break;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Performs one full registration exchange.</summary>
    public async Task<bool> RegisterOnceAsync(CancellationToken ct = default)
    {
        // The background beat and an immediate re-registration must not run two exchanges at once.
        await _exchangeLock.WaitAsync(ct);
        ushort local = 0;
        IDisposable? subscription = null;
        try
        {
            var server = await _resolveServer(ct);
            if (server == null) return NoReply("Could not resolve the registry host");

            local = _endpoint.AllocateCallNumber();
            var inbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
            subscription = _endpoint.Subscribe(server, p => inbox.Writer.TryWrite(p));

            byte[]? token = null;
            ushort remote = 0;
            byte oseq = 0, iseq = 0;
            byte[]? md5 = null;

            // At most: REGREQ, [CALLTOKEN → REGREQ], REGAUTH → REGREQ, REGACK/REGREJ.
            for (int step = 0; step < 4; step++)
            {
                byte[] request = BuildRegReq(local, remote, oseq, iseq, token, md5);
                Iax2Frame? reply = null;
                for (int attempt = 0; attempt <= Retries && reply == null; attempt++)
                {
                    await SendAsync(server, request, ct);
                    reply = await ReceiveAsync(inbox, local, ct);
                }
                if (reply == null) return NoReply("No reply from the registry (IAX2)");
                var f = reply.Value;

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
                        await SendAckAsync(server, local, f.SourceCallNumber, f, f.ISeqno, (byte)(f.OSeqno + 1), ct);
                        ApplyAck(f.Payload);
                        Volatile.Write(ref _noReplyFailures, 0);
                        LastFailureWasRejection = false;
                        LastSuccessfulHeartbeat = DateTime.UtcNow;
                        return Report(true, PerceivedAddress is null
                            ? "Registered (IAX2)"
                            : $"Registered (IAX2) as {PerceivedAddress}:{PerceivedPort}");

                    case Iax2Protocol.IaxControlSubclass.RegRej:
                        string why = "";
                        foreach (var ie in Iax2Protocol.ParseInformationElements(f.Payload))
                            if (ie.Id == Iax2Protocol.InformationElementId.Cause)
                                why = Encoding.ASCII.GetString(ie.Data).TrimEnd('\0');
                        await SendAckAsync(server, local, f.SourceCallNumber, f, f.ISeqno, (byte)(f.OSeqno + 1), ct);
                        Volatile.Write(ref _noReplyFailures, 0);
                        LastFailureWasRejection = true;
                        return Report(false, string.IsNullOrWhiteSpace(why)
                            ? "Registration rejected (IAX2)" : $"Registration rejected (IAX2): {why}");
                }
            }
            return NoReply("IAX2 registration did not complete");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return NoReply($"IAX2 registration failed: {ex.Message}");
        }
        finally
        {
            subscription?.Dispose();
            if (local != 0) _endpoint.ReleaseCallNumber(local);
            _exchangeLock.Release();
        }
    }

    /// <summary>An attempt that got no verdict from the registry. One is tolerated while registered.</summary>
    private bool NoReply(string message)
    {
        int failures = Interlocked.Increment(ref _noReplyFailures);
        LastFailureWasRejection = false;
        if (IsRegistered && failures <= ToleratedNoReplies) return false;
        return Report(false, message);
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
        _endpoint.SendToAsync(packet, server, ct).AsTask();

    private async Task SendAckAsync(IPEndPoint server, ushort local, ushort remote, Iax2Frame to, byte oseq, byte iseq,
        CancellationToken ct)
    {
        byte[] ack = new byte[Iax2Protocol.FullHeaderSize];
        Iax2Protocol.WriteFullFrame(ack, local, remote, to.Timestamp, oseq, iseq,
            Iax2Protocol.FrameType.IaxControl, (byte)Iax2Protocol.IaxControlSubclass.Ack);
        await SendAsync(server, ack, ct);
    }

    private async Task<Iax2Frame?> ReceiveAsync(Channel<byte[]> inbox, ushort local, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ReplyTimeout);
        try
        {
            while (true)
            {
                byte[] packet = await inbox.Reader.ReadAsync(timeout.Token);
                if (!Iax2Protocol.TryParseFrame(packet, out var frame)) continue;
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
        if (_ownsEndpoint) _endpoint.Dispose();
        else if (_endpointUser) _endpoint.ReleaseUser();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
