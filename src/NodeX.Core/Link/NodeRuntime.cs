using System.Diagnostics;
using System.Net;
using NodeX.Core.Audio;
using NodeX.Core.Diagnostics;
using NodeX.Core.Settings;

namespace NodeX.Core;

/// <summary>
/// Application-level coordinator, independent of any UI toolkit: owns the saved configuration, the
/// AllStarLink registration and up to <see cref="MaxLinks"/> simultaneous node links (one
/// <see cref="NodeConnection"/> each, of which exactly one is the transmit node), and keeps every link alive
/// across failures: it re-dials a link that was established and then lost (with back-off), and when the
/// device's network changes it re-registers immediately and re-establishes the calls from the new address.
/// UIs bind to its events.
/// </summary>
public sealed class NodeRuntime : IAsyncDisposable
{
    private readonly Func<IAudioDeviceService> _audioFactory;
    private readonly NodeResolution _resolver;
    private readonly NodeAudioEngineOptions _audioOptions;
    private readonly Func<AslRegistrationConfig, AslRegistrationService> _registrationFactory;
    private readonly ReconnectPolicy _policy;
    private readonly Action<Iax2Session>? _configureSession;
    private readonly bool _ownsMonitor;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _activeLock = new();
    private readonly object _connLock = new();
    private readonly List<NodeConnection> _connections = [];   // guarded by _connLock
    private string? _transmitNode;                              // guarded by _connLock

    private static DiagnosticLog Log => DiagnosticLog.Default;

    private INetworkMonitor? _monitor;
    private INodeRegistration? _registration;
    private readonly Func<NodeConfig, Iax2Endpoint, Iax2Registrar>? _iaxRegistrarFactory;

    /// <summary>
    /// The one UDP socket every call and the IAX2 registration use (see <see cref="Iax2Endpoint"/>), so far nodes see
    /// calls come from the address the registry recorded. Replaced when the network changes. Guarded by _endpointLock.
    /// </summary>
    private Iax2Endpoint? _endpoint;
    private readonly object _endpointLock = new();
    private CancellationTokenSource? _debounceCts;
    private TaskCompletionSource _networkSignal = NewSignal();
    private string _lastSignature = "";
    private bool _active;
    private string? _activeNode;
    private bool _started;
    private Task? _startTask;                                   // guarded by _connLock
    private int _startupAutoConnectDone;
    private bool _disposed;
    private volatile bool _exited;
    private readonly RxActivity _rxActivity = new();
    private long _activitySeq;

    public NodeRuntime(
        NodeConfigService configService,
        Func<IAudioDeviceService> audioFactory,
        NodeResolution? resolver = null,
        NodeAudioEngineOptions? audioOptions = null,
        Func<AslRegistrationConfig, AslRegistrationService>? registrationFactory = null,
        INetworkMonitor? networkMonitor = null,
        ReconnectPolicy? reconnectPolicy = null,
        Action<Iax2Session>? configureSession = null,
        Func<NodeConfig, Iax2Endpoint, Iax2Registrar>? iaxRegistrarFactory = null)
    {
        ConfigService = configService ?? throw new ArgumentNullException(nameof(configService));
        _audioFactory = audioFactory ?? throw new ArgumentNullException(nameof(audioFactory));
        _resolver = resolver ?? new NodeResolution();
        _audioOptions = audioOptions ?? new NodeAudioEngineOptions();

        // Production registers over IAX2 from the call socket (HTTPS as the fallback). A caller that injects its own
        // HTTPS registration (the tests' fake registry) gets HTTPS only, unless it also injects an IAX2 registrar.
        _iaxRegistrarFactory = iaxRegistrarFactory ?? (registrationFactory == null
            ? (cfg, endpoint) => new Iax2Registrar(cfg.Settings.NodeNumber, cfg.Password ?? "", endpoint)
            : null);
        _registrationFactory = registrationFactory ?? (cfg => new AslRegistrationService(cfg));

        // The network check and NAT keep-alive talk to public STUN servers; a test with its own fake registry does not.
        NetworkCheckServers = registrationFactory == null ? NetworkCheck.DefaultServers : [];
        _monitor = networkMonitor;
        _ownsMonitor = networkMonitor == null;      // created lazily in StartAsync so previews never watch the network
        _policy = reconnectPolicy ?? new ReconnectPolicy();
        _configureSession = configureSession;
        Config = new NodeConfig(new NodeSettings(), null);
        _rxActivity.NodeBecameActive += OnRxNodeBecameActive;
    }

    public NodeConfigService ConfigService { get; }

    /// <summary>
    /// Whether this app may transmit. Listening always works. Platform heads set
    /// <see cref="TransmitPolicy.MicrophoneAllowed"/>.
    /// </summary>
    public TransmitPolicy Transmit { get; } = new();

    /// <summary>Latest loaded or saved configuration (including the password, in memory only).</summary>
    public NodeConfig Config { get; private set; }

    /// <summary>Active manual receive gain multiplier (updated immediately for live sliders).</summary>
    public float LiveRxGain { get; private set; } = 1.0f;

    /// <summary>Active manual microphone gain multiplier (updated immediately for live sliders).</summary>
    public float LiveMicGain { get; private set; } = 1.0f;

    /// <summary>Active receive auto-level toggle (updated immediately for live switches).</summary>
    public bool LiveRxAutoLevel { get; private set; }

    /// <summary>Active Android extra speaker boost toggle.</summary>
    public bool LiveRxLoudnessBoost { get; private set; }

    /// <summary>Raised when live receive gain, auto-level, or Android loudness boost changes.</summary>
    public event Action<float, bool, bool>? RxAudioChanged;

    /// <summary>
    /// Applies receive audio settings immediately to every active link and notifies platform audio backends
    /// without waiting for settings persistence or interrupting calls.
    /// </summary>
    public void SetLiveRxAudio(float rxGain, bool rxAutoLevel, bool? rxLoudnessBoost = null)
    {
        float gain = RxAudioProcessor.SanitizeGain(rxGain);
        bool boost = rxLoudnessBoost ?? LiveRxLoudnessBoost;
        LiveRxGain = gain;
        LiveRxAutoLevel = rxAutoLevel;
        LiveRxLoudnessBoost = boost;
        foreach (var link in Links)
        {
            link.Audio.RxGain = gain;
            link.Audio.RxAutoLevel = rxAutoLevel;
        }
        RxAudioChanged?.Invoke(gain, rxAutoLevel, boost);
    }

    /// <summary>
    /// Applies microphone transmit gain immediately to every active link without interrupting calls.
    /// </summary>
    public void SetLiveMicGain(float micGain)
    {
        float gain = NodeAudioEngine.SanitizeMicGain(micGain);
        LiveMicGain = gain;
        foreach (var link in Links)
            link.Audio.MicGain = gain;
    }

    // ----- registration state -----

    private bool _registered;
    private string _registrationStatus = "Not registered";
    private DateTime? _lastRegistrationOk;
    private TimeSpan _registrationInterval = TimeSpan.FromSeconds(60);

    /// <summary>Clock for the staleness rule; tests replace it.</summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    /// <summary>
    /// Registered <b>and</b> the registry accepted this node recently. The last attempt having succeeded is not
    /// enough: while a phone sleeps no attempt is made at all, nothing fails, and the registry silently drops the
    /// node after a few minutes. A field report (2026-11-19) showed exactly that: "ASL Registered" in green while
    /// far nodes said "node cannot be found".
    /// </summary>
    public bool IsRegistered => _registered && !RegistrationLapsed;

    /// <summary>
    /// True when the last attempt succeeded but no refresh has succeeded for <see cref="RegistrationStaleAfter"/>
    /// (the device slept, or the loop stalled), so the registry has probably dropped the node.
    /// </summary>
    public bool RegistrationLapsed =>
        _registered && _lastRegistrationOk is { } ok && UtcNow() - ok > RegistrationStaleAfter(_registrationInterval);

    private int _renewingLapsed;

    /// <summary>
    /// Re-registers at once if the registration has <see cref="RegistrationLapsed">lapsed</see> (the UI calls this
    /// on its clock tick, so a phone that wakes up does not wait for the next beat). One renewal at a time.
    /// </summary>
    public void RenewIfLapsed()
    {
        if (!RegistrationLapsed || _exited || Interlocked.Exchange(ref _renewingLapsed, 1) == 1) return;
        Log.Info("reg", $"registration lapsed ({RegistrationStatus}); renewing now");
        _ = Task.Run(async () =>
        {
            try { await RefreshRegistrationAsync(); }
            finally { Volatile.Write(ref _renewingLapsed, 0); }
        });
    }

    /// <summary>Two missed refreshes plus slack: one lost refresh is normal and is retried within seconds.</summary>
    public static TimeSpan RegistrationStaleAfter(TimeSpan interval) => interval * 2 + TimeSpan.FromSeconds(30);

    public string RegistrationStatus => RegistrationLapsed
        ? $"Registration lapsed (last accepted {DurationText.Format(UtcNow() - _lastRegistrationOk!.Value)} ago); re-registering…"
        : _registrationStatus;

