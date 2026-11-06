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
/// (AST_CONTROL_KEY / AST_CONTROL_UNKEY), keep-alives (POKE/PONG), and 20ms audio Mini Frames.
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
    }

    /// <summary>
    /// Initiates a call session to a target AllStarLink / IAX2 node endpoint.
    /// </summary>
    public async Task ConnectAsync(IPEndPoint targetEndPoint, CancellationToken cancellationToken = default)
    {
        if (State != Iax2SessionState.Disconnected)
        {
            throw new InvalidOperationException($"Cannot connect while session is in {State} state.");
        }

        _remoteEndPoint = targetEndPoint;
        _sessionStopwatch.Restart();
        _oSeqno = 0;
        _iSeqno = 0;
        _callToken = null;
        _remoteCallNumber = 0;
        DisconnectReason = Iax2DisconnectReason.None;
        _retransmit.Clear();
        Volatile.Write(ref _unansweredKeepAlives, 0);
        SetState(Iax2SessionState.Connecting);

        _receiveLoopTask = Task.Run(ReceiveLoopAsync);
        _keepAliveLoopTask = Task.Run(KeepAliveLoopAsync);
        _retransmitLoopTask = Task.Run(RetransmitLoopAsync);

        await SendNewCallAsync(cancellationToken);
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
        if (State == Iax2SessionState.Disconnected) return;

        SetState(Iax2SessionState.Disconnecting);

        if (_remoteEndPoint != null && _remoteCallNumber != 0)
        {
            try
            {
                var ies = new List<Iax2InformationElement>
                {
                    Iax2InformationElement.String(Iax2Protocol.InformationElementId.Cause, reason)
                };
                byte[] ieBuffer = new byte[256];
                int ieLen = Iax2Protocol.WriteInformationElements(ieBuffer, ies.ToArray());

                await SendFullFrameAsync(
                    Iax2Protocol.FrameType.IaxControl,
                    (byte)Iax2Protocol.IaxControlSubclass.Hangup,
                    ieBuffer.AsMemory(0, ieLen),
                    CancellationToken.None);
            }
            catch
            {
                // Ignore socket errors during teardown
            }
        }

        SetDisconnected(Iax2DisconnectReason.LocalRequest);
    }

    /// <summary>Records why the call ended, then moves to Disconnected (first reason wins).</summary>
    private void SetDisconnected(Iax2DisconnectReason reason)
    {
        if (State != Iax2SessionState.Disconnected) DisconnectReason = reason;
        SetState(Iax2SessionState.Disconnected);
    }

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

    private async Task SendFullFrameAsync(
        Iax2Protocol.FrameType frameType,
        byte subclass,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken,
        uint? timestamp = null)
    {
        if (_remoteEndPoint == null) return;

        uint ts = timestamp ?? (uint)_sessionStopwatch.ElapsedMilliseconds;
        byte[] buffer = new byte[Iax2Protocol.FullHeaderSize + payload.Length];

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
                _oSeqno++;
            }
        }
        finally
        {
            _sendLock.Release();
        }

        await TxAsync(buffer, cancellationToken);
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
        Iax2Protocol.WriteFullFrame(
            buffer,
            _localCallNumber,
            destCallNumber,
            timestamp,
            _oSeqno,
            _iSeqno,
            Iax2Protocol.FrameType.IaxControl,
            (byte)subclass);

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
                    await TxAsync(packet, _cts.Token);
                }

                if (exceeded)
                {
                    // RFC 5456 section 7: no acknowledgment after the retry limit => call leg unusable.
                    ErrorOccurred?.Invoke(
                        $"Link lost: no acknowledgment after {MaxRetransmits} retransmissions.");
                    SetDisconnected(Iax2DisconnectReason.LinkLost);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReceiveLoopAsync()
    {
        byte[] buffer = new byte[2048];
        EndPoint senderEp = new IPEndPoint(IPAddress.Any, 0);

        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, senderEp, _cts.Token);
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
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"IAX2 receive error: {ex.Message}");
        }
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
            // Stateless challenge: carries no sequencing and acknowledges nothing.
            await HandleIaxControlFrameAsync(Iax2Protocol.IaxControlSubclass.CallToken, frame.Payload, frame.Timestamp);
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
                    ErrorOccurred?.Invoke("Remote node does not recognise this call (INVAL).");
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
        _iSeqno++;

        // Acknowledge received Full Frame
        await SendAckAsync(frame.SourceCallNumber, frame.Timestamp);

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
                SetState(Iax2SessionState.Connected);
                break;

            case Iax2Protocol.IaxControlSubclass.AuthReq:
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
                SetDisconnected(Iax2DisconnectReason.RemoteHangup);
                break;

            case Iax2Protocol.IaxControlSubclass.Reject:
                SetDisconnected(Iax2DisconnectReason.RemoteRejected);
                break;
        }
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
                SetState(Iax2SessionState.Connected);
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
        // Mini frames carry the sender's call number; ignore anything not from our established peer.
        if (_remoteCallNumber == 0 || frame.SourceCallNumber != _remoteCallNumber) return;
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

        string hashHex = string.Empty;
        if (!string.IsNullOrEmpty(challenge) && !string.IsNullOrEmpty(Password))
        {
            byte[] hashBytes = MD5.HashData(Encoding.UTF8.GetBytes(challenge + Password));
            hashHex = Convert.ToHexStringLower(hashBytes);
        }

        var responseIes = new List<Iax2InformationElement>
        {
            Iax2InformationElement.String(Iax2Protocol.InformationElementId.Username, Username)
        };

        if (!string.IsNullOrEmpty(hashHex))
        {
            responseIes.Add(Iax2InformationElement.String(Iax2Protocol.InformationElementId.Md5Result, hashHex));
        }
        else
        {
            responseIes.Add(Iax2InformationElement.String(Iax2Protocol.InformationElementId.Password, Password));
        }

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
                    ErrorOccurred?.Invoke(
                        $"Link lost: no response to {MaxMissedKeepAlives} keep-alive PINGs.");
                    SetDisconnected(Iax2DisconnectReason.LinkLost);
                    continue;
                }

                Interlocked.Increment(ref _unansweredKeepAlives);

                // PING is the RFC 5456 in-call liveness probe: it keeps the NAT mapping open,
                // the remote answers with PONG, and the echoed timestamp yields an RTT sample.
                await SendFullFrameAsync(
                    Iax2Protocol.FrameType.IaxControl,
                    (byte)Iax2Protocol.IaxControlSubclass.Ping,
                    ReadOnlyMemory<byte>.Empty,
                    _cts.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void SetState(Iax2SessionState newState)
    {
        if (State != newState)
        {
            State = newState;
            if (newState == Iax2SessionState.Disconnected) _retransmit.Clear(); // nothing left to resend
            StateChanged?.Invoke(newState);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _cts.Cancel();
        await DisconnectAsync();

        // Events are raised on the receive/keep-alive threads. If a handler disposes the session
        // (directly or via an inline continuation) the loop cannot finish while we wait on it, so
        // bound the wait rather than hang forever.
        var loopTimeout = TimeSpan.FromSeconds(2);
        if (_receiveLoopTask != null) await Task.WhenAny(_receiveLoopTask, Task.Delay(loopTimeout));
        if (_keepAliveLoopTask != null) await Task.WhenAny(_keepAliveLoopTask, Task.Delay(loopTimeout));
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
