using System.Diagnostics;
using System.Text.Json;
using NodeX.Core.Audio;
using NodeX.Core.Diagnostics;
using NodeX.Core.Settings;

namespace NodeX.Core.Cast;

/// <summary>
/// Manages a Chromecast screen + audio session across <see cref="NodeRuntime"/> links on Desktop and Android.
/// Supports both NodeX's Custom Web Receiver TV dashboard (via configured <see cref="NodeSettings.CastAppId"/>)
/// and Chromecast's built-in Default Media Receiver (<c>CC1AD845</c>) fallback.
/// </summary>
public sealed class CastSessionManager : IAsyncDisposable
{
    private readonly NodeRuntime _runtime;
    private readonly Func<CastClient> _clientFactory;
    private readonly Func<byte[]?> _iconProvider;
    private readonly Func<IDisposable?>? _multicastLockAcquirer;
    private readonly CastAudioMixer _mixer = new();
    private readonly Dictionary<NodeLink, Action<ReadOnlyMemory<short>>> _audioHooks = [];
    private readonly Lock _gate = new();

    private CastClient? _client;
    private CastLanAudioServer? _lanAudio;
    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;
    private bool _disposed;

    public CastSessionManager(
        NodeRuntime runtime,
        Func<CastClient>? clientFactory = null,
        Func<byte[]?>? iconProvider = null,
        Func<IDisposable?>? multicastLockAcquirer = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _clientFactory = clientFactory ?? (() => new CastClient());
        _iconProvider = iconProvider ?? (static () => null);
        _multicastLockAcquirer = multicastLockAcquirer;
    }

    public bool IsCasting { get; private set; }
    public bool IsConnecting { get; private set; }
    public CastDevice? ActiveDevice { get; private set; }
    public bool IsCustomDashboardMode { get; private set; }
    public double VolumeLevel { get; private set; } = 1.0;
    public bool IsTvMuted { get; private set; }
    public bool MuteLocalSpeaker { get; private set; } = true;
    public string StatusText { get; private set; } = "Not casting";

    public event Action? StateChanged;
    public event Action<string>? Notice;

    public Task<IReadOnlyList<CastDevice>> DiscoverAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        CastDiscovery.DiscoverAsync(timeout, _multicastLockAcquirer, cancellationToken);