    public IPAddress? PerceivedAddress { get; private set; }

    /// <summary>Public port the registry last reported for this node (0 when unknown).</summary>
    public int PerceivedPort { get; private set; }

    /// <summary>Sends one informational IAX POKE to a private host/port without touching AllStarLink services.</summary>
    public Task<PrivateProbeResult> ProbePrivateNodeAsync(string host, int port, CancellationToken cancellationToken = default) =>
        _resolver.ProbePrivateAsync(host, port, cancellationToken);

    /// <summary>How the node is registered right now ("IAX2" or "HTTPS"), or empty when not registered.</summary>
    public string RegistrationTransport => _registration?.Transport ?? "";

    /// <summary>The last network check (see <see cref="NetworkCheck"/>), or null if none has run on this network.</summary>
    public NetworkCheckResult? LastNetworkCheck { get; private set; }

    /// <summary>
    /// When the current unbroken run of successful registration began (UTC), or null when not registered. The
    /// periodic ~60 s refresh does not reset it; a lapse, a changed registration setting or a restart does.
    /// </summary>
    public DateTimeOffset? RegisteredSince { get; private set; }

    /// <summary>Raised (on any thread) whenever registration status changes.</summary>
    public event Action? RegistrationChanged;

    // ----- link state -----

    /// <summary>The most nodes that can be linked at once (each costs a UDP socket and an audio output).</summary>
    public const int MaxLinks = 8;

    /// <summary>
    /// The transmit link: the live call to <see cref="TransmitNode"/>, or null when that node is between attempts or
    /// none is chosen. With one node linked this is simply "the link".
    /// </summary>
    public NodeLink? Link
    {
        get
        {
            lock (_connLock)
                return _connections.FirstOrDefault(c => c.Node == _transmitNode)?.Link;
        }
    }

    /// <summary>Every live call, in the order they were added.</summary>
    public IReadOnlyList<NodeLink> Links
    {
        get
        {
            lock (_connLock)
                return _connections.Select(c => c.Link).OfType<NodeLink>().ToArray();
        }
    }

    /// <summary>Every node the user wants linked (including ones still connecting or reconnecting), in the order added.</summary>
    public IReadOnlyList<NodeConnection> Connections
    {
        get
        {
            lock (_connLock)
                return _connections.ToArray();
        }
    }

    /// <summary>The node the user's voice goes to; null when none is chosen (every link is then listen-only).</summary>
    public string? TransmitNode
    {
        get
        {
            lock (_connLock)
                return _transmitNode;
        }
    }

    /// <summary>Shared remote-RX tracker used by Android Auto and activity ordering.</summary>
    public RxActivity RxActivity => _rxActivity;

    /// <summary>Raised on any thread when an unmuted node starts receiving or the user keys PTT on a node.</summary>
    public event Action<string>? LinkActivityChanged;

    /// <summary>Raised when a call is created. May be raised on any thread.</summary>
    public event Action<NodeLink>? LinkAdded;

    /// <summary>Raised after a call has ended and been disposed. May be raised on any thread.</summary>
    public event Action<NodeLink>? LinkRemoved;

    /// <summary>
    /// Raised (on any thread) whenever the set of connections, a connection's state, the transmit choice or a mute
    /// changes. UIs re-read <see cref="Connections"/>.
    /// </summary>
    public event Action? ConnectionsChanged;

    /// <summary>
    /// Raised when the node link becomes wanted (true) or no longer wanted (false), so platform heads can
    /// start or stop background execution (Android foreground service, Bluetooth SCO). It stays true
    /// across automatic reconnects, so the background service is not torn down mid-recovery.
    /// </summary>
    public event Action<bool, string?>? LinkActiveChanged;

    /// <summary>What auto-reconnect is doing now.</summary>
    public ReconnectStatus Reconnect { get; private set; } = ReconnectStatus.Inactive;

    /// <summary>Raised (on any thread) when <see cref="Reconnect"/> changes.</summary>
    public event Action<ReconnectStatus>? ReconnectChanged;

    /// <summary>Raised when something failed that the user should see.</summary>
    public event Action<string>? ErrorOccurred;

    /// <summary>Raised for informational events worth logging, such as "Reconnected to node 2000".</summary>
    public event Action<string>? Notice;

    // ----- background presence -----

    /// <summary>
    /// What the OS must keep alive right now (see <see cref="BackgroundState"/>). Platform heads start or stop their
    /// background execution (Android foreground service) from this, so a configured node stays registered with
    /// AllStarLink while idle, and everything stops after <see cref="ExitAsync"/>.
    /// </summary>
    public BackgroundState Background { get; private set; } = new(false, null, false, "Not registered");

    /// <summary>Raised (on any thread) when <see cref="Background"/> changes.</summary>
    public event Action<BackgroundState>? BackgroundChanged;

    /// <summary>True after <see cref="ExitAsync"/>: nothing runs and nothing restarts.</summary>
    public bool HasExited => _exited;

    private void PublishBackground()
    {
        BackgroundState next;
        lock (_activeLock)
        {
            bool wantRegistered = Config.IsComplete && Config.Settings.RegisterOnStartup;
            bool needed = !_exited && (_active || wantRegistered);
            next = new BackgroundState(needed, _active ? _activeNode : null, IsRegistered, RegistrationStatus);
            if (next == Background) return;
            Background = next;
        }
        try
        {
            BackgroundChanged?.Invoke(next);
        }
        catch (Exception ex)
        {
            Log.Info("bg", $"background listener failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The app came back to the foreground (or the OS woke it): re-register now rather than at the next 60 s
    /// beat, and re-check the network. After a long suspension the registry entry may have lapsed.
    /// </summary>
    public void NotifyResumed()
    {
        if (_exited || !_started) return;
        OnNetworkChanged();
        _ = Task.Run(async () =>
        {
            try { await RefreshRegistrationAsync(); }
            catch (Exception) { /* the periodic beat will retry */ }
        });
    }

    /// <summary>
    /// "Exit NodeX": hangs up, stops registering and stops reacting to anything. After this the platform head
    /// should end the process. Registration is not withdrawn explicitly (ASL has no documented call for it); the
    /// registry entry simply stops being refreshed.
    /// </summary>
    public async Task ExitAsync()
    {
        if (_exited) return;
        _exited = true;

        await DisposeAsync();
        PublishBackground();
    }

    // =====================================================================================
    // Startup, configuration, registration
    // =====================================================================================

    /// <summary>Loads the saved configuration, starts registering if it is complete, and starts watching the network.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        // Idempotent and awaitable by every caller: the UI and the Android Auto service can both start the runtime,
        // and whichever comes second waits for the first to finish loading the settings.
        lock (_connLock)
            return _startTask ??= StartCoreAsync(cancellationToken);
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        _started = true;

        _monitor ??= new SystemNetworkMonitor();
        _lastSignature = _monitor.GetSignature();
        _monitor.Changed += OnNetworkChanged;

        ConfigService.Changed += OnConfigSaved;
        var cfg = await ConfigService.LoadAsync(cancellationToken);
        await ApplyConfigAsync(cfg, cancellationToken);
    }

    private void OnConfigSaved(NodeConfig cfg) => _ = ApplyConfigAsync(cfg);

    /// <summary>Adopts a configuration and (re)starts registration to match it.</summary>
    public async Task ApplyConfigAsync(NodeConfig cfg, CancellationToken cancellationToken = default)
    {
        if (_exited) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_exited) return;
            var previous = Config;
            Config = cfg;
            Log.Detailed = cfg.Settings.DetailedLogging;
            SetLiveRxAudio(cfg.Settings.RxGain, cfg.Settings.RxAutoLevel, cfg.Settings.RxLoudnessBoost);
            SetLiveMicGain(cfg.Settings.MicGain);
            if (previous.Settings.AutoReconnect && !cfg.Settings.AutoReconnect)
                _ = Task.Run(StopRetriesAfterAutoReconnectOffAsync);   // outside the gate: a dial may need it

            // Changing something unrelated to registration (a favourite, the PTT mode...) must not drop and
            // redo the registration.
            if (_registration != null && cfg.IsComplete && cfg.Settings.RegisterOnStartup
                && SameRegistration(previous, cfg))
                return;

            await StopRegistrationAsync();

            if (cfg.IsComplete && cfg.Settings.RegisterOnStartup)
            {
                await StartRegistrationCoreAsync(cfg, cancellationToken);
            }
            else
            {
                SetRegistration(false, cfg.IsComplete ? "Registration off" : "Not configured", null, 0);
            }
        }
        finally
        {
            _gate.Release();
            PublishBackground();   // completeness / "register on startup" may have changed
        }
    }

    /// <summary>
    /// Auto-reconnect was just turned off: every node that is only waiting to be redialled is stopped and dropped at
    /// once (2026-11-21 review: a running loop used to dial again). Live calls are untouched.
    /// </summary>
    private async Task StopRetriesAfterAutoReconnectOffAsync()
    {
        foreach (var c in SnapshotConnections().Where(IsReconnectRunning))
        {
            await StopReconnectAsync(c);
            if (c.Link == null && !Config.Settings.AutoReconnect)
            {
                Notice?.Invoke($"Auto-reconnect is off, so NodeX stopped reconnecting to node {c.DisplayNode}.");
                RemoveConnection(c, byUser: false, "auto-reconnect was turned off while reconnecting");
            }
        }
    }

