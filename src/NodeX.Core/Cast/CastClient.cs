using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;

namespace NodeX.Core.Cast;

/// <summary>
/// TLS CASTV2 client: connects to port 8009 on a Chromecast, keeps heartbeats running, launches an app ID,
/// adjusts TV volume, sends custom/media messages, and surfaces TV-side disconnects.
/// </summary>
public sealed class CastClient : IAsyncDisposable
{
    private readonly Func<IPEndPoint, CancellationToken, Task<Stream>> _streamFactory;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Lock _stateLock = new();

    private Stream? _stream;
    private CancellationTokenSource? _sessionCts;
    private Task? _readTask;
    private Task? _heartbeatTask;
    private TaskCompletionSource<LaunchStatus>? _launchTcs;
    private string _targetAppId = "";
    private int _requestId;
    private long _lastRxTicks;
    private bool _disposed;
    private int _disconnectRaised;

    internal TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(5);
    internal TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(16);

    public CastClient(Func<IPEndPoint, CancellationToken, Task<Stream>>? streamFactory = null)
    {
        _streamFactory = streamFactory ?? OpenCastTlsStreamAsync;
    }

    public string? SessionId { get; private set; }
    public string? TransportId { get; private set; }
    public double VolumeLevel { get; private set; } = 1.0;
    public bool IsMuted { get; private set; }

    public event Action<double, bool>? VolumeChanged;
    public event Action<string>? Disconnected;

    private readonly record struct LaunchStatus(string SessionId, string TransportId);

    public async Task ConnectAndLaunchAsync(CastDevice device, string appId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (string.IsNullOrWhiteSpace(appId)) throw new ArgumentException("Cast App ID is required.", nameof(appId));

        await CloseTransportAsync().ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var endpoint = new IPEndPoint(device.Address, device.Port);
        var stream = await _streamFactory(endpoint, cancellationToken).ConfigureAwait(false);

        var sessionCts = new CancellationTokenSource();
        var launchTcs = new TaskCompletionSource<LaunchStatus>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_stateLock)
        {
            _stream = stream;
            _sessionCts = sessionCts;
            _launchTcs = launchTcs;
            _targetAppId = appId.Trim();
            SessionId = null;
            TransportId = null;
            Volatile.Write(ref _lastRxTicks, Environment.TickCount64);
            Volatile.Write(ref _disconnectRaised, 0);
        }

