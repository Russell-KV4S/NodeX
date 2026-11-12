using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace NodeX.Core;

/// <summary>
/// Session state for an IAX2 call.
/// </summary>
public enum Iax2SessionState
{
    Disconnected,
    Resolving,
    Connecting,
    CallTokenHandshake,
    Authenticating,
    Connected,
    Disconnecting
}

/// <summary>Why a call ended; lets callers decide whether re-dialling makes sense.</summary>
public enum Iax2DisconnectReason
{
    /// <summary>Not disconnected, or never connected.</summary>
    None,
    /// <summary>We hung up (user action or shutdown).</summary>
    LocalRequest,
    /// <summary>The remote node hung up (for example its operator disconnected us).</summary>
    RemoteHangup,
    /// <summary>The remote node rejected the call.</summary>
    RemoteRejected,
    /// <summary>The remote node was busy or congested.</summary>
    RemoteBusy,
    /// <summary>The link died: no keep-alive replies, unacknowledged frames, or the peer forgot the call.</summary>
    LinkLost
}

/// <summary>
/// Manages an IAX2 UDP connection, Call ID allocation, sequence tracking,
/// Asterisk 20+ / ASL3 CallToken security handshakes, AllStarLink PTT keying
/// (AST_CONTROL_KEY / AST_CONTROL_UNKEY), keep-alives (PING/PONG), and 20ms audio Mini Frames.
/// </summary>
public sealed class Iax2Session : IAsyncDisposable, IDisposable, NodeX.Core.Audio.IVoiceChannel
{
    private readonly Socket _socket;
    private readonly ushort _localCallNumber;
    private ushort _remoteCallNumber;
    private byte _oSeqno;
    private byte _iSeqno;
    private readonly Stopwatch _sessionStopwatch = new();
    private readonly CancellationTokenSource _cts = new();

    private EndPoint? _remoteEndPoint;
    private Task? _receiveLoopTask;
    private Task? _keepAliveLoopTask;
    private bool _isDisposed;
    private byte[]? _callToken;
    private int _unansweredKeepAlives;

    /// <summary>
    /// Interval between in-call keep-alive PINGs. Keep well under typical carrier-NAT UDP timeouts
    /// (~30 s). Set before <see cref="ConnectAsync"/>.
    /// </summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Consecutive silent keep-alive intervals before the link is declared dead.</summary>
    public int MaxMissedKeepAlives { get; set; } = 3;

    /// <summary>
    /// Interval between app_rpt "L " link-list text frames. A real node sends one regularly, and some hubs
    /// (verified live against 27339, 2026-11-06) hang up a link that sends none for about 45 s even though it
    /// answers every PING. Zero turns it off. Set before <see cref="ConnectAsync"/>.
    /// </summary>
    public TimeSpan LinkListInterval { get; set; } = TimeSpan.FromSeconds(10);
    private Task? _linkListLoopTask;

    /// <summary>
    /// Send the app_rpt "!NEWKEY1!" text frame as soon as the call is accepted (what real nodes and DVSwitch do).
    /// Set before <see cref="ConnectAsync"/>.
    /// </summary>
    public bool SendNewKeyOnAccept { get; set; } = true;
    private bool _newKeySent;
    private bool _voiceFormatAnnounced;
    private uint _lastVoiceTimestamp;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Iax2RetransmitQueue _retransmit = new();
    private Task? _retransmitLoopTask;
    private const long MinRetransmitTimeoutMs = 100;

    /// <summary>
    /// Retransmission timeout used until a round-trip time has been measured; afterwards 2 x RTT,
    /// clamped to 100 ms..10 s (RFC 5456 7.2.1).
    /// </summary>
    public TimeSpan InitialRetransmitTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Retransmissions of an unacknowledged frame before the call is torn down (RFC default 4).</summary>
    public int MaxRetransmits { get; set; } = 4;

    public Iax2SessionState State { get; private set; } = Iax2SessionState.Disconnected;

    /// <summary>
    /// Test seam: puts a session that was never dialled into <paramref name="state"/> so code that needs an
    /// established call (the PTT controller) can be exercised without a network peer. With no remote endpoint
    /// nothing is ever sent.
    /// </summary>
    internal void SetStateForTests(Iax2SessionState state) => State = state;

    /// <summary>Why the call ended. Set before <see cref="StateChanged"/> reports Disconnected.</summary>
    public Iax2DisconnectReason DisconnectReason { get; private set; }
    public bool IsTransmitting { get; private set; }

    /// <inheritdoc/>
    public event Action<bool>? TransmittingChanged;
    public TimeSpan RoundTripTime { get; private set; }

    // Session credentials & configuration
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string LocalNodeNumber { get; set; } = string.Empty;
    public string RemoteNodeNumber { get; set; } = string.Empty;

    // Events
    public event Action<Iax2SessionState>? StateChanged;
    public event Action<ReadOnlyMemory<byte>>? AudioReceived;
    public event Action<string>? NodeMessageReceived;
    public event Action<string>? ErrorOccurred;
    public event Action<bool>? RemoteKeyedChanged;

    /// <summary>Fired when a PONG yields a fresh round-trip-time measurement.</summary>
    public event Action<TimeSpan>? RoundTripTimeUpdated;

    /// <summary>
    /// Fired for every received voice payload (u-law) with its 16-bit millisecond timestamp,
    /// suitable for feeding a jitter buffer.
    /// </summary>
    public event Action<ushort, ReadOnlyMemory<byte>>? VoiceFrameReceived;