    private static bool SameRegistration(NodeConfig a, NodeConfig b) =>
        a.Settings.NodeNumber == b.Settings.NodeNumber
        && a.Password == b.Password
        && a.Settings.IaxPort == b.Settings.IaxPort
        && a.Settings.RegistrationUrl == b.Settings.RegistrationUrl
        && a.Settings.IaxRegistration == b.Settings.IaxRegistration;

    /// <summary>
    /// Creates and starts the registration for <paramref name="cfg"/> (caller holds <see cref="_gate"/>). Returns a
    /// task that completes with the first attempt's result (or false after <see cref="ReconnectPolicy.RegistrationWait"/>
    /// plus the IAX2 fallback time).
    /// </summary>
    private async Task<Task<bool>> StartRegistrationCoreAsync(NodeConfig cfg, CancellationToken cancellationToken)
    {
        var registration = CreateRegistration(cfg);
        var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFirst(bool ok, string _) => first.TrySetResult(ok);
        registration.RegistrationStatusChanged += OnFirst;
        registration.RegistrationStatusChanged += OnRegistrationStatus;
        if (registration is FallbackRegistration fallback)
        {
            fallback.FellBack += reason =>
            {
                Log.Info("reg", $"IAX2 registration did not work on this network ({reason}); registering over HTTPS instead");
                Notice?.Invoke($"AllStarLink did not accept registration over IAX2 ({reason}), so NodeX is " +
                               "registering over HTTPS for now and will try IAX2 again every 5 minutes. On mobile data, nodes may refuse links until then.");
            };
            fallback.Recovered += () =>
            {
                Log.Info("reg", "IAX2 registration works again; HTTPS registration stopped");
                Notice?.Invoke("Registered over IAX2 again (from the same connection as the calls).");
            };
        }
        _registration = registration;
        Log.Info("reg", $"registration starting over {registration.Transport}" +
                        (registration is FallbackRegistration f ? $" from UDP port {f.Iax.LocalPort} (the call socket), HTTPS as fallback" : ""));
        await registration.StartAsync(cancellationToken);

        return first.Task.ContinueWith(t =>
        {
            registration.RegistrationStatusChanged -= OnFirst;
            return t.Result;
        }, TaskScheduler.Default);
    }

    private INodeRegistration CreateRegistration(NodeConfig cfg)
    {
        if (cfg.Settings.IaxRegistration && _iaxRegistrarFactory != null)
        {
            var iax = _iaxRegistrarFactory(cfg, EnsureEndpoint());
            return new FallbackRegistration(iax, () => _registrationFactory(cfg.ToRegistrationConfig()));
        }
        return _registrationFactory(cfg.ToRegistrationConfig());
    }

    /// <summary>The current shared call socket, created on first use (and again after a network change).</summary>
    private Iax2Endpoint EnsureEndpoint()
    {
        lock (_endpointLock)
        {
            if (_endpoint == null || _endpoint.IsDisposed)
            {
                _endpoint = new Iax2Endpoint();
                _networkCheckDone = false;
                Log.Detail("net", $"call socket opened on UDP port {_endpoint.LocalPort}");
                _natKeepAliveCts?.Cancel();
                _natKeepAliveCts = new CancellationTokenSource();
                var endpoint = _endpoint;
                var token = _natKeepAliveCts.Token;
                _ = Task.Run(() => NatKeepAliveLoopAsync(endpoint, token));
            }
            return _endpoint;
        }
    }

    private CancellationTokenSource? _natKeepAliveCts;     // guarded by _endpointLock

    /// <summary>Gap between NAT keep-alives on the call socket (carrier UDP mappings can expire after ~30 s idle).</summary>
    internal TimeSpan NatKeepAliveInterval { get; set; } = TimeSpan.FromSeconds(25);