    public async Task StartCastAsync(CastDevice device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await StopCastAsync(silent: true).ConfigureAwait(false);

        string customAppId = (_runtime.Config.Settings.CastAppId ?? "").Trim().ToUpperInvariant();
        bool useCustom = NodeSettingsValidator.IsValidCastAppId(customAppId);
        string appId = useCustom ? customAppId : CastProtocol.DefaultMediaReceiverAppId;

        MuteLocalSpeaker = _runtime.Config.Settings.CastMuteLocalSpeaker;
        IsConnecting = true;
        ActiveDevice = device;
        IsCustomDashboardMode = useCustom;
        StatusText = $"Connecting to {device.FriendlyName}…";
        StateChanged?.Invoke();

        var client = _clientFactory();
        CastLanAudioServer? lanServer = null;
        try
        {
            if (!useCustom)
                lanServer = new CastLanAudioServer(device.Address, _iconProvider());

            client.VolumeChanged += OnClientVolumeChanged;
            client.Disconnected += OnClientDisconnected;
            await client.ConnectAndLaunchAsync(device, appId, cancellationToken).ConfigureAwait(false);

            var pumpCts = new CancellationTokenSource();
            lock (_gate)
            {
                _client = client;
                _lanAudio = lanServer;
                _pumpCts = pumpCts;
            }

            AttachRuntime(pumpCts.Token);

            if (!useCustom && lanServer != null)
            {
                await SendDefaultMediaLoadAsync(client, lanServer, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await SendStateSnapshotAsync(client, cancellationToken).ConfigureAwait(false);
            }

            IsConnecting = false;
            IsCasting = true;
            VolumeLevel = client.VolumeLevel;
            IsTvMuted = client.IsMuted;
            StatusText = useCustom
                ? $"Casting TV dashboard & audio to {device.FriendlyName}"
                : $"Casting audio to {device.FriendlyName} (Default Media Receiver)";

            _pumpTask = Task.Run(() => PumpLoopAsync(client, lanServer, useCustom, pumpCts.Token));
            DiagnosticLog.Default.Info("cast", StatusText);
            Notice?.Invoke(StatusText + ".");
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            IsConnecting = false;
            IsCasting = false;
            ActiveDevice = null;
            StatusText = ex.Message;
            DetachRuntime();
            client.VolumeChanged -= OnClientVolumeChanged;
            client.Disconnected -= OnClientDisconnected;
            await client.DisposeAsync().ConfigureAwait(false);
            if (lanServer != null) await lanServer.DisposeAsync().ConfigureAwait(false);
            StateChanged?.Invoke();
            throw;
        }
    }

    public void SetMuteLocalSpeaker(bool mute)
    {
        MuteLocalSpeaker = mute;
        foreach (var link in _runtime.Links)
            link.Audio.MuteLocalOutput = IsCasting && mute;
        StateChanged?.Invoke();
    }

    public async Task SetVolumeAsync(double level, CancellationToken cancellationToken = default)
    {
        double clamped = Math.Clamp(level, 0.0, 1.0);
        VolumeLevel = clamped;
        if (_client is { } c)
            await c.SetVolumeAsync(clamped, cancellationToken).ConfigureAwait(false);
        StateChanged?.Invoke();
    }

    public async Task SetTvMutedAsync(bool muted, CancellationToken cancellationToken = default)
    {
        IsTvMuted = muted;
        if (_client is { } c)
            await c.SetMutedAsync(muted, cancellationToken).ConfigureAwait(false);
        StateChanged?.Invoke();
    }

    public async Task StopCastAsync(bool silent = false)
    {
        CastClient? client;
        CastLanAudioServer? lan;
        CancellationTokenSource? cts;
        Task? pump;

        lock (_gate)
        {
            client = _client;
            lan = _lanAudio;
            cts = _pumpCts;
            pump = _pumpTask;
            _client = null;
            _lanAudio = null;
            _pumpCts = null;
            _pumpTask = null;
        }

        DetachRuntime();
        foreach (var link in _runtime.Links)
            link.Audio.MuteLocalOutput = false;

        cts?.Cancel();
        if (pump != null) try { await pump.ConfigureAwait(false); } catch { }
        cts?.Dispose();

        if (client != null)
        {
            client.VolumeChanged -= OnClientVolumeChanged;
            client.Disconnected -= OnClientDisconnected;
            await client.DisposeAsync().ConfigureAwait(false);
        }
        if (lan != null)
            await lan.DisposeAsync().ConfigureAwait(false);

        bool wasActive = IsCasting || IsConnecting;
        IsCasting = false;
        IsConnecting = false;
        ActiveDevice = null;
        if (!silent)
        {
            StatusText = "Not casting";
            if (wasActive) Notice?.Invoke("Stopped casting.");
        }
        StateChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopCastAsync(silent: true).ConfigureAwait(false);
    }

    private void AttachRuntime(CancellationToken ct)
    {
        _runtime.LinkAdded += OnLinkAdded;
        _runtime.LinkRemoved += OnLinkRemoved;
        _runtime.ConnectionsChanged += RequestStateSync;
        _runtime.RegistrationChanged += RequestStateSync;
        _runtime.LocalStationChanged += RequestStateSync;
        _runtime.LinkActivityChanged += OnActivityChanged;
        foreach (var link in _runtime.Links)
            HookLink(link);
        _ = ct;
    }

    private void DetachRuntime()
    {
        _runtime.LinkAdded -= OnLinkAdded;
        _runtime.LinkRemoved -= OnLinkRemoved;
        _runtime.ConnectionsChanged -= RequestStateSync;
        _runtime.RegistrationChanged -= RequestStateSync;
        _runtime.LocalStationChanged -= RequestStateSync;
        _runtime.LinkActivityChanged -= OnActivityChanged;

        foreach (var (link, handler) in _audioHooks.ToArray())
        {
            link.Audio.RxAudioProcessed -= handler;
            link.Audio.MuteLocalOutput = false;
        }
        _audioHooks.Clear();
        _mixer.Clear();
    }

    private volatile bool _stateDirty = true;

    private void OnLinkAdded(NodeLink link)
    {
        HookLink(link);
        _stateDirty = true;
    }

    private void OnLinkRemoved(NodeLink link)
    {
        if (_audioHooks.Remove(link, out var handler))
            link.Audio.RxAudioProcessed -= handler;
        link.Audio.MuteLocalOutput = false;
        _mixer.Detach(link.Target.NodeNumber);
        _stateDirty = true;
    }

    private void RequestStateSync() => _stateDirty = true;
    private void OnActivityChanged(string _) => _stateDirty = true;

    private void HookLink(NodeLink link)
    {
        if (_audioHooks.ContainsKey(link)) return;
        string node = link.Target.NodeNumber;
        Action<ReadOnlyMemory<short>> handler = pcm => _mixer.PushFrame(node, pcm.Span);
        _audioHooks[link] = handler;
        link.Audio.RxAudioProcessed += handler;
        link.Audio.MuteLocalOutput = MuteLocalSpeaker;
    }

    private void OnClientVolumeChanged(double level, bool muted)
    {
        VolumeLevel = level;
        IsTvMuted = muted;
        StateChanged?.Invoke();
    }

    private void OnClientDisconnected(string reason)
    {
        _ = Task.Run(async () =>
        {
            await StopCastAsync(silent: true).ConfigureAwait(false);
            StatusText = reason;
            Notice?.Invoke($"Chromecast disconnected: {reason}");
            StateChanged?.Invoke();
        });
    }

    private async Task SendDefaultMediaLoadAsync(CastClient client, CastLanAudioServer server, CancellationToken ct)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["metadataType"] = 3,
            ["title"] = $"Station: {_runtime.LocalStationDisplay}",
            ["subtitle"] = BuildSummarySubtitle()
        };
        if (server.HasIcon)
            metadata["images"] = new[] { new { url = server.IconUrl } };