    /// <summary>
    /// Diagnostic tap: raised for every datagram sent (<c>outbound</c> = true) or accepted from the peer,
    /// with a copy of the raw bytes. Used by the live-node validation tool; fires on I/O threads, so
    /// handlers must be fast and must not throw.
    /// </summary>
    public event Action<bool, ReadOnlyMemory<byte>>? PacketTrace;

    private ValueTask<int> TxAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        var trace = PacketTrace;
        if (trace != null)
        {
            try { trace(true, packet.ToArray()); } catch { /* diagnostics must never break the call */ }
        }
        return _socket.SendToAsync(packet, SocketFlags.None, _remoteEndPoint!, cancellationToken);
    }

    public Iax2Session()
    {
        // Allocate random 15-bit call number (1..32767)
        _localCallNumber = (ushort)Random.Shared.Next(1, 0x7FFF);

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveBufferSize = 64 * 1024,
            SendBufferSize = 64 * 1024
        };
        // Bind to ephemeral UDP port
        _socket.Bind(new IPEndPoint(IPAddress.Any, 0));

        // Windows turns an ICMP "port unreachable" for an earlier send into WSAECONNRESET on the next receive.
        // It tells us nothing the retransmit limit will not tell us better, so switch that behaviour off
        // (the receive loop also tolerates the error, for platforms where this call is not available).
        if (OperatingSystem.IsWindows())
        {
            try { _socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null); }
            catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException or NotSupportedException) { }
        }
    }

    /// <summary>Winsock SIO_UDP_CONNRESET control code.</summary>
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    /// <summary>Consecutive socket receive failures after which the link is declared lost (about 2 s of nothing but errors).</summary>
    private const int MaxConsecutiveReceiveErrors = 20;

    /// <summary>
    /// Initiates a call session to a target AllStarLink / IAX2 node endpoint.
    /// </summary>
    public async Task ConnectAsync(IPEndPoint targetEndPoint, CancellationToken cancellationToken = default)
    {
        lock (_stateGate)
        {
            if (State != Iax2SessionState.Disconnected)
                throw new InvalidOperationException($"Cannot connect while session is in {State} state.");
            if (_ended)
                throw new InvalidOperationException("An IAX2 session is single-use. Create a new one to dial again.");
        }

        _remoteEndPoint = targetEndPoint;
        _sessionStopwatch.Restart();
        _oSeqno = 0;
        _iSeqno = 0;
        _callToken = null;
        _remoteCallNumber = 0;
        _newKeySent = false;
        DisconnectReason = Iax2DisconnectReason.None;
        _retransmit.Clear();
        Volatile.Write(ref _unansweredKeepAlives, 0);
        SetState(Iax2SessionState.Connecting);

        _receiveLoopTask = Task.Run(ReceiveLoopAsync);
        _keepAliveLoopTask = Task.Run(KeepAliveLoopAsync);
        _retransmitLoopTask = Task.Run(RetransmitLoopAsync);
        _linkListLoopTask = LinkListInterval > TimeSpan.Zero ? Task.Run(LinkListLoopAsync) : null;

        await SendNewCallAsync(cancellationToken);
    }

    /// <summary>Sends an empty app_rpt link list ("L ") on a timer while connected (see <see cref="LinkListInterval"/>).</summary>
    private async Task LinkListLoopAsync()
    {
        using var timer = new PeriodicTimer(LinkListInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                if (State != Iax2SessionState.Connected) continue;

                // One beat that cannot be sent (no route for a moment) must not end the loop: a hub hangs up a
                // link that goes about 45 s without one. The frame is queued for retransmission anyway.
                try { await SendTextAsync("L "); }
                catch (SocketException) { }
            }
        }
        catch (OperationCanceledException)
        {
            // Session is closing.
        }
        catch (ObjectDisposedException)
        {
            // Session was closed under us.
        }
        catch (Exception ex)
        {
            ReportError($"Link list send failed: {ex.Message}");
        }
    }

    private async Task SendNewCallAsync(CancellationToken cancellationToken)
    {
        var ies = new List<Iax2InformationElement>
        {
            Iax2InformationElement.String(Iax2Protocol.InformationElementId.CalledNumber, RemoteNodeNumber),
            Iax2InformationElement.String(Iax2Protocol.InformationElementId.CallingNumber, LocalNodeNumber),
            Iax2InformationElement.String(Iax2Protocol.InformationElementId.CallingName, $"NodeX-{LocalNodeNumber}"),
            Iax2InformationElement.UInt32(Iax2Protocol.InformationElementId.Format, (uint)Iax2Protocol.CodecFormat.Ulaw),
            Iax2InformationElement.UInt32(Iax2Protocol.InformationElementId.Capability, (uint)Iax2Protocol.CodecFormat.Ulaw),
            Iax2InformationElement.UInt16(Iax2Protocol.InformationElementId.Version, 2)
        };

        if (!string.IsNullOrEmpty(Username))
        {
            ies.Add(Iax2InformationElement.String(Iax2Protocol.InformationElementId.Username, Username));
        }

        // The first NEW carries an empty CALLTOKEN IE to advertise call-token support (Asterisk 1.8+/ASL3);
        // the NEW re-sent after the challenge echoes the token the server issued.
        ies.Add(Iax2InformationElement.Raw(Iax2Protocol.InformationElementId.CallToken, _callToken ?? []));

        byte[] ieBuffer = new byte[1024];
        int ieLen = Iax2Protocol.WriteInformationElements(ieBuffer, ies.ToArray());

        await SendFullFrameAsync(
            Iax2Protocol.FrameType.IaxControl,
            (byte)Iax2Protocol.IaxControlSubclass.New,
            ieBuffer.AsMemory(0, ieLen),
            cancellationToken);
    }

    /// <summary>
    /// Disconnects from the active session and sends HANGUP.
    /// </summary>
    public async Task DisconnectAsync(string reason = "NodeX Disconnect")
    {
        // Only the caller that moves the call into Disconnecting hangs it up; a second caller, or a call that
        // has already ended, has nothing to do.
        if (State == Iax2SessionState.Disconnected || !SetState(Iax2SessionState.Disconnecting)) return;

        try
        {
            int seq = await SendHangupAsync(reason);
            if (seq >= 0) await WaitForHangupAckAsync((byte)seq);
        }
        catch
        {
            // Ignore socket errors during teardown
        }

        SetDisconnected(Iax2DisconnectReason.LocalRequest);
    }

    /// <summary>
    /// How long <see cref="DisconnectAsync"/> waits for the remote to acknowledge our HANGUP, re-sending it in the
    /// meantime, before closing anyway. HANGUP is the one frame that used to be sent exactly once: if it was lost
    /// the far node kept the link up until its own timeout. An acknowledgment normally arrives within one round
    /// trip, so this only costs time when the path is dead. Zero sends HANGUP once and does not wait (used when
    /// the network has just changed and the old path cannot work). Set before disconnecting.
    /// </summary>
    public TimeSpan HangupAckWait { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Gap between HANGUP re-sends while waiting for its acknowledgment.</summary>
    internal static readonly TimeSpan HangupResendInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Sends HANGUP with a CAUSE if the peer knows the call. Returns its sequence number, or -1 when nothing was sent.</summary>
    private async Task<int> SendHangupAsync(string reason)
    {
        if (_remoteEndPoint == null || _remoteCallNumber == 0) return -1;

        var ies = new List<Iax2InformationElement>
        {
            Iax2InformationElement.String(Iax2Protocol.InformationElementId.Cause, reason)
        };
        byte[] ieBuffer = new byte[256];
        int ieLen = Iax2Protocol.WriteInformationElements(ieBuffer, ies.ToArray());

        return await SendFullFrameAsync(
            Iax2Protocol.FrameType.IaxControl,
            (byte)Iax2Protocol.IaxControlSubclass.Hangup,
            ieBuffer.AsMemory(0, ieLen),
            CancellationToken.None);
    }

    private async Task WaitForHangupAckAsync(byte seq)
    {
        if (HangupAckWait <= TimeSpan.Zero) return;

        long deadline = _sessionStopwatch.ElapsedMilliseconds + (long)HangupAckWait.TotalMilliseconds;
        long nextResend = _sessionStopwatch.ElapsedMilliseconds + (long)HangupResendInterval.TotalMilliseconds;

        // The receive loop releases the frame when the peer's ACK (or an INVAL: it has already dropped the call) arrives.
        while (_retransmit.IsPending(seq))
        {
            long now = _sessionStopwatch.ElapsedMilliseconds;
            if (now >= deadline) return;

            if (now >= nextResend)
            {
                byte[]? again = _retransmit.RetransmitCopy(seq);
                if (again == null) return;
                await TxAsync(again, CancellationToken.None);
                nextResend = now + (long)HangupResendInterval.TotalMilliseconds;
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Records why the call ended, then moves to Disconnected (first reason wins). False if it had already ended.</summary>
    private bool SetDisconnected(Iax2DisconnectReason reason) => SetState(Iax2SessionState.Disconnected, reason);

    /// <summary>
    /// Controls Push-To-Talk (PTT) transmit status.
    /// Emits AST_CONTROL_KEY (12) when true and AST_CONTROL_UNKEY (13) when false.
    /// </summary>
    public async Task SetPttAsync(bool transmit)
    {
        if (State != Iax2SessionState.Connected || IsTransmitting == transmit) return;

        IsTransmitting = transmit;
        try { TransmittingChanged?.Invoke(transmit); } catch (Exception) { /* a listener must not break keying */ }
        if (transmit)
        {
            // Each new over begins with a full voice frame so the remote learns the format/timestamp.
            _voiceFormatAnnounced = false;
        }

        byte subclass = transmit
            ? (byte)Iax2Protocol.ControlSubclass.AstControlKey
            : (byte)Iax2Protocol.ControlSubclass.AstControlUnkey;

        await SendFullFrameAsync(
            Iax2Protocol.FrameType.Control,
            subclass,
            ReadOnlyMemory<byte>.Empty,
            _cts.Token);
    }

    /// <summary>
    /// Transmits a 20ms block of 8 kHz audio encoded as G.711 µ-law via an IAX2 Mini Frame.
    /// </summary>
    public async Task SendAudioFrameAsync(ReadOnlyMemory<byte> ulawAudio, CancellationToken cancellationToken = default)
    {
        if (State != Iax2SessionState.Connected || _remoteEndPoint == null || !IsTransmitting) return;

        uint ts = NextVoiceTimestamp((uint)_sessionStopwatch.ElapsedMilliseconds, ulawAudio.Length);

        // RFC 5456 8.1.1: mini frames carry only the low 16 bits of the timestamp, so a full
        // voice frame must be sent first and whenever the upper 16 bits change (wrap).
        if (!_voiceFormatAnnounced || (ts >> 16) != (_lastVoiceTimestamp >> 16))
        {
            _voiceFormatAnnounced = true;
            _lastVoiceTimestamp = ts;
            await SendFullFrameAsync(
                Iax2Protocol.FrameType.Voice,
                (byte)Iax2Protocol.CodecFormat.Ulaw,
                ulawAudio,
                cancellationToken,
                ts);
            return;
        }

        _lastVoiceTimestamp = ts;
        byte[] buffer = new byte[Iax2Protocol.MiniHeaderSize + ulawAudio.Length];
        Iax2Protocol.WriteMiniFrame(buffer, _localCallNumber, (ushort)(ts & 0xFFFF), ulawAudio.Span);
        await TxAsync(buffer, cancellationToken);
    }

    /// <summary>A gap this large between expected and actual send time restarts the voice timeline.</summary>
    internal const int VoiceTimestampResyncMs = 100;

    /// <summary>
    /// Voice is produced every 20 ms but reaches the socket whenever the capture thread happens to run, so
    /// stamping with the wall clock gives uneven spacing (measured live: 16 to 24 ms around a nominal 20).
    /// Stamp with the previous timestamp plus the frame's own duration instead, nudged by at most 1 ms per
    /// frame towards the wall clock so clock drift never accumulates; after a real gap (dropped frames, a
    /// stall) restart from the wall clock.
    /// </summary>
    internal uint NextVoiceTimestamp(uint wallClockMs, int ulawBytes) =>
        NextVoiceTimestamp(_voiceFormatAnnounced, _lastVoiceTimestamp, wallClockMs, ulawBytes);

    internal static uint NextVoiceTimestamp(bool overInProgress, uint lastTimestamp, uint wallClockMs, int ulawBytes)
    {
        if (!overInProgress) return wallClockMs;             // first frame of an over: full frame, fresh timeline

        uint frameMs = (uint)Math.Max(1, ulawBytes / 8);     // 8 samples per ms
        uint expected = unchecked(lastTimestamp + frameMs);
        int diff = unchecked((int)(wallClockMs - expected));
        if (Math.Abs(diff) > VoiceTimestampResyncMs) return wallClockMs;

        int nudge = diff > 5 ? 1 : diff < -5 ? -1 : 0;
        return unchecked((uint)((int)expected + nudge));
    }

    /// <summary>
    /// Transmits 16-bit linear PCM audio samples by encoding to G.711 µ-law before transmission.
    /// </summary>
    public Task SendLinearPcmFrameAsync(ReadOnlySpan<short> pcmSamples, CancellationToken cancellationToken = default)
    {
        byte[] ulaw = new byte[pcmSamples.Length];
        G711Ulaw.Encode(pcmSamples, ulaw);
        return SendAudioFrameAsync(ulaw, cancellationToken);
    }

    /// <summary>
    /// Sends a DTMF digit (e.g. for remote node linking commands).
    /// </summary>
    public async Task SendDtmfAsync(char digit)
    {
        if (State != Iax2SessionState.Connected) return;

        await SendFullFrameAsync(
            Iax2Protocol.FrameType.Dtmf,
            (byte)digit,
            ReadOnlyMemory<byte>.Empty,
            _cts.Token);
    }

    /// <summary>
    /// Sends an app_rpt link text frame (for example the "L " link list or "!NEWKEY1!"). app_rpt terminates the
    /// string with a NUL, so this does too.
    /// </summary>
    public async Task SendTextAsync(string text)
    {
        if (State != Iax2SessionState.Connected) return;

        byte[] bytes = new byte[Encoding.UTF8.GetByteCount(text) + 1];
        Encoding.UTF8.GetBytes(text, 0, text.Length, bytes, 0);
        await SendFullFrameAsync(Iax2Protocol.FrameType.Text, 0, bytes, _cts.Token);
    }

    /// <returns>The sequence number the frame consumed, or -1 when it was not sent or is not a sequenced frame.</returns>
    private async Task<int> SendFullFrameAsync(
        Iax2Protocol.FrameType frameType,
        byte subclass,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken,
        uint? timestamp = null)
    {
        if (_remoteEndPoint == null) return -1;

        uint ts = timestamp ?? (uint)_sessionStopwatch.ElapsedMilliseconds;
        byte[] buffer = new byte[Iax2Protocol.FullHeaderSize + payload.Length];
        int sequence = -1;

        // Serialize sequence-number allocation: audio, PTT and keep-alive run on different threads.
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            Iax2Protocol.WriteFullFrame(
                buffer,
                _localCallNumber,
                _remoteCallNumber,
                ts,
                _oSeqno,
                _iSeqno,
                frameType,
                subclass,
                payload.Span);

            if (Iax2Sequencing.IsReliable(frameType, subclass))
            {
                // Keep the frame until the peer acknowledges it (RFC 5456 section 7).
                _retransmit.Add(_oSeqno, buffer, _sessionStopwatch.ElapsedMilliseconds, RetransmitTimeoutMs());
                sequence = _oSeqno;
                _oSeqno++;
            }
        }
        finally
        {
            _sendLock.Release();
        }

        await TxAsync(buffer, cancellationToken);
        return sequence;
    }

    /// <summary>Retransmission timer for a newly sent frame: 2 x RTT once measured, else the initial value.</summary>
    private long RetransmitTimeoutMs()
    {
        double rttMs = RoundTripTime.TotalMilliseconds;
        long ms = rttMs > 0
            ? (long)(rttMs * 2)
            : (long)InitialRetransmitTimeout.TotalMilliseconds;
        return Math.Clamp(ms, MinRetransmitTimeoutMs, Iax2RetransmitQueue.MaxTimeoutMs);
    }

    private Task SendAckAsync(ushort destCallNumber, uint timestamp) =>
        SendUnsequencedAsync(Iax2Protocol.IaxControlSubclass.Ack, destCallNumber, timestamp);

    /// <summary>
    /// Sends ACK / VNAK-style frames: they carry the current OSeqno but do not consume it, and are
    /// never retransmitted.
    /// </summary>
    private async Task SendUnsequencedAsync(Iax2Protocol.IaxControlSubclass subclass, ushort destCallNumber, uint timestamp)
    {
        if (_remoteEndPoint == null) return;

        byte[] buffer = new byte[Iax2Protocol.FullHeaderSize];

        // Same lock as sequenced frames: the header must carry one consistent pair of sequence numbers even while
        // a PING, link-list text or KEY is being numbered on another thread.
        await _sendLock.WaitAsync(CancellationToken.None);
        try
        {
            Iax2Protocol.WriteFullFrame(
                buffer,
                _localCallNumber,
                destCallNumber,
                timestamp,
                _oSeqno,
                _iSeqno,
                Iax2Protocol.FrameType.IaxControl,
                (byte)subclass);
        }
        finally
        {
            _sendLock.Release();
        }

        await TxAsync(buffer, _cts.Token);
    }

    private async Task RetransmitLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(25));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                if (State == Iax2SessionState.Disconnected || _remoteEndPoint == null) continue;

                var due = _retransmit.CollectDue(
                    _sessionStopwatch.ElapsedMilliseconds, MaxRetransmits, out bool exceeded);

                foreach (byte[] packet in due)
                {
                    // A send that fails (no route for a moment) is just a lost packet; the retry limit decides.
                    try { await TxAsync(packet, _cts.Token); }
                    catch (SocketException) { }
                }

                if (exceeded && State is not (Iax2SessionState.Disconnecting or Iax2SessionState.Disconnected))
                {
                    // RFC 5456 section 7: no acknowledgment after the retry limit => call leg unusable.
                    ReportError(
                        $"Link lost: no acknowledgment after {MaxRetransmits} retransmissions.");
                    SetDisconnected(Iax2DisconnectReason.LinkLost);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }     // the session was closed under us
        catch (Exception ex)
        {
            // Retransmission must not stop silently: without it a dead call is never noticed.
            ReportError($"Link lost: {ex.Message}");
            SetDisconnected(Iax2DisconnectReason.LinkLost);
        }
    }

    /// <summary>
    /// Reads datagrams for the life of the session. Nothing that happens to one packet may end the loop: a socket
    /// error (for example an ICMP "port unreachable" surfacing as a receive failure) or a frame whose handling
    /// throws is reported and the loop carries on, because once it stops the call can never be answered,
    /// acknowledged or hung up. Only cancellation, disposal, or errors with nothing in between end it.
    /// </summary>
    private async Task ReceiveLoopAsync()
    {
        byte[] buffer = new byte[2048];
        EndPoint senderEp = new IPEndPoint(IPAddress.Any, 0);
        int consecutiveErrors = 0;

        while (!_cts.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, senderEp, _cts.Token);
                consecutiveErrors = 0;
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex)
            {
                if (_cts.IsCancellationRequested) return;
                if (++consecutiveErrors < MaxConsecutiveReceiveErrors)
                {
                    try { await Task.Delay(100, _cts.Token); }
                    catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { return; }
                    continue;
                }

                ReportError($"IAX2 receive error: {ex.Message}");
                SetDisconnected(Iax2DisconnectReason.LinkLost);
                return;
            }

            try
            {
                if (result.ReceivedBytes < Iax2Protocol.MiniHeaderSize) continue;

                // Outbound-linking design: only the node we dialed may talk to this socket. Anything
                // else (scans, stale peers, spoofed packets) must not redirect or disturb the call.
                if (!result.RemoteEndPoint.Equals(_remoteEndPoint)) continue;

                var packetSpan = buffer.AsSpan(0, result.ReceivedBytes);
                var rxTrace = PacketTrace;
                if (rxTrace != null)
                {
                    try { rxTrace(false, packetSpan.ToArray()); } catch { }
                }
                if (!Iax2Protocol.TryParseFrame(packetSpan, out var frame)) continue;

                if (frame.IsFullFrame)
                {
                    await HandleFullFrameAsync(frame);
                }
                else
                {
                    HandleMiniFrame(frame);
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception ex)
            {
                // Usually a subscriber's handler threw. The frame is lost; the call is not.
                ReportError($"IAX2 frame handling error: {ex.Message}");
            }
        }
    }

    /// <summary>Raises <see cref="ErrorOccurred"/>; a faulty listener must never take a session loop down.</summary>
    private void ReportError(string message)
    {
        try { ErrorOccurred?.Invoke(message); }
        catch (Exception) { /* nothing sensible left to do with it */ }
    }

    private async Task HandleFullFrameAsync(Iax2Frame frame)
    {
        bool isIax = frame.FrameType == Iax2Protocol.FrameType.IaxControl;
        bool isCallToken = isIax && frame.Subclass == (byte)Iax2Protocol.IaxControlSubclass.CallToken;

        // Frames must be addressed to our call (0 is allowed before the peer has learned our number)
        // and, once the call is established, come from the peer's call number.
        if (frame.DestCallNumber != 0 && frame.DestCallNumber != _localCallNumber) return;
        if (!isCallToken && _remoteCallNumber != 0 && frame.SourceCallNumber != _remoteCallNumber) return;

        // Any valid frame from the peer proves the link is alive.
        Volatile.Write(ref _unansweredKeepAlives, 0);

        if (isCallToken)
        {
            // Stateless challenge: carries no sequencing and acknowledges nothing. It is only meaningful before
            // the peer has answered our NEW; it skips the call-number check above, so once a call exists it must
            // not be allowed to reset the sequence numbers and re-send NEW.
            if (_remoteCallNumber == 0
                && State is Iax2SessionState.Connecting or Iax2SessionState.CallTokenHandshake)
            {
                await HandleIaxControlFrameAsync(Iax2Protocol.IaxControlSubclass.CallToken, frame.Payload, frame.Timestamp);
            }
            return;
        }

        // Every full frame carries the peer's next-expected sequence number, which releases
        // the frames we were holding for retransmission.
        _retransmit.AcknowledgeUpTo(frame.ISeqno);

        if (isIax)
        {
            switch ((Iax2Protocol.IaxControlSubclass)frame.Subclass)
            {
                case Iax2Protocol.IaxControlSubclass.Ack:
                case Iax2Protocol.IaxControlSubclass.TxCnt:
                case Iax2Protocol.IaxControlSubclass.TxAcc:
                    return; // unsequenced, nothing to acknowledge

                case Iax2Protocol.IaxControlSubclass.Vnak:
                    // Peer saw a gap: resend everything from its next-expected sequence number.
                    foreach (byte[] packet in _retransmit.RetransmitFrom(frame.ISeqno, _sessionStopwatch.ElapsedMilliseconds))
                    {
                        await TxAsync(packet, _cts.Token);
                    }
                    return;

                case Iax2Protocol.IaxControlSubclass.Inval:
                    if (State == Iax2SessionState.Disconnecting)
                    {
                        // We are hanging up and the peer has already dropped the call: as good as an ACK.
                        _retransmit.Clear();
                        return;
                    }
                    if (State == Iax2SessionState.Disconnected) return;

                    ReportError("Remote node does not recognise this call (INVAL).");
                    SetDisconnected(Iax2DisconnectReason.LinkLost);
                    return;
            }
        }

        switch (Iax2Sequencing.Classify(frame.OSeqno, _iSeqno))
        {
            case Iax2Sequencing.Order.Future:
                // Out of order: ignore the frame and ask the peer to resend from what we expect.
                await SendUnsequencedAsync(Iax2Protocol.IaxControlSubclass.Vnak, frame.SourceCallNumber, frame.Timestamp);
                return;

            case Iax2Sequencing.Order.Duplicate:
                // Peer retransmitted something we already processed (our ACK was lost): re-ACK only.
                await SendAckAsync(frame.SourceCallNumber, frame.Timestamp);
                return;
        }

        _remoteCallNumber = frame.SourceCallNumber;

        // Under the send lock, so a frame being numbered on another thread never sees half of this step.
        await _sendLock.WaitAsync(CancellationToken.None);
        try { _iSeqno++; }
        finally { _sendLock.Release(); }

        // Acknowledge received Full Frame
        await SendAckAsync(frame.SourceCallNumber, frame.Timestamp);

        // A call that is being hung up or has ended still acknowledges what the peer sends (so it stops
        // retransmitting), but nothing in those frames is acted on or passed to listeners.
        if (State is Iax2SessionState.Disconnecting or Iax2SessionState.Disconnected) return;

        switch (frame.FrameType)
        {
            case Iax2Protocol.FrameType.IaxControl:
                await HandleIaxControlFrameAsync((Iax2Protocol.IaxControlSubclass)frame.Subclass, frame.Payload, frame.Timestamp);
                break;

            case Iax2Protocol.FrameType.Control:
                HandleControlFrame((Iax2Protocol.ControlSubclass)frame.Subclass);
                break;

            case Iax2Protocol.FrameType.Voice:
                if (frame.Payload.Length > 0)
                {
                    AudioReceived?.Invoke(frame.Payload);
                    VoiceFrameReceived?.Invoke((ushort)(frame.Timestamp & 0xFFFF), frame.Payload);
                }
                break;

            case Iax2Protocol.FrameType.Text:
                if (frame.Payload.Length > 0)
                {
                    string msg = Encoding.UTF8.GetString(frame.Payload);
                    NodeMessageReceived?.Invoke(msg);
                }
                break;
        }
    }

    private async Task HandleIaxControlFrameAsync(Iax2Protocol.IaxControlSubclass subclass, byte[] payload, uint timestamp)
    {
        switch (subclass)
        {
            case Iax2Protocol.IaxControlSubclass.CallToken:
                // Asterisk 20+ / ASL3 CallToken handshake challenge
                SetState(Iax2SessionState.CallTokenHandshake);
                var ies = Iax2Protocol.ParseInformationElements(payload);
                foreach (var ie in ies)
                {
                    if (ie.Id == Iax2Protocol.InformationElementId.CallToken)
                    {
                        _callToken = ie.Data;
                        break;
                    }
                }
                // Resend NEW with the received CallToken. The challenge is stateless on the remote
                // side, so the re-sent NEW starts a fresh sequence space.
                _remoteCallNumber = 0;
                _retransmit.Clear(); // the superseded first NEW must not be retransmitted
                await _sendLock.WaitAsync(_cts.Token);
                try
                {
                    _oSeqno = 0;
                    _iSeqno = 0;
                }
                finally
                {
                    _sendLock.Release();
                }
                await SendNewCallAsync(_cts.Token);
                break;

            case Iax2Protocol.IaxControlSubclass.Accept:
                // Only a call that is still being set up can be accepted (a repeated ACCEPT changes nothing).
                if (!IsSettingUp(State) || !SetState(Iax2SessionState.Connected)) break;
                // Answer the app_rpt "new key" handshake straight away, as a real node does.
                if (SendNewKeyOnAccept && !_newKeySent)
                {
                    _newKeySent = true;
                    await SendTextAsync("!NEWKEY1!");
                }
                break;

            case Iax2Protocol.IaxControlSubclass.AuthReq:
                if (!IsSettingUp(State)) break;
                SetState(Iax2SessionState.Authenticating);
                await HandleAuthRequestAsync(payload);
                break;

            case Iax2Protocol.IaxControlSubclass.Poke:
            case Iax2Protocol.IaxControlSubclass.Ping:
                // Reply with PONG echoing the request timestamp (RFC 5456 6.7.2)
                await SendFullFrameAsync(
                    Iax2Protocol.FrameType.IaxControl,
                    (byte)Iax2Protocol.IaxControlSubclass.Pong,
                    payload,
                    _cts.Token,
                    timestamp);
                break;

            case Iax2Protocol.IaxControlSubclass.LagRq:
                // RFC 5456 6.7.4: MUST be answered with a LAGRP echoing the LAGRQ time-stamp.
                await SendFullFrameAsync(
                    Iax2Protocol.FrameType.IaxControl,
                    (byte)Iax2Protocol.IaxControlSubclass.LagRp,
                    ReadOnlyMemory<byte>.Empty,
                    _cts.Token,
                    timestamp);
                break;

            case Iax2Protocol.IaxControlSubclass.Pong:
                // PONG echoes our PING timestamp, so RTT = now - echoed timestamp.
                long rtt = _sessionStopwatch.ElapsedMilliseconds - timestamp;
                if (rtt >= 0 && rtt < 60_000)
                {
                    RoundTripTime = TimeSpan.FromMilliseconds(rtt);
                    RoundTripTimeUpdated?.Invoke(RoundTripTime);
                }
                break;

            case Iax2Protocol.IaxControlSubclass.Hangup:
                RemoteCause = DescribeCause(payload);
                SetDisconnected(Iax2DisconnectReason.RemoteHangup);
                break;

            case Iax2Protocol.IaxControlSubclass.Reject:
                RemoteCause = DescribeCause(payload);
                SetDisconnected(Iax2DisconnectReason.RemoteRejected);
                break;
        }
    }

    /// <summary>The reason text and code the remote put in its HANGUP/REJECT (empty when it gave none).</summary>
    public string RemoteCause { get; private set; } = "";

    internal static string DescribeCause(byte[] payload)
    {
        string text = "";
        int? code = null;
        try
        {
            foreach (var ie in Iax2Protocol.ParseInformationElements(payload))
            {
                if (ie.Id == Iax2Protocol.InformationElementId.Cause)
                    text = Encoding.UTF8.GetString(ie.Data).Trim();
                else if (ie.Id == Iax2Protocol.InformationElementId.CauseCode && ie.Data.Length > 0)
                    code = ie.Data[0];
            }
        }
        catch (Exception)
        {
            // A malformed IE block must never stop the hangup being processed.
        }

        if (text.Length == 0 && code == null) return "";
        return text.Length > 0 && code != null ? $"{text} (code {code})"
            : text.Length > 0 ? text
            : $"code {code}";
    }

    private void HandleControlFrame(Iax2Protocol.ControlSubclass subclass)
    {
        switch (subclass)
        {
            case Iax2Protocol.ControlSubclass.AstControlKey:
                RemoteKeyedChanged?.Invoke(true);
                break;

            case Iax2Protocol.ControlSubclass.AstControlUnkey:
                RemoteKeyedChanged?.Invoke(false);
                break;

            case Iax2Protocol.ControlSubclass.Answer:
                if (IsSettingUp(State)) SetState(Iax2SessionState.Connected);
                break;

            case Iax2Protocol.ControlSubclass.Hangup:
                SetDisconnected(Iax2DisconnectReason.RemoteHangup);
                break;

            case Iax2Protocol.ControlSubclass.Busy:
            case Iax2Protocol.ControlSubclass.Congestion:
                SetDisconnected(Iax2DisconnectReason.RemoteBusy);
                break;
        }
    }

    private void HandleMiniFrame(Iax2Frame frame)
    {
        // Mini frames carry the sender's call number; ignore anything not from our established peer, and anything
        // that arrives once the call is being hung up or has ended.
        if (_remoteCallNumber == 0 || frame.SourceCallNumber != _remoteCallNumber) return;
        if (State is Iax2SessionState.Disconnecting or Iax2SessionState.Disconnected) return;
        Volatile.Write(ref _unansweredKeepAlives, 0);

        if (frame.Payload.Length > 0)
        {
            AudioReceived?.Invoke(frame.Payload);
            VoiceFrameReceived?.Invoke((ushort)(frame.Timestamp & 0xFFFF), frame.Payload);
        }
    }

    private async Task HandleAuthRequestAsync(byte[] payload)
    {
        var ies = Iax2Protocol.ParseInformationElements(payload);
        string challenge = string.Empty;

        foreach (var ie in ies)
        {
            if (ie.Id == Iax2Protocol.InformationElementId.Challenge)
            {
                challenge = ie.AsString();
                break;
            }
        }

        // Only the MD5 challenge method is answered. The RFC's plaintext method would put the password itself
        // on the wire, so it is never used: with no challenge or no password the call is given up instead.
        if (string.IsNullOrEmpty(challenge) || string.IsNullOrEmpty(Password))
        {
            ReportError("The remote node asked for a password that this link does not have (node links log in without one).");
            try { await SendHangupAsync("Authentication not possible"); }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException) { }
            SetDisconnected(Iax2DisconnectReason.RemoteRejected);
            return;
        }

        string hashHex = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(challenge + Password)));

        var responseIes = new List<Iax2InformationElement>
        {
            Iax2InformationElement.String(Iax2Protocol.InformationElementId.Username, Username),
            Iax2InformationElement.String(Iax2Protocol.InformationElementId.Md5Result, hashHex)
        };

        byte[] buffer = new byte[256];
        int len = Iax2Protocol.WriteInformationElements(buffer, responseIes.ToArray());

        await SendFullFrameAsync(
            Iax2Protocol.FrameType.IaxControl,
            (byte)Iax2Protocol.IaxControlSubclass.AuthRep,
            buffer.AsMemory(0, len),
            _cts.Token);
    }

    private async Task KeepAliveLoopAsync()
    {
        using var timer = new PeriodicTimer(KeepAliveInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                if (State != Iax2SessionState.Connected) continue;

                // Watchdog: any inbound packet resets the counter, so this only trips when the
                // remote has been completely silent through several of our PINGs (dead link,
                // NAT mapping lost, or network handoff).
                if (Volatile.Read(ref _unansweredKeepAlives) >= MaxMissedKeepAlives)
                {
                    ReportError(
                        $"Link lost: no response to {MaxMissedKeepAlives} keep-alive PINGs.");
                    SetDisconnected(Iax2DisconnectReason.LinkLost);
                    continue;
                }

                Interlocked.Increment(ref _unansweredKeepAlives);

                // PING is the RFC 5456 in-call liveness probe: it keeps the NAT mapping open,
                // the remote answers with PONG, and the echoed timestamp yields an RTT sample.
                try
                {
                    await SendFullFrameAsync(
                        Iax2Protocol.FrameType.IaxControl,
                        (byte)Iax2Protocol.IaxControlSubclass.Ping,
                        ReadOnlyMemory<byte>.Empty,
                        _cts.Token);
                }
                catch (SocketException)
                {
                    // This PING did not get out (no route for a moment). It is queued for retransmission and the
                    // watchdog above keeps counting, so the loop must carry on rather than end here.
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }     // the session was closed under us
    }

    private readonly Lock _stateGate = new();

    /// <summary>
    /// Guarded by <see cref="_stateGate"/>. Once a dialled session reaches Disconnected it stays there: a late
    /// ACCEPT or ANSWER, or a second loop noticing the same failure, cannot bring it back or report it twice.
    /// </summary>
    private bool _ended;

    /// <summary>True for the states a call passes through before it is answered.</summary>
    private static bool IsSettingUp(Iax2SessionState state) =>
        state is Iax2SessionState.Connecting or Iax2SessionState.CallTokenHandshake or Iax2SessionState.Authenticating;

    /// <summary>
    /// Moves to <paramref name="newState"/> and raises <see cref="StateChanged"/>. The receive, keep-alive and
    /// retransmit loops and the caller's thread can all get here at once, so the transition itself is atomic;
    /// the event is raised outside the lock. Returns false when nothing changed.
    /// </summary>
    private bool SetState(Iax2SessionState newState, Iax2DisconnectReason reason = Iax2DisconnectReason.None)
    {
        lock (_stateGate)
        {
            if (_ended || State == newState) return false;

            if (newState == Iax2SessionState.Disconnected)
            {
                _ended = true;
                DisconnectReason = reason;      // set before StateChanged reports Disconnected
            }
            State = newState;
        }

        if (newState == Iax2SessionState.Disconnected) _retransmit.Clear(); // nothing left to resend

        // Each listener on its own: one that throws must not stop the others hearing that the call ended
        // (the runtime cleans the link up from this event).
        var handlers = StateChanged;
        if (handlers != null)
        {
            foreach (Action<Iax2SessionState> handler in handlers.GetInvocationList())
            {
                try { handler(newState); }
                catch (Exception ex) { ReportError($"A session state listener failed: {ex.Message}"); }
            }
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        // Hang up while the loops are still running: the receive loop has to be alive to see the HANGUP's
        // acknowledgment (see HangupAckWait). Then stop them.
        await DisconnectAsync();
        _cts.Cancel();

        // Events are raised on the receive/keep-alive threads. If a handler disposes the session
        // (directly or via an inline continuation) the loop cannot finish while we wait on it, so
        // bound the wait rather than hang forever.
        var loopTimeout = TimeSpan.FromSeconds(2);
        if (_receiveLoopTask != null) await Task.WhenAny(_receiveLoopTask, Task.Delay(loopTimeout));
        if (_keepAliveLoopTask != null) await Task.WhenAny(_keepAliveLoopTask, Task.Delay(loopTimeout));
        if (_linkListLoopTask != null) await Task.WhenAny(_linkListLoopTask, Task.Delay(loopTimeout));
        if (_retransmitLoopTask != null) await Task.WhenAny(_retransmitLoopTask, Task.Delay(loopTimeout));

        _socket.Dispose();
        _cts.Dispose();
        _sendLock.Dispose();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