    /// <summary>
    /// Keeps the call socket's carrier NAT mapping (and so its public address) alive between registrations with a
    /// tiny STUN request every <see cref="NatKeepAliveInterval"/>, while registered over IAX2. If the mapping
    /// expired, the next packet could get a different public address than the one the registry and the far nodes'
    /// node lists hold, and links would be refused again for minutes. The answer also shows when the public address
    /// has changed anyway; the node then re-registers at once.
    /// </summary>
    private async Task NatKeepAliveLoopAsync(Iax2Endpoint endpoint, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !endpoint.IsDisposed)
            {
                await Task.Delay(NatKeepAliveInterval, ct);
                if (NetworkCheckServers.Count == 0 || _registration is not FallbackRegistration { UsingFallback: false })
                    continue;
                if (!_registered) continue;

                var (host, port) = NetworkCheckServers[0];
                IPEndPoint? server;
                try
                {
                    var addrs = await Dns.GetHostAddressesAsync(host, ct);
                    var v4 = Array.Find(addrs, a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                    server = v4 == null ? null : new IPEndPoint(v4, port);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { server = null; }
                if (server == null) continue;

                var mapped = await NetworkCheck.QueryAsync(endpoint, server, TimeSpan.FromSeconds(1.5), ct);
                var registered = PerceivedAddress;
                if (mapped != null && registered != null && !mapped.Address.Equals(registered))
                {
                    Log.Info("net", $"call socket's public address changed from {registered} to {mapped.Address}; re-registering now");
                    _ = RefreshRegistrationAsync();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            Log.Info("net", $"NAT keep-alive stopped: {ex.Message}");
        }
    }

    /// <summary>
    /// The network changed: the old socket's NAT mapping belongs to the old path. New calls and the registration
    /// get a fresh socket; calls still on the old one keep it until they end.
    /// </summary>
    private void RetireEndpoint()
    {
        Iax2Endpoint? old;
        lock (_endpointLock)
        {
            old = _endpoint;
            _endpoint = null;
            _natKeepAliveCts?.Cancel();
            _natKeepAliveCts = null;
        }
        old?.Retire();
    }

    private async Task StopRegistrationAsync()
    {
        var old = _registration;
        _registration = null;
        if (old == null) return;

        old.RegistrationStatusChanged -= OnRegistrationStatus;
        await old.DisposeAsync();
        SetRegistration(false, "Not registered", null, 0);
    }

    private void OnRegistrationStatus(bool registered, string message)
    {
        var registration = _registration;
        if (registration != null) _registrationInterval = registration.CurrentInterval;
        SetRegistration(registered, message, registration?.PerceivedAddress, registration?.PerceivedPort ?? 0);
        if (registered)
        {
            _ = RunNetworkCheckOnceAsync();
            MaybeAutoConnectOnStartup();
        }
    }

    /// <summary>
    /// Runs once per app launch after the first successful registration: connects every configured
    /// auto-connect node (up to <see cref="MaxLinks"/>), skipping any already linked. Never re-dials after
    /// the user manually disconnects during the same session.
    /// </summary>
    private void MaybeAutoConnectOnStartup()
    {
        if (_exited || _disposed || !Config.IsComplete) return;
        if (Interlocked.CompareExchange(ref _startupAutoConnectDone, 1, 0) != 0) return;

        var targets = Config.Settings.GetEffectiveAutoConnectNodes();
        if (targets.Count == 0) return;

        var privateNodes = (Config.Settings.PrivateNodes ?? []).ToArray();
        var list = targets.Take(MaxLinks).ToArray();
        _ = Task.Run(() => RunStartupAutoConnectAsync(list, privateNodes));
    }

    private async Task RunStartupAutoConnectAsync(string[] targets, PrivateNodeEntry[] privateNodes)
    {
        Log.Info("link", $"startup auto-connect starting for {targets.Length} node(s): {string.Join(", ", targets)}");
        Notice?.Invoke($"Auto-connecting to {targets.Length} startup node{(targets.Length == 1 ? "" : "s")}…");

        foreach (string raw in targets)
        {
            if (_exited || _disposed) break;
            if (SnapshotConnections().Length >= MaxLinks) break;
            if (!ManualNodeAddress.TryParse(raw, out var addr, out _) || addr == null || addr.IsIpv6) continue;
            if (FindConnection(addr.Node) != null) continue;

            string? name = addr.IsPrivate
                ? privateNodes.FirstOrDefault(p => p.ToManualAddress().SameTargetAs(addr))?.Name
                : null;
            try
            {
                await ConnectAsync(addr, name).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Info("link", $"startup auto-connect to '{raw}' failed: {ex.Message}");
            }
        }
    }

    private void SetRegistration(bool registered, string status, IPAddress? address, int port = 0)
    {
        bool wasRegistered = IsRegistered;      // false while lapsed, so a success after a lapse starts a new run
        if (!registered) RegisteredSince = null;
        else if (!wasRegistered) RegisteredSince = DateTimeOffset.UtcNow;   // false -> true; refreshes keep the start

        if (registered != wasRegistered || status != _registrationStatus || !Equals(address, PerceivedAddress) || port != PerceivedPort)
            Log.Info("reg", $"registered={registered} status=\"{status}\" perceivedAddress={address?.ToString() ?? "none"}");

        if (registered) _lastRegistrationOk = UtcNow();
        else _lastRegistrationOk = null;
        _registered = registered;
        _registrationStatus = status;
        PerceivedAddress = address;
        PerceivedPort = port;
        RegistrationChanged?.Invoke();
        PublishBackground();
    }

    // ----- network check -----

    private bool _networkCheckDone;     // guarded by _endpointLock; one check per call socket

    /// <summary>Servers for the network check; tests point it at a fake or turn it off with an empty list.</summary>
    internal IReadOnlyList<(string Host, int Port)> NetworkCheckServers { get; set; }

    /// <summary>
    /// Once per call socket, after the first successful registration on it: asks two STUN servers what public
    /// address the call socket has, compares that with what the registry recorded, logs it, and warns the user when
    /// far nodes will see a different address than the registry (they then refuse the link).
    /// </summary>
    private async Task RunNetworkCheckOnceAsync()
    {
        Iax2Endpoint? endpoint;
        lock (_endpointLock)
        {
            if (_networkCheckDone || _endpoint == null || NetworkCheckServers.Count == 0) return;
            _networkCheckDone = true;
            endpoint = _endpoint;
        }
        await RunNetworkCheckAsync(endpoint);
    }

    /// <summary>Runs the network check now on the current call socket (also for "Send logs").</summary>
    public Task<NetworkCheckResult?> RunNetworkCheckAsync() => RunNetworkCheckAsync(EnsureEndpoint());

    private async Task<NetworkCheckResult?> RunNetworkCheckAsync(Iax2Endpoint endpoint)
    {
        try
        {
            var result = await NetworkCheck.RunAsync(endpoint, PerceivedAddress, RegistrationTransport, NetworkCheckServers);
            LastNetworkCheck = result;
            Log.Info("net", $"network check (call socket UDP port {endpoint.LocalPort}): {result.Describe()}");
            if (result.UserWarning is { } warning) ErrorOccurred?.Invoke(warning);
            return result;
        }
        catch (Exception ex)
        {
            Log.Info("net", $"network check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Re-registers now (the Android idle wake-up alarm calls this: while the phone sleeps the normal timer does not run).
    /// </summary>
    public Task<bool> RenewRegistrationAsync() => _exited ? Task.FromResult(false) : RefreshRegistrationAsync();

    /// <summary>Registers right now (outside the normal cycle), e.g. after the network changed.</summary>
    private async Task<bool> RefreshRegistrationAsync()
    {
        var registration = _registration;
        if (registration == null) return false;

        var sw = Stopwatch.StartNew();
        try
        {
            bool ok = await registration.SendHeartbeatAsync();
            Log.Detail("reg", $"immediate re-registration ok={ok} in {sw.ElapsedMilliseconds} ms");
            return ok;
        }
        catch (Exception ex)
        {
            Log.Detail("reg", $"immediate re-registration threw after {sw.ElapsedMilliseconds} ms: {ex.Message}");
            return false;
        }
    }

    // =====================================================================================
    // Connecting and disconnecting (one NodeConnection per linked node)
    // =====================================================================================

    private NodeConnection[] SnapshotConnections()
    {
        lock (_connLock) return _connections.ToArray();
    }

    private NodeConnection? FindConnection(string node)
    {
        lock (_connLock) return _connections.FirstOrDefault(c => c.Node == node);
    }

    /// <summary>Prefixes a message with the node it is about when more than one node is linked.</summary>
    private string ForNode(NodeConnection c, string message)
    {
        int count;
        lock (_connLock) count = _connections.Count;
        return count > 1 ? $"Node {c.DisplayNode}: {message}" : message;
    }

    /// <summary>
    /// Resolves <paramref name="targetNode"/> and dials it, <b>in addition to</b> any nodes already linked (up to
    /// <see cref="MaxLinks"/>). The first node linked becomes the transmit target; later ones are listen-only until
    /// <see cref="SetTransmitNode"/> chooses them. Connecting again to a node that is already linked redials just that
    /// node. Returns null when the node cannot be resolved.
    /// </summary>
    /// <exception cref="InvalidOperationException">Not configured, <see cref="MaxLinks"/> nodes are already linked, or a private node with the same number is already linked.</exception>
    public Task<NodeInfo?> ConnectAsync(string targetNode, CancellationToken cancellationToken = default)
    {
        if (!Config.IsComplete)
            throw new InvalidOperationException("Enter your node number and password in Settings first.");
        targetNode = targetNode?.Trim() ?? "";
        if (!NodeSettingsValidator.IsValidNodeNumber(targetNode))
            throw new ArgumentException("Target must be a 3 to 7 digit node number.", nameof(targetNode));

        return ConnectCoreAsync(new ManualNodeAddress(targetNode), privateName: null, cancellationToken);
    }

    /// <summary>
    /// Connects to a parsed public or private node address (§4.9). Private entries resolve their host fresh on every
    /// dial, use their exact port without probing, and never contact AllStarLink services. Linking two nodes with the
    /// same number simultaneously (e.g. a public 1999 and a private 1999, or two different hosts for 1999) is refused
    /// with a clear message.
    /// </summary>
    public Task<NodeInfo?> ConnectAsync(ManualNodeAddress address, string? name = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!Config.IsComplete)
            throw new InvalidOperationException("Enter your node number and password in Settings first.");
        if (!NodeSettingsValidator.IsValidNodeNumber(address.Node))
            throw new ArgumentException("Target must be a 3 to 7 digit node number.", nameof(address));
        if (address.IsIpv6)
            throw new NotSupportedException("IPv6 dialling is not supported yet.");

        return ConnectCoreAsync(address, name, cancellationToken);
    }

    private async Task<NodeInfo?> ConnectCoreAsync(
        ManualNodeAddress address, string? privateName, CancellationToken cancellationToken)
    {
        string targetNode = address.Node;
        NodeConnection? existing;
        NodeConnection c;
        Task<NodeInfo?>? joinPending = null;
        var mine = new TaskCompletionSource<NodeInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_connLock)
        {
            existing = _connections.FirstOrDefault(x => x.Node == targetNode);
            if (existing != null)
            {
                var existingAddr = existing.PrivateAddress ?? new ManualNodeAddress(existing.Node);
                if (!address.SameTargetAs(existingAddr))
                {
                    string via = existing.PrivateAddress != null
                        ? existing.PrivateAddress.HostDisplay
                        : "AllStarLink";
                    throw new InvalidOperationException($"Node {targetNode} is already linked via {via}.");
                }
            }
            else if (_connections.Count >= MaxLinks)
            {
                throw new InvalidOperationException(
                    $"You can link up to {MaxLinks} nodes at once. Disconnect one first.");
            }

            c = existing ?? new NodeConnection(targetNode, address.IsPrivate ? address : null, privateName);
            if (existing?.PendingConnect is { IsCompleted: false } running)
            {
                joinPending = running;                    // already being connected: join that attempt
            }
            else
            {
                c.PendingConnect = mine.Task;
                if (existing != null && !string.IsNullOrWhiteSpace(privateName))
                    c.PrivateName = privateName.Trim();
                if (existing == null)
                {
                    _connections.Add(c);
                    if (_connections.Count == 1) _transmitNode = targetNode;   // the only link is where you transmit
                }
            }
        }

        if (joinPending != null)
        {
            Log.Detail("dial", $"node {c.DisplayNode}: connect already in progress; joining it");
            return await joinPending.WaitAsync(cancellationToken);
        }

        // Nobody may be waiting on this attempt; observe a failure so it is not reported as unobserved.
        _ = mine.Task.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        try
        {
            var result = await ConnectOwnedAsync(c, existing != null, cancellationToken);
            mine.TrySetResult(result);
            return result;
        }
        catch (OperationCanceledException ex)
        {
            mine.TrySetCanceled(ex.CancellationToken);
            throw;
        }
        catch (Exception ex)
        {
            mine.TrySetException(ex);
            throw;
        }
        finally
        {
            lock (_connLock)
            {
                if (ReferenceEquals(c.PendingConnect, mine.Task)) c.PendingConnect = null;
            }
        }
    }

    /// <summary>The connect that owns <paramref name="c"/>'s dial (see <see cref="NodeConnection.PendingConnect"/>).</summary>
    private async Task<NodeInfo?> ConnectOwnedAsync(NodeConnection c, bool redial, CancellationToken cancellationToken)
    {
        if (redial)
        {
            // Redial just this node: stop its recovery and end its call, keeping its transmit/mute choices.
            await StopReconnectAsync(c);
            var old = c.Link;
            if (old != null) await EndLinkAsync(c, old, transient: true);
            c.Wanted = true;
        }

        Interlocked.Exchange(ref c.EarlyHangups, 0);
        Interlocked.Exchange(ref c.RemoteDrops, 0);

        // The user can hang this node up while we are still resolving (the row is already on screen).
        // That must stop the connect, not let it dial a node that is no longer in the list.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, c.Abandoned.Token);
        try
        {
            ApplyTransmitFlags();
            UpdateActive();
            ConnectionsChanged?.Invoke();

            var info = await DialAsync(c, linked.Token);
            if (info == null)
            {
                // Public or private: with auto-reconnect on, a node that cannot be found yet is kept and retried until
                // the user disconnects it (spec 4.6 "retry until you disconnect"; the public case used to drop it).
                string what = c.PrivateAddress is { } priv ? $"Cannot find host {priv.Host}." : $"Cannot find node {c.Node}.";
                if (c.Wanted && Config.Settings.AutoReconnect)
                {
                    ErrorOccurred?.Invoke($"{what} NodeX will keep trying until you disconnect it.");
                    StartReconnect(c, immediate: false);
                    return null;
                }
                ErrorOccurred?.Invoke(what);
                RemoveConnection(c, byUser: false, c.PrivateAddress is { } p
                    ? $"cannot find host {p.Host}"
                    : "its address could not be looked up");
                return null;
            }
            return info;
        }
        catch (Exception ex)
        {
            RemoveConnection(c, byUser: false, $"the connect did not start: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    /// <summary>Resolves and starts a call, publishing the new link. Used for the first dial and for retries.</summary>
    private async Task<NodeInfo?> DialAsync(NodeConnection c, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var registration = PrepareRegistrationForDial(c.DisplayNode);    // runs while the node is looked up
        NodeInfo? info = c.PrivateAddress is { } priv
            ? await _resolver.ResolvePrivateAsync(priv, c.PrivateName, cancellationToken)
            : await _resolver.ResolveNodeAsync(c.Node, cancellationToken);
        Log.Detail("dial", $"node {c.DisplayNode}: {(c.IsPrivate ? "private " : "")}resolve {(info == null ? "failed" : "ok -> " + info.EndPoint)} in {sw.ElapsedMilliseconds} ms");
        if (info == null) return null;
        ThrowIfAbandoned(c);
        c.Info = info;

        if (!registration.IsCompleted)
        {
            var regClock = Stopwatch.StartNew();
            var done = await Task.WhenAny(registration, Task.Delay(_policy.RegistrationWait, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            Log.Detail("dial", done == registration
                ? $"node {c.Node}: re-registered first (ok={registration.Result}), waited {regClock.ElapsedMilliseconds} ms"
                : $"node {c.Node}: re-registration still pending after {regClock.ElapsedMilliseconds} ms; dialling anyway");
            ThrowIfAbandoned(c);
        }

        // On the shared call socket, so the far node sees the address the registry recorded.
        var link = new NodeLink(
            Config.Settings with { MicGain = LiveMicGain, RxGain = LiveRxGain, RxAutoLevel = LiveRxAutoLevel },
            info, _audioFactory(), _audioOptions, _configureSession, Transmit, EnsureEndpoint());
        link.Ptt.ListenOnlyReason = $"Node {c.Node} is listen-only. Choose it as the transmit node to talk on it.";
        link.Audio.Muted = c.Muted;
        link.Error += m => ErrorOccurred?.Invoke(ForNode(c, m));
        link.Session.StateChanged += state => OnLinkState(c, link, state);

        var dialClock = Stopwatch.StartNew();
        link.Session.StateChanged += state =>
            Log.Detail("iax", $"node {c.Node}: session {state} {dialClock.ElapsedMilliseconds} ms after dial started");

        c.Link = link;

        // A hang-up that slipped in before the link was published saw no call to end. Now that it is visible to
        // DisconnectAsync, check once more; from here on a hang-up finds and closes it.
        if (!IsCurrent(c))
        {
            if (c.TryClearLink(link)) await link.DisposeAsync();
            throw new OperationCanceledException($"Node {c.Node} was disconnected before the call started.");
        }

        link.Ptt.PttStateChanged += (active, _) => { if (active) RecordConnectionActivity(c); };
        _rxActivity.Attach(link);
        ApplyTransmitFlags();          // sets this link's TransmitEnabled from the current choice
        LinkAdded?.Invoke(link);
        ConnectionsChanged?.Invoke();
        UpdateActive();

        try
        {
            await link.ConnectAsync(cancellationToken);
        }
        catch
        {
            await EndLinkAsync(c, link, transient: true);
            throw;
        }
        return info;
    }

    /// <summary>
    /// Renews the registration before a dial when it has lapsed, or (over IAX2) when it is older than
    /// <see cref="ReconnectPolicy.PreDialRegistrationAge"/>. Completes with true straight away when nothing is needed.
    /// </summary>
    private Task<bool> PrepareRegistrationForDial(string node)
    {
        var registration = _registration;
        if (registration == null) return Task.FromResult(false);

        TimeSpan age = _lastRegistrationOk is { } ok ? UtcNow() - ok : TimeSpan.MaxValue;
        bool due = !IsRegistered || (registration.Transport == "IAX2" && age > _policy.PreDialRegistrationAge);
        if (!due) return Task.FromResult(true);

        Log.Detail("dial", $"node {node}: registering first ({(IsRegistered ? $"last accepted {age.TotalSeconds:0} s ago" : RegistrationStatus)})");
        return RefreshRegistrationAsync();
    }

    /// <summary>True while <paramref name="c"/> is still one of the nodes the user wants linked.</summary>
    private bool IsCurrent(NodeConnection c)
    {
        lock (_connLock) return c.Wanted && _connections.Contains(c);
    }

    private void ThrowIfAbandoned(NodeConnection c)
    {
        if (!IsCurrent(c))
            throw new OperationCanceledException($"Node {c.Node} was disconnected before the call started.");
    }

    /// <summary>The user hangs up every link: stops any reconnecting and closes them all.</summary>
    public async Task DisconnectAsync()
    {
        // All at once: each hang-up may wait a moment for its HANGUP to be acknowledged, and with several nodes
        // on a dead path those waits must not add up.
        await Task.WhenAll(SnapshotConnections().Select(DisconnectConnectionAsync));
        UpdateActive();
    }

    /// <summary>The user hangs up one node, leaving the others linked.</summary>
    public async Task DisconnectAsync(string node)
    {
        var c = FindConnection(node);
        if (c != null) await DisconnectConnectionAsync(c);
    }

    private async Task DisconnectConnectionAsync(NodeConnection c)
    {
        c.Wanted = false;
        await StopReconnectAsync(c);

        var link = c.Link;
        const string reason = "hung up from this device (Disconnect, the car screen or the notification)";
        if (link != null) await EndLinkAsync(c, link, transient: false, byUser: true, reason);
        // Always: if a transient teardown (network change, link loss) cleared the link first, EndLinkAsync above did
        // nothing and the node stayed listed with no call, holding a slot (2026-11-21 review). Idempotent.
        RemoveConnection(c, byUser: true, reason);
    }

    // ----- transmit target, mute -----

    /// <summary>
    /// Chooses the one linked node the user transmits to; every other link becomes listen-only (an active key on the
    /// old one is released). <c>null</c> means no transmit node. Returns false when <paramref name="node"/> is not linked.
    /// </summary>
    public bool SetTransmitNode(string? node)
    {
        lock (_connLock)
        {
            if (node != null && !_connections.Any(c => c.Node == node)) return false;
            if (_transmitNode == node) return true;
            _transmitNode = node;
        }

        ApplyTransmitFlags();
        Notice?.Invoke(node == null ? "No transmit node chosen." : $"Transmitting to node {node}.");
        ConnectionsChanged?.Invoke();
        return true;
    }

    /// <summary>Silences (or restores) the received audio of one linked node. It is still received and shown.</summary>
    public void SetMuted(string node, bool muted)
    {
        var c = FindConnection(node);
        if (c == null || c.Muted == muted) return;
        c.Muted = muted;
        if (c.Link is { } link) link.Audio.Muted = muted;
        ConnectionsChanged?.Invoke();
    }

    /// <summary>Pushes the transmit choice onto every connection and live link.</summary>
    private void ApplyTransmitFlags()
    {
        List<(NodeConnection C, bool Tx)> items;
        lock (_connLock)
        {
            items = _connections.Select(c => (c, c.Node == _transmitNode)).ToList();
            foreach (var (c, tx) in items) c.IsTransmitTarget = tx;
        }

        // Outside the lock: clearing TransmitEnabled on a keyed link starts an async unkey.
        foreach (var (c, tx) in items)
            if (c.Link is { } link) link.Ptt.TransmitEnabled = tx;
    }

    /// <summary>
    /// Forgets a node that is no longer wanted. If it was the transmit node, transmitting moves to the one remaining
    /// link only when the user caused it (hung up); if the far end dropped us, the choice is left empty so a
    /// transmission is never silently redirected to a different node.
    /// </summary>
    /// <param name="reason">Why, for the troubleshooting log: every node that leaves the list says what removed it.</param>
    private void RemoveConnection(NodeConnection c, bool byUser, string reason)
    {
        bool lostTransmit = false;
        int remaining;
        lock (_connLock)
        {
            if (!_connections.Remove(c)) return;
            Log.Info("link", $"node {c.Node}: no longer linked and will not be redialled: {reason}");
            c.Wanted = false;
            if (_transmitNode == c.Node)
            {
                lostTransmit = true;
                _transmitNode = byUser && _connections.Count == 1 ? _connections[0].Node : null;
            }
            remaining = _connections.Count;
        }

        c.Abandoned.Cancel();          // stops a connect for this node that is still waiting or resolving
        ApplyTransmitFlags();
        UpdateActive();
        if (lostTransmit && remaining > 0)
        {
            string? now = TransmitNode;
            Notice?.Invoke(now != null
                ? $"Node {c.Node} ended; transmitting to node {now}."
                : $"Node {c.Node} ended. Choose a node to transmit to.");
        }
        ConnectionsChanged?.Invoke();
    }

    /// <summary>Publishes "a link is wanted" (and which nodes) from the current connections.</summary>
    private void UpdateActive()
    {
        string[] nodes;
        lock (_connLock) nodes = _connections.Where(c => c.Wanted).Select(c => c.Node).ToArray();
        SetActive(nodes.Length > 0, nodes.Length > 0 ? string.Join(", ", nodes) : null);
    }

    private void OnLinkState(NodeConnection c, NodeLink link, Iax2SessionState state)
    {
        ConnectionsChanged?.Invoke();

        // Handle off the receive thread: disposing a session from inside its own event handler would
        // wait on itself.
        if (state == Iax2SessionState.Disconnected && ReferenceEquals(c.Link, link))
        {
            _ = Task.Run(() => HandleLinkEndedAsync(c, link));
        }
    }

    private async Task HandleLinkEndedAsync(NodeConnection c, NodeLink link)
    {
        try
        {
            if (!ReferenceEquals(c.Link, link)) return;
            if (IsReconnectRunning(c)) return;           // the reconnect loop owns the links it creates

            // Exactly once per call. A link that drops just as its reconnect loop finishes is reported both by its
            // own state event and by the loop's last look; handling it twice counted two retries for one hang-up.
            if (Interlocked.Exchange(ref link.EndHandled, 1) != 0) return;

            var reason = link.DisconnectReason;
            string node = c.DisplayNode;
            Log.Info("link", $"node {node}: call ended, reason={reason}, wasConnected={link.WasConnected}, " +
                             $"up={link.ConnectedDuration.TotalSeconds:0.0} s, remoteCause=\"{link.RemoteCause}\"");

            // Say how long it had been up: "lost after 4 min" (network/sleep) reads very differently from
            // "hung up after 0:20" (the far node dropped us). Goes into the copyable event log.
            string up = link.WasConnected
                ? $" after {(int)link.ConnectedDuration.TotalMinutes} min {link.ConnectedDuration.Seconds:00} s up"
                : "";

            // The remote accepted the call and hung up almost at once. Verified live: this is what an
            // ASL3 node does while its own node list does not contain this node yet (e.g. right after
            // registering or after the address changed). It resolves itself within minutes, so retry
            // with back-off instead of reporting a failure.
            bool earlyHangup = reason == Iax2DisconnectReason.RemoteHangup
                               && link.WasConnected
                               && link.ConnectedDuration < _policy.EarlyHangupWindow
                               && !link.Session.RemoteRequestedDisconnect;   // a deliberate "!!DISCONNECT!!" is never retried

            if (earlyHangup && c.Wanted && Config.Settings.AutoReconnect)
            {
                int n = Interlocked.Increment(ref c.EarlyHangups);
                if (ReconnectPolicy.WithinLimit(n, _policy.EarlyHangupMaxRetries))
                {
                    var delay = BackoffDelay(n);
                    await EndLinkAsync(c, link, transient: true);
                    Notice?.Invoke(
                        (c.IsPrivate
                            ? $"Node {node} hung up straight after accepting; that server probably doesn't know this node. " +
                              "If it doesn't load the public AllStarLink node list, its owner must add this node to [nodes] in rpt.conf (see Private nodes, help). "
                            : $"Node {node} hung up straight after accepting; it probably doesn't know this node yet (a busy hub can take 10 minutes or more). ") +
                        $"Retrying in {Math.Ceiling(delay.TotalSeconds):0} s ({ReconnectPolicy.Count("retry", n, _policy.EarlyHangupMaxRetries)}).");
                    StartReconnect(c, immediate: false, initialDelay: delay, attemptOffset: n - 1);
                    return;
                }

                ErrorOccurred?.Invoke(
                    $"Node {node} keeps hanging up right after accepting the link. It probably does not " +
                    "recognise this node yet (its node list can take many minutes to refresh after registering). Try again later.");
                Volatile.Write(ref c.EarlyHangups, 0);
                c.Wanted = false;
                await EndLinkAsync(c, link, transient: false,
                    reason: $"it hung up straight after accepting {n} times in a row (retry limit {_policy.EarlyHangupMaxRetries})");
                return;
            }

            if (link.WasConnected && !earlyHangup) Volatile.Write(ref c.EarlyHangups, 0);   // it really was up
            if (link.ConnectedDuration >= _policy.StableLinkTime) Volatile.Write(ref c.RemoteDrops, 0);   // and healthy

            // The far node hung up an established link without asking to disconnect. That is not somebody ending
            // the link on purpose (app_rpt sends "!!DISCONNECT!!" first when it is): its Asterisk restarted, or it
            // gave up on packets we could not answer during a network gap too short for our own keep-alive watchdog
            // to notice. app_rpt redials its own links in exactly this case, so do the same (until the user disconnects, unless the policy sets a limit).
            bool deliberate = link.Session.RemoteRequestedDisconnect;
            bool remoteDrop = reason == Iax2DisconnectReason.RemoteHangup && link.WasConnected && !earlyHangup && !deliberate;
            int drops = 0;

            if (remoteDrop && c.Wanted && Config.Settings.AutoReconnect)
            {
                drops = Interlocked.Increment(ref c.RemoteDrops);
                if (ReconnectPolicy.WithinLimit(drops, _policy.RemoteHangupMaxRetries))
                {
                    var delay = BackoffDelay(drops);
                    await EndLinkAsync(c, link, transient: true);
                    Notice?.Invoke(
                        $"Node {node} dropped the link{up} without asking to disconnect (a restart or a network gap). " +
                        $"Reconnecting in {Math.Ceiling(delay.TotalSeconds):0} s ({ReconnectPolicy.Count("try", drops, _policy.RemoteHangupMaxRetries)}).");
                    StartReconnect(c, immediate: false, initialDelay: delay);
                    return;
                }
                Volatile.Write(ref c.RemoteDrops, 0);
            }

            // Only a link that was up and then died is worth re-dialling. A call that was never answered,
            // or that the other side ended on purpose, is not.
            bool recoverable = c.Wanted
                               && Config.Settings.AutoReconnect
                               && link.WasConnected
                               && reason == Iax2DisconnectReason.LinkLost;

            if (recoverable)
            {
                await EndLinkAsync(c, link, transient: true);
                Notice?.Invoke($"Link to node {node} lost{up} (no reply to keep-alives).");
                StartReconnect(c, immediate: false);
                return;
            }

            // A first dial that got no answer at all, or "busy". The node may be restarting, briefly offline, or
            // dropping packets from an address it does not know yet. Owner decision 2026-11-19: keep trying (with the
            // usual back-off, up to a minute apart) until the user disconnects, instead of giving up after one try.
            // An explicit rejection is still final.
            bool keepTrying = c.Wanted
                              && Config.Settings.AutoReconnect
                              && !link.WasConnected
                              && reason is Iax2DisconnectReason.LinkLost or Iax2DisconnectReason.RemoteBusy;
            if (keepTrying)
            {
                if (reason == Iax2DisconnectReason.LinkLost && !c.IsPrivate) _resolver.Invalidate(c.Node);   // look the address up afresh
                await EndLinkAsync(c, link, transient: true);
                Notice?.Invoke(reason == Iax2DisconnectReason.RemoteBusy
                    ? $"Node {node} is busy. NodeX will keep trying until you disconnect it."
                    : $"No response from node {node} (it may be offline or not accepting this node yet). NodeX will keep trying until you disconnect it.");
                StartReconnect(c, immediate: false);
                return;
            }

            switch (reason)
            {
                case Iax2DisconnectReason.LinkLost when !link.WasConnected:
                    if (!c.IsPrivate) _resolver.Invalidate(c.Node);     // the address may be out of date: look it up afresh next time
                    ErrorOccurred?.Invoke($"No response from node {node}.");
                    break;
                case Iax2DisconnectReason.LinkLost:
                    ErrorOccurred?.Invoke($"Link to node {node} lost{up}.");
                    break;
                case Iax2DisconnectReason.RemoteRejected:
                    ErrorOccurred?.Invoke($"Node {node} rejected the connection.");
                    break;
                case Iax2DisconnectReason.RemoteBusy:
                    ErrorOccurred?.Invoke($"Node {node} is busy.");
                    break;
                case Iax2DisconnectReason.RemoteHangup when deliberate:
                    Notice?.Invoke($"Node {node} disconnected{up}: the far node ended the link on purpose, so NodeX is not reconnecting.");
                    break;
                case Iax2DisconnectReason.RemoteHangup when drops > 1:
                    ErrorOccurred?.Invoke(
                        $"Node {node} disconnected{up}: it has dropped the link {drops} times in a row, so NodeX has stopped reconnecting. Connect again when you want to retry.");
                    break;
                case Iax2DisconnectReason.RemoteHangup:
                    Notice?.Invoke($"Node {node} disconnected{up}: the far node hung up" +
                        (link.RemoteCause.Length > 0 ? $" (reason: {link.RemoteCause})" : " (no reason given)") +
                        ", so NodeX is not reconnecting.");
                    break;
            }

            c.Wanted = false;
            await EndLinkAsync(c, link, transient: false, reason:
                $"call ended with {reason}" +
                (deliberate ? ", the far node asked to disconnect" : "") +
                (drops > 0 ? $", dropped {drops} times in a row" : "") +
                (Config.Settings.AutoReconnect ? "" : ", auto-reconnect is off") +
                (link.WasConnected ? "" : ", never connected"));
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"Error handling link loss: {ex.Message}");
        }
    }

    /// <param name="transient">True when another attempt follows, so the connection is kept.</param>
    /// <param name="byUser">The user hung up (see <see cref="RemoveConnection"/>).</param>
    /// <param name="reason">Why the node is being forgotten (only used when not <paramref name="transient"/>).</param>
    private void OnRxNodeBecameActive(string node)
    {
        var c = FindConnection(node);
        if (c == null || c.Muted) return;
        RecordConnectionActivity(c);
    }

    private void RecordConnectionActivity(NodeConnection c)
    {
        c.NoteActivity(Interlocked.Increment(ref _activitySeq));
        try { LinkActivityChanged?.Invoke(c.Node); }
        catch (Exception) { /* a UI subscriber's error must not drop audio/network frames */ }
    }

    private async Task EndLinkAsync(NodeConnection c, NodeLink link, bool transient, bool byUser = false, string reason = "")
    {
        if (!c.TryClearLink(link)) return;
        _rxActivity.Detach(link);

        try
        {
            await link.DisposeAsync();
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"Error while closing link: {ex.Message}");
        }

        LinkRemoved?.Invoke(link);
        ConnectionsChanged?.Invoke();
        if (!transient) RemoveConnection(c, byUser, reason);
    }

    private void SetActive(bool active, string? node)
    {
        lock (_activeLock)
        {
            if (_active == active && (!active || _activeNode == node)) return;
            _active = active;
            _activeNode = active ? node : null;
        }
        LinkActiveChanged?.Invoke(active, active ? node : null);
        PublishBackground();
    }

    // =====================================================================================
    // Auto-reconnect (per node)
    // =====================================================================================

    private static bool IsReconnectRunning(NodeConnection c)
    {
        lock (c.ReconnectLock) return c.ReconnectCts != null;
    }

    /// <summary>Delay before retry number <paramref name="n"/> (1-based): InitialDelay, doubling, capped at MaxDelay.</summary>
    private TimeSpan BackoffDelay(int n)
    {
        double ticks = _policy.InitialDelay.Ticks * Math.Pow(_policy.Factor, Math.Max(0, n - 1));
        return TimeSpan.FromTicks((long)Math.Min(ticks, _policy.MaxDelay.Ticks));
    }

    private void StartReconnect(NodeConnection c, bool immediate, TimeSpan? initialDelay = null, int attemptOffset = 0)
    {
        lock (c.ReconnectLock)
        {
            if (_disposed || c.ReconnectCts != null || !c.Wanted) return;

            var cts = new CancellationTokenSource();
            c.ReconnectCts = cts;
            c.ReconnectTask = Task.Run(() => ReconnectLoopAsync(c, immediate, cts, initialDelay, attemptOffset));
        }
    }

    private static async Task StopReconnectAsync(NodeConnection c)
    {
        CancellationTokenSource? cts;
        Task task;
        lock (c.ReconnectLock)
        {
            cts = c.ReconnectCts;
            task = c.ReconnectTask;
        }
        if (cts == null) return;

        cts.Cancel();
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // The loop is cancelled; do not let a stuck attempt block a user hang-up.
        }
    }

    /// <summary>Records one node's recovery status and republishes the combined <see cref="Reconnect"/>.</summary>
    private void PublishReconnect(NodeConnection c, ReconnectStatus status)
    {
        c.Reconnect = status;

        ReconnectStatus combined;
        lock (_connLock)
        {
            // The first node that is recovering speaks for all; per-node status is on each NodeConnection.
            var active = _connections.Select(x => x.Reconnect).Where(s => s.Active).Cast<ReconnectStatus?>().FirstOrDefault();
            combined = active ?? ReconnectStatus.Inactive;
        }

        Reconnect = combined;
        ReconnectChanged?.Invoke(combined);
        ConnectionsChanged?.Invoke();
    }

    private async Task ReconnectLoopAsync(
        NodeConnection c, bool immediate, CancellationTokenSource cts, TimeSpan? initialDelay = null, int attemptOffset = 0)
    {
        var ct = cts.Token;
        string target = c.DisplayNode;
        int attempt = attemptOffset;
        TimeSpan delay = immediate ? TimeSpan.Zero : initialDelay ?? _policy.InitialDelay;
        bool recovered = false;

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                if (delay > TimeSpan.Zero)
                {
                    PublishReconnect(c, new(true, attempt + 1, delay,
                        $"Reconnecting to node {target} in {Math.Ceiling(delay.TotalSeconds):0} s (attempt {attempt + 1}).",
                        DateTimeOffset.UtcNow + delay));
                    await WaitAsync(delay, ct);      // a network change cuts the wait short
                }

                while (_monitor != null && !_monitor.IsAvailable)
                {
                    PublishReconnect(c, new(true, attempt + 1, null, "Waiting for a network connection…"));
                    await WaitAsync(TimeSpan.FromSeconds(5), ct);
                }

                if (!Config.Settings.AutoReconnect)
                {
                    // Turned off while this loop was waiting: never dial again on our own.
                    Notice?.Invoke($"Auto-reconnect is off, so NodeX stopped reconnecting to node {target}.");
                    RemoveConnection(c, byUser: false, "auto-reconnect was turned off while reconnecting");
                    return;
                }

                attempt++;
                PublishReconnect(c, new(true, attempt, null, $"Reconnecting to node {target} (attempt {attempt})…"));

                var attemptClock = Stopwatch.StartNew();
                Log.Detail("reconnect", $"node {target}: attempt {attempt} starting (delay was {delay.TotalSeconds:0.0} s)");
                var outcome = await TryReconnectOnceAsync(c, ct);
                bool succeeded = outcome == AttemptOutcome.Connected;
                Log.Info("reconnect", $"node {target}: attempt {attempt} {outcome.ToString().ToLowerInvariant()} after {attemptClock.ElapsedMilliseconds} ms");

                if (outcome == AttemptOutcome.Rejected)
                {
                    // An explicit REJECT is final (spec 4.6), also during recovery: retrying it forever helps nobody.
                    ErrorOccurred?.Invoke($"Node {target} rejected the connection, so NodeX has stopped reconnecting.");
                    RemoveConnection(c, byUser: false, "the node rejected the reconnect");
                    return;
                }

                if (succeeded)
                {
                    // Do not claim success yet: a node that is not yet in its hub's list accepts and hangs up at once.
                    // The announcement waits until the call has survived the early-hangup window.
                    _ = AnnounceReconnectWhenSettledAsync(c, c.Link, attempt);
                    recovered = true;
                    return;
                }

                if (_policy.MaxAttempts is { } max && attempt >= max)
                {
                    ErrorOccurred?.Invoke($"Gave up reconnecting to node {target} after {attempt} attempts.");
                    RemoveConnection(c, byUser: false, $"gave up reconnecting after {attempt} attempts");
                    return;
                }

                delay = delay == TimeSpan.Zero
                    ? _policy.InitialDelay
                    : TimeSpan.FromTicks(Math.Min((long)(delay.Ticks * _policy.Factor), _policy.MaxDelay.Ticks));
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped by the user or by shutdown.
        }
        finally
        {
            lock (c.ReconnectLock)
            {
                if (ReferenceEquals(c.ReconnectCts, cts)) c.ReconnectCts = null;
            }
            PublishReconnect(c, ReconnectStatus.Inactive);

            // The recovered link may have dropped while we still owned it; make sure that is noticed.
            if (recovered && c.Link is { } live && live.Session.State == Iax2SessionState.Disconnected)
            {
                _ = Task.Run(() => HandleLinkEndedAsync(c, live));
            }
        }
    }

    /// <summary>
    /// Raises "Reconnected to node N" only once the recovered call is still up after the early-hangup window.
    /// If it is hung up straight after accepting, the early-hangup path reports the retry instead.
    /// </summary>
    private async Task AnnounceReconnectWhenSettledAsync(NodeConnection c, NodeLink? link, int attempt)
    {
        if (link == null) return;
        try { await Task.Delay(_policy.EarlyHangupWindow); }
        catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException) { return; }

        if (!ReferenceEquals(c.Link, link) || link.Session.State != Iax2SessionState.Connected) return;

        Notice?.Invoke(attempt == 1
            ? $"Reconnected to node {c.DisplayNode}."
            : $"Reconnected to node {c.DisplayNode} after {attempt} attempts.");
    }

    private enum AttemptOutcome { Connected, Failed, Rejected }

    /// <summary>One dial attempt. Connected only when the call reaches Connected within the policy's timeout.</summary>
    private async Task<AttemptOutcome> TryReconnectOnceAsync(NodeConnection c, CancellationToken ct)
    {
        NodeInfo? info;
        try
        {
            info = await DialAsync(c, ct);
        }
        catch (OperationCanceledException)
        {
            if (c.Link is { } attemptLink) await EndLinkAsync(c, attemptLink, transient: true);
            throw;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"Reconnect attempt failed: {ex.Message}");
            return AttemptOutcome.Failed;
        }

        if (info == null)
        {
            ErrorOccurred?.Invoke(c.PrivateAddress is { } priv
                ? $"Cannot find host {priv.Host}; will retry."
                : $"Could not resolve node {c.Node}; will retry.");
            return AttemptOutcome.Failed;
        }

        var link = c.Link;
        if (link == null) return AttemptOutcome.Failed;

        bool connected;
        try
        {
            connected = await WaitForConnectedAsync(link, _policy.AttemptTimeout, ct);
        }
        catch (OperationCanceledException)
        {
            await EndLinkAsync(c, link, transient: true);
            throw;
        }

        if (!connected)
        {
            var reason = link.DisconnectReason;
            // Nothing answered at that address. The node may have moved since it was looked up (its public IP
            // changed), so the next attempt must not reuse the cached address for the rest of the cache lifetime.
            // A node that answered with REJECT or BUSY was found, so its address is kept.
            if (!c.IsPrivate && reason is not (Iax2DisconnectReason.RemoteRejected or Iax2DisconnectReason.RemoteBusy))
                _resolver.Invalidate(c.Node);
            await EndLinkAsync(c, link, transient: true);
            return reason == Iax2DisconnectReason.RemoteRejected ? AttemptOutcome.Rejected : AttemptOutcome.Failed;
        }
        return AttemptOutcome.Connected;
    }

    private static async Task<bool> WaitForConnectedAsync(NodeLink link, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnState(Iax2SessionState s)
        {
            if (s == Iax2SessionState.Connected) tcs.TrySetResult(true);
            // Connected and dropped again before we subscribed still counts as reached: the end handler takes it.
            else if (s == Iax2SessionState.Disconnected) tcs.TrySetResult(link.WasConnected);
        }

        link.Session.StateChanged += OnState;
        try
        {
            OnState(link.Session.State);   // it may already be there
            var finished = await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct));
            ct.ThrowIfCancellationRequested();
            return finished == tcs.Task && tcs.Task.Result;
        }
        finally
        {
            link.Session.StateChanged -= OnState;
        }
    }