        string payload = JsonSerializer.Serialize(new
        {
            type = "LOAD",
            requestId = 1,
            autoplay = true,
            media = new
            {
                contentId = server.LiveWavUrl,
                streamType = "LIVE",
                contentType = "audio/wav",
                metadata
            }
        });
        await client.SendMediaJsonAsync(payload, ct).ConfigureAwait(false);
    }

    private string BuildSummarySubtitle()
    {
        var conns = _runtime.Connections;
        if (conns.Count == 0) return "Not linked to any node";
        string nodes = string.Join(", ", conns.Take(4).Select(c => c.Node));
        return conns.Count > 4 ? $"Linked to {nodes} (+{conns.Count - 4} more)" : $"Linked to {nodes}";
    }

    private async Task PumpLoopAsync(CastClient client, CastLanAudioServer? lanServer, bool useCustom, CancellationToken ct)
    {
        short[] mixed = new short[AudioFormat.SamplesPerFrame];
        byte[] ulawBatch = new byte[AudioFormat.SamplesPerFrame * 3];
        int batchFrames = 0;
        int silentTailRemaining = 0;
        long seq = 0;
        long lastStateSentAt = Environment.TickCount64;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(AudioFormat.FrameMilliseconds));

        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                bool hasAudio = _mixer.MixNextFrame(mixed);

                if (useCustom)
                {
                    if (hasAudio) silentTailRemaining = 10; // 200 ms tail before pausing audio messages
                    if (hasAudio || silentTailRemaining > 0)
                    {
                        if (!hasAudio) silentTailRemaining--;
                        G711Ulaw.Encode(mixed, ulawBatch.AsSpan(batchFrames * AudioFormat.SamplesPerFrame, AudioFormat.SamplesPerFrame));
                        batchFrames++;
                        if (batchFrames == 3 || (!hasAudio && silentTailRemaining == 0))
                        {
                            int byteLen = batchFrames * AudioFormat.SamplesPerFrame;
                            string b64 = Convert.ToBase64String(ulawBatch, 0, byteLen);
                            batchFrames = 0;
                            string json = JsonSerializer.Serialize(new
                            {
                                type = "audio",
                                seq = ++seq,
                                sr = AudioFormat.SampleRate,
                                enc = "ulaw",
                                data = b64
                            });
                            await client.SendCustomJsonAsync(CastProtocol.NsNodeX, json, ct).ConfigureAwait(false);
                        }
                    }

                    long now = Environment.TickCount64;
                    bool active = hasAudio || _runtime.RxActivity.Talking().Count > 0 || _runtime.Links.Any(l => l.Ptt.IsPttActive);
                    long interval = active ? 200 : 5000;
                    if (_stateDirty || now - lastStateSentAt >= interval)
                    {
                        _stateDirty = false;
                        lastStateSentAt = now;
                        await SendStateSnapshotAsync(client, ct).ConfigureAwait(false);
                    }
                }
                else if (lanServer != null)
                {
                    // Continuous 8 kHz stream keeps CC1AD845 Default Media Receiver buffered cleanly during idle pauses.
                    lanServer.PushMixedFrame(mixed);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            DiagnosticLog.Default.Detail("cast", $"cast pump stopped: {ex.Message}");
            if (!ct.IsCancellationRequested)
                OnClientDisconnected(ex.Message);
        }
    }

    private Task SendStateSnapshotAsync(CastClient client, CancellationToken ct)
    {
        var settings = _runtime.Config.Settings;
        var conns = CarBrowse.OrderActiveConnections(_runtime.Connections, settings.SortLinksByActivity);
        var talking = new HashSet<string>(_runtime.RxActivity.Talking(), StringComparer.Ordinal);
        string? txNode = _runtime.TransmitNode;
        bool anyTx = false;
        double maxMic = 0, maxSpk = 0;

        var links = new List<object>(conns.Count);
        foreach (var c in conns)
        {
            bool isTx = c.Node == txNode;
            bool isTalking = talking.Contains(c.Node);
            bool connTx = c.Link?.Ptt.IsPttActive == true;
            anyTx |= connTx;

            if (c.Link is { } link)
            {
                maxMic = Math.Max(maxMic, (link.Audio.InputLevel.RmsDbfs + 60.0) / 60.0);
                maxSpk = Math.Max(maxSpk, (link.Audio.OutputLevel.RmsDbfs + 60.0) / 60.0);
            }

            string title = c.IsPrivate
                ? (c.PrivateName ?? "").Trim()
                : (settings.FavoriteLabels.TryGetValue(c.Node, out var lbl) ? lbl.Trim() : "");

            string state = c.Link?.Session.State == Iax2SessionState.Connected
                ? "CONNECTED"
                : c.Reconnect.Active ? "RECONNECTING" : "CONNECTING";

            int? rttMs = c.Link is { Session.RoundTripTime: var rtt } && rtt > TimeSpan.Zero
                ? (int)Math.Round(rtt.TotalMilliseconds)
                : null;

            string alsoLinked = c.Link != null
                ? AslTextParser.FormatLinkedNodes(c.Link.RemoteLinkedNodes, settings.NodeNumber, maxShown: 6)
                : "";

            // Never expose private hostnames or secrets on the shared TV screen.
            links.Add(new
            {
                node = c.Node,
                label = title,
                @private = c.IsPrivate,
                isTx,
                transmitting = connTx,
                isReceiving = isTalking,
                muted = c.Muted,
                state,
                rttMs,
                alsoLinked
            });
        }

        string json = JsonSerializer.Serialize(new
        {
            type = "state",
            station = _runtime.LocalStationDisplay,
            registered = _runtime.IsRegistered,
            registrationText = _runtime.IsRegistered ? "ASL Registered" : _runtime.RegistrationStatus,
            transmitting = anyTx,
            txNode,
            micLevel = Math.Clamp(maxMic, 0.0, 1.0),
            spkLevel = Math.Clamp(maxSpk, 0.0, 1.0),
            sortRecent = settings.SortLinksByActivity,
            links
        });
        return client.SendCustomJsonAsync(CastProtocol.NsNodeX, json, ct);
    }
}