        _readTask = Task.Run(() => ReadLoopAsync(stream, sessionCts.Token));
        _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(sessionCts.Token));

        try
        {
            await SendJsonAsync(
                CastProtocol.NsConnection,
                CastProtocol.DefaultReceiverId,
                """{"type":"CONNECT"}""",
                cancellationToken).ConfigureAwait(false);

            int reqId = Interlocked.Increment(ref _requestId);
            string launchJson = JsonSerializer.Serialize(new { type = "LAUNCH", appId = appId.Trim(), requestId = reqId });
            await SendJsonAsync(CastProtocol.NsReceiver, CastProtocol.DefaultReceiverId, launchJson, cancellationToken).ConfigureAwait(false);

            var launched = await launchTcs.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            SessionId = launched.SessionId;
            TransportId = launched.TransportId;

            await SendJsonAsync(
                CastProtocol.NsConnection,
                launched.TransportId,
                """{"type":"CONNECT"}""",
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await CloseTransportAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task SendCustomJsonAsync(string ns, string json, CancellationToken cancellationToken = default)
    {
        string transport = TransportId ?? throw new InvalidOperationException("Cast app is not launched.");
        return SendJsonAsync(ns, transport, json, cancellationToken);
    }

    public Task SendMediaJsonAsync(string json, CancellationToken cancellationToken = default) =>
        SendCustomJsonAsync(CastProtocol.NsMedia, json, cancellationToken);

    public Task SetVolumeAsync(double level, CancellationToken cancellationToken = default)
    {
        double clamped = Math.Clamp(level, 0.0, 1.0);
        int reqId = Interlocked.Increment(ref _requestId);
        string json = JsonSerializer.Serialize(new { type = "SET_VOLUME", volume = new { level = clamped }, requestId = reqId });
        return SendJsonAsync(CastProtocol.NsReceiver, CastProtocol.DefaultReceiverId, json, cancellationToken);
    }

    public Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default)
    {
        int reqId = Interlocked.Increment(ref _requestId);
        string json = JsonSerializer.Serialize(new { type = "SET_VOLUME", volume = new { muted }, requestId = reqId });
        return SendJsonAsync(CastProtocol.NsReceiver, CastProtocol.DefaultReceiverId, json, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        string? sessionId = SessionId;
        string? transportId = TransportId;
        if (_stream != null)
        {
            try
            {
                if (!string.IsNullOrEmpty(sessionId))
                {
                    int reqId = Interlocked.Increment(ref _requestId);
                    string stopJson = JsonSerializer.Serialize(new { type = "STOP", sessionId, requestId = reqId });
                    await SendJsonAsync(CastProtocol.NsReceiver, CastProtocol.DefaultReceiverId, stopJson, cancellationToken).ConfigureAwait(false);
                }
                if (!string.IsNullOrEmpty(transportId))
                    await SendJsonAsync(CastProtocol.NsConnection, transportId, """{"type":"CLOSE"}""", cancellationToken).ConfigureAwait(false);
                await SendJsonAsync(CastProtocol.NsConnection, CastProtocol.DefaultReceiverId, """{"type":"CLOSE"}""", cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort goodbye before closing socket
            }
        }

        await CloseTransportAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }

    private async Task SendJsonAsync(string ns, string destinationId, string json, CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new InvalidOperationException("Cast transport is closed.");
        var msg = new CastMessage(CastProtocol.DefaultSenderId, destinationId, ns, CastPayloadType.String, json);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stream == null || _stream != stream) throw new InvalidOperationException("Cast transport is closed.");
            await CastProtocol.WriteFrameAsync(stream, msg, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(Stream stream, CancellationToken ct)
    {
        string reason = "Chromecast closed the connection.";
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var msg = await CastProtocol.ReadFrameAsync(stream, ct).ConfigureAwait(false);
                if (msg == null) break;

                Volatile.Write(ref _lastRxTicks, Environment.TickCount64);
                HandleIncoming(msg);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
        }

        _launchTcs?.TrySetException(new InvalidOperationException(reason));
        RaiseDisconnectedOnce(reason);
    }

    private void HandleIncoming(CastMessage msg)
    {
        if (msg.PayloadType != CastPayloadType.String || string.IsNullOrWhiteSpace(msg.PayloadUtf8))
            return;

        if (msg.Namespace == CastProtocol.NsHeartbeat)
        {
            if (msg.PayloadUtf8.Contains("\"PING\"", StringComparison.Ordinal))
            {
                _ = Task.Run(async () =>
                {
                    try { await SendJsonAsync(CastProtocol.NsHeartbeat, CastProtocol.DefaultReceiverId, """{"type":"PONG"}""", CancellationToken.None); }
                    catch { }
                });
            }
            return;
        }

        if (msg.Namespace == CastProtocol.NsConnection && msg.PayloadUtf8.Contains("\"CLOSE\"", StringComparison.Ordinal))
        {
            if (TransportId == null || string.Equals(msg.SourceId, TransportId, StringComparison.Ordinal))
                RaiseDisconnectedOnce("Chromecast closed the app session.");
            return;
        }

        if (msg.Namespace == CastProtocol.NsReceiver)
        {
            try
            {
                using var doc = JsonDocument.Parse(msg.PayloadUtf8);
                var root = doc.RootElement;
                string? type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
                if (type == "LAUNCH_ERROR")
                {
                    string errReason = root.TryGetProperty("reason", out var reasonEl) ? reasonEl.GetString() ?? "Launch refused" : "Launch refused";
                    _launchTcs?.TrySetException(new InvalidOperationException($"Chromecast rejected app '{_targetAppId}': {errReason}."));
                    return;
                }
                if (type != "RECEIVER_STATUS" || !root.TryGetProperty("status", out var status))
                    return;

                if (status.TryGetProperty("volume", out var vol))
                {
                    double level = vol.TryGetProperty("level", out var lvlEl) && lvlEl.TryGetDouble(out double d) ? Math.Clamp(d, 0.0, 1.0) : VolumeLevel;
                    bool muted = vol.TryGetProperty("muted", out var muteEl) && muteEl.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? muteEl.GetBoolean()
                        : IsMuted;
                    if (Math.Abs(level - VolumeLevel) > 0.001 || muted != IsMuted)
                    {
                        VolumeLevel = level;
                        IsMuted = muted;
                        VolumeChanged?.Invoke(level, muted);
                    }
                }

                if (status.TryGetProperty("applications", out var apps) && apps.ValueKind == JsonValueKind.Array)
                {
                    string? activeSid = SessionId;
                    bool sawOurSession = false;
                    foreach (var app in apps.EnumerateArray())
                    {
                        string? appId = app.TryGetProperty("appId", out var idEl) ? idEl.GetString() : null;
                        string? sid = app.TryGetProperty("sessionId", out var sidEl) ? sidEl.GetString() : null;
                        string? tid = app.TryGetProperty("transportId", out var tidEl) ? tidEl.GetString() : null;

                        if (activeSid != null && sid == activeSid) sawOurSession = true;
                        if (string.Equals(appId, _targetAppId, StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrEmpty(sid)
                            && !string.IsNullOrEmpty(tid))
                        {
                            _launchTcs?.TrySetResult(new LaunchStatus(sid, tid));
                        }
                    }

                    if (activeSid != null && !sawOurSession)
                        RaiseDisconnectedOnce("Chromecast switched to another app.");
                }
                else if (SessionId != null)
                {
                    RaiseDisconnectedOnce("Chromecast returned to idle.");
                }
            }
            catch (JsonException)
            {
                // Ignore malformed status JSON
            }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatInterval, ct).ConfigureAwait(false);
                if (Environment.TickCount64 - Volatile.Read(ref _lastRxTicks) > (long)HeartbeatTimeout.TotalMilliseconds)
                {
                    RaiseDisconnectedOnce("Chromecast stopped answering heartbeats.");
                    await CloseTransportAsync().ConfigureAwait(false);
                    return;
                }
                await SendJsonAsync(CastProtocol.NsHeartbeat, CastProtocol.DefaultReceiverId, """{"type":"PING"}""", ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RaiseDisconnectedOnce(ex.Message);
        }
    }

    private void RaiseDisconnectedOnce(string reason)
    {
        if (Interlocked.Exchange(ref _disconnectRaised, 1) == 0)
            Disconnected?.Invoke(reason);
    }

    private async Task CloseTransportAsync()
    {
        CancellationTokenSource? cts;
        Stream? stream;
        Task? readTask, hbTask;

        lock (_stateLock)
        {
            cts = _sessionCts;
            stream = _stream;
            readTask = _readTask;
            hbTask = _heartbeatTask;
            _sessionCts = null;
            _stream = null;
            _readTask = null;
            _heartbeatTask = null;
            SessionId = null;
            TransportId = null;
        }

        // Prevent redundant Disconnected events triggered by our own intentional close.
        Volatile.Write(ref _disconnectRaised, 1);

        cts?.Cancel();
        if (stream != null)
        {
            try { await stream.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        if (readTask != null) try { await readTask.ConfigureAwait(false); } catch { }
        if (hbTask != null) try { await hbTask.ConfigureAwait(false); } catch { }
        cts?.Dispose();
    }

    private static async Task<Stream> OpenCastTlsStreamAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        var tcp = new TcpClient();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));
            await tcp.ConnectAsync(endpoint.Address, endpoint.Port, timeoutCts.Token).ConfigureAwait(false);

            // Chromecast devices present a LAN device-specific self-signed/device-CA certificate whose SAN holds a
            // hardware UUID rather than the LAN IP, so standard hostname verification does not apply over LAN IP.
            var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = endpoint.Address.ToString()
            }, timeoutCts.Token).ConfigureAwait(false);
            return ssl;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }
}