    // =====================================================================================
    // Network changes
    // =====================================================================================

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Waits for <paramref name="delay"/> or until the network changes, whichever comes first.</summary>
    private async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timer = Task.Delay(delay, linked.Token);
        await Task.WhenAny(timer, _networkSignal.Task);
        linked.Cancel();
        ct.ThrowIfCancellationRequested();
    }

    private void OnNetworkChanged()
    {
        // Wake any reconnect wait immediately, then debounce the burst of events before acting.
        Log.Detail("net", $"network event received (monitor signature now \"{_monitor?.GetSignature()}\", available={_monitor?.IsAvailable})");
        Interlocked.Exchange(ref _networkSignal, NewSignal()).TrySetResult();

        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _debounceCts, cts)?.Cancel();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_policy.NetworkChangeDebounce, cts.Token);
                await HandleNetworkChangedAsync();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"Network change handling failed: {ex.Message}");
            }
        });
    }

    private async Task HandleNetworkChangedAsync()
    {
        var monitor = _monitor;
        if (monitor == null || _disposed) return;

        // DHCP renewals and similar noise leave the usable addresses unchanged: nothing to do.
        string signature = monitor.GetSignature();
        string previousSignature = Interlocked.Exchange(ref _lastSignature, signature);
        if (previousSignature == signature)
        {
            Log.Detail("net", $"network settled with the same address signature \"{signature}\"; nothing to do");
            return;
        }

        bool available = monitor.IsAvailable;
        var handoffClock = Stopwatch.StartNew();
        Log.Info("net", $"network changed: \"{previousSignature}\" -> \"{signature}\", available={available}, " +
                        $"autoReconnect={Config.Settings.AutoReconnect}");

        // A new address starts a new wait for every far node to learn it (each hangs up straight after accepting
        // until it has). The retries spent learning the previous address must not count against this one: a phone
        // that hops between Wi-Fi and cellular would otherwise run a node out of retries and lose it.
        foreach (var c in SnapshotConnections()) Volatile.Write(ref c.EarlyHangups, 0);

        // 1. A fresh call socket for the new path (the old one's NAT mapping belongs to the old network; calls still
        //    on it keep it until they end), then tell the registry our new address right away rather than at the
        //    next 60 s cycle: remote nodes validate callers against it.
        RetireEndpoint();
        Task<bool> registration = await RestartRegistrationForNewNetworkAsync(available);

        // 2. A call is bound to the old source address, so it cannot continue: dial again from the new one.
        //    Every established link is affected, so each node recovers on its own.
        if (!Config.Settings.AutoReconnect) return;

        // Pair each node with the call that was on the old network NOW: after the registration wait below, a newer
        // call (another network event, a redial) may be the node's link, and that one must not be torn down.
        var affected = SnapshotConnections()
            .Where(c => c.Wanted && !IsReconnectRunning(c) && c.Link is { WasConnected: true })
            .Select(c => (Conn: c, Old: c.Link!))
            .ToArray();
        if (affected.Length == 0) return;

        if (available)
        {
            var finished = await Task.WhenAny(registration, Task.Delay(_policy.RegistrationWait));
            Log.Info("net", finished == registration
                ? $"re-registration finished ok={registration.Result} {handoffClock.ElapsedMilliseconds} ms after acting on the change"
                : $"re-registration still pending after {_policy.RegistrationWait.TotalSeconds:0.#} s; re-dialling anyway");
        }

        Notice?.Invoke(available
            ? "Network changed; re-establishing the link from the new address."
            : "Network connection lost; will reconnect when it returns.");

        // Hang up every old call at once (each hang-up waits on a dead or changing path), then re-dial each node.
        await Task.WhenAll(affected.Select(async pair =>
        {
            var (c, link) = pair;
            if (!ReferenceEquals(c.Link, link) || IsReconnectRunning(c) || !c.Wanted) return;
            // The old source address is gone, so the HANGUP cannot be acknowledged: do not wait for it.
            link.Session.HangupAckWait = TimeSpan.Zero;
            await EndLinkAsync(c, link, transient: true);
            StartReconnect(c, immediate: available);
        }));
        Log.Info("net", $"handoff: {affected.Length} link(s) hung up and re-dial started {handoffClock.ElapsedMilliseconds} ms after acting on the change");
    }

    /// <summary>
    /// After a network change. An IAX2 registration is bound to the retired socket, so it is replaced with one on
    /// the new socket (this also gives IAX2 another chance if it had fallen back to HTTPS on the old network). An
    /// HTTPS-only registration is just refreshed. Returns the first attempt's result.
    /// </summary>
    private async Task<Task<bool>> RestartRegistrationForNewNetworkAsync(bool available)
    {
        var current = _registration;
        if (current == null) return Task.FromResult(false);
        if (current is not FallbackRegistration)
            return available ? RefreshRegistrationAsync() : Task.FromResult(false);

        await _gate.WaitAsync();
        try
        {
            if (_exited || _disposed || !ReferenceEquals(_registration, current)) return Task.FromResult(false);
            var cfg = Config;
            await StopRegistrationAsync();
            if (!cfg.IsComplete || !cfg.Settings.RegisterOnStartup) return Task.FromResult(false);
            return await StartRegistrationCoreAsync(cfg, CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }
    }

    // =====================================================================================

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _rxActivity.NodeBecameActive -= OnRxNodeBecameActive;
        _rxActivity.Dispose();

        ConfigService.Changed -= OnConfigSaved;
        if (_monitor != null)
        {
            _monitor.Changed -= OnNetworkChanged;
            if (_ownsMonitor) _monitor.Dispose();
        }
        Interlocked.Exchange(ref _debounceCts, null)?.Cancel();

        await DisconnectAsync();

        await _gate.WaitAsync();
        try
        {
            await StopRegistrationAsync();
        }
        finally
        {
            _gate.Release();
        }
        RetireEndpoint();
    }
}
