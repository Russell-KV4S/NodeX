using System.Net;
using NodeX.Core.Audio;
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

    private INetworkMonitor? _monitor;
    private AslRegistrationService? _registration;
    private CancellationTokenSource? _debounceCts;
    private TaskCompletionSource _networkSignal = NewSignal();
    private string _lastSignature = "";
    private bool _active;
    private string? _activeNode;
    private bool _started;
    private bool _disposed;
    private volatile bool _exited;

    public NodeRuntime(
        NodeConfigService configService,
        Func<IAudioDeviceService> audioFactory,
        NodeResolution? resolver = null,
        NodeAudioEngineOptions? audioOptions = null,
        Func<AslRegistrationConfig, AslRegistrationService>? registrationFactory = null,
        INetworkMonitor? networkMonitor = null,
        ReconnectPolicy? reconnectPolicy = null,
        Action<Iax2Session>? configureSession = null)
    {
        ConfigService = configService ?? throw new ArgumentNullException(nameof(configService));
        _audioFactory = audioFactory ?? throw new ArgumentNullException(nameof(audioFactory));
        _resolver = resolver ?? new NodeResolution();
        _audioOptions = audioOptions ?? new NodeAudioEngineOptions();
        _registrationFactory = registrationFactory ?? (cfg => new AslRegistrationService(cfg));
        _monitor = networkMonitor;
        _ownsMonitor = networkMonitor == null;      // created lazily in StartAsync so previews never watch the network
        _policy = reconnectPolicy ?? new ReconnectPolicy();
        _configureSession = configureSession;
        Config = new NodeConfig(new NodeSettings(), null);
    }

    public NodeConfigService ConfigService { get; }

    /// <summary>
    /// Whether this app may transmit. Listening always works. <see cref="TransmitPolicy.ListenOnly"/> follows the
    /// saved settings; platform heads set <see cref="TransmitPolicy.MicrophoneAllowed"/>.
    /// </summary>
    public TransmitPolicy Transmit { get; } = new();

    /// <summary>Latest loaded or saved configuration (including the password, in memory only).</summary>
    public NodeConfig Config { get; private set; }

    // ----- registration state -----

    public bool IsRegistered { get; private set; }
    public string RegistrationStatus { get; private set; } = "Not registered";
    public IPAddress? PerceivedAddress { get; private set; }

    /// <summary>Raised (on any thread) whenever registration status changes.</summary>
    public event Action? RegistrationChanged;

    // ----- link state -----

    /// <summary>The most nodes that can be linked at once (each costs a UDP socket and an audio output).</summary>
    public const int MaxLinks = 4;

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
        BackgroundChanged?.Invoke(next);
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
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started) return;
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
            Transmit.ListenOnly = cfg.Settings.ListenOnly;

            // Changing something unrelated to registration (a favourite, the PTT mode...) must not drop and
            // redo the registration.
            if (_registration != null && cfg.IsComplete && cfg.Settings.RegisterOnStartup
                && SameRegistration(previous, cfg))
                return;

            await StopRegistrationAsync();

            if (cfg.IsComplete && cfg.Settings.RegisterOnStartup)
            {
                _registration = _registrationFactory(cfg.ToRegistrationConfig());
                _registration.RegistrationStatusChanged += OnRegistrationStatus;
                await _registration.StartAsync(cancellationToken);
            }
            else
            {
                SetRegistration(false, cfg.IsComplete ? "Registration off" : "Not configured", null);
            }
        }
        finally
        {
            _gate.Release();
            PublishBackground();   // completeness / "register on startup" may have changed
        }
    }

    private static bool SameRegistration(NodeConfig a, NodeConfig b) =>
        a.Settings.NodeNumber == b.Settings.NodeNumber
        && a.Password == b.Password
        && a.Settings.IaxPort == b.Settings.IaxPort
        && a.Settings.RegistrationUrl == b.Settings.RegistrationUrl;

    private async Task StopRegistrationAsync()
    {
        var old = _registration;
        _registration = null;
        if (old == null) return;

        old.RegistrationStatusChanged -= OnRegistrationStatus;
        await old.DisposeAsync();
        SetRegistration(false, "Not registered", null);
    }

    private void OnRegistrationStatus(bool registered, string message) =>
        SetRegistration(registered, message, _registration?.PerceivedAddress);

    private void SetRegistration(bool registered, string status, IPAddress? address)
    {
        IsRegistered = registered;
        RegistrationStatus = status;
        PerceivedAddress = address;
        RegistrationChanged?.Invoke();
        PublishBackground();
    }

    /// <summary>Registers right now (outside the normal cycle), e.g. after the network changed.</summary>
    private async Task<bool> RefreshRegistrationAsync()
    {
        var registration = _registration;
        if (registration == null) return false;

        try
        {
            return await registration.SendHeartbeatAsync();
        }
        catch (Exception)
        {
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
        return count > 1 ? $"Node {c.Node}: {message}" : message;
    }

    /// <summary>
    /// Resolves <paramref name="targetNode"/> and dials it, <b>in addition to</b> any nodes already linked (up to
    /// <see cref="MaxLinks"/>). The first node linked becomes the transmit target; later ones are listen-only until
    /// <see cref="SetTransmitNode"/> chooses them. Connecting again to a node that is already linked redials just that
    /// node. Returns null when the node cannot be resolved.
    /// </summary>
    /// <exception cref="InvalidOperationException">Not configured, or <see cref="MaxLinks"/> nodes are already linked.</exception>
    public async Task<NodeInfo?> ConnectAsync(string targetNode, CancellationToken cancellationToken = default)
    {
        if (!Config.IsComplete)
            throw new InvalidOperationException("Enter your node number and password in Settings first.");
        targetNode = targetNode?.Trim() ?? "";
        if (!NodeSettingsValidator.IsValidNodeNumber(targetNode))
            throw new ArgumentException("Target must be a 3 to 7 digit node number.", nameof(targetNode));

        NodeConnection? existing;
        NodeConnection c;
        lock (_connLock)
        {
            existing = _connections.FirstOrDefault(x => x.Node == targetNode);
            if (existing == null && _connections.Count >= MaxLinks)
                throw new InvalidOperationException(
                    $"You can link up to {MaxLinks} nodes at once. Disconnect one first.");

            c = existing ?? new NodeConnection(targetNode);
            if (existing == null)
            {
                _connections.Add(c);
                if (_connections.Count == 1) _transmitNode = targetNode;   // the only link is where you transmit
            }
        }

        if (existing != null)
        {
            // Redial just this node: stop its recovery and end its call, keeping its transmit/mute choices.
            await StopReconnectAsync(c);
            var old = c.Link;
            if (old != null) await EndLinkAsync(c, old, transient: true);
            c.Wanted = true;
        }

        Interlocked.Exchange(ref c.EarlyHangups, 0);
        ApplyTransmitFlags();
        UpdateActive();
        ConnectionsChanged?.Invoke();

        try
        {
            var info = await DialAsync(c, cancellationToken);
            if (info == null) RemoveConnection(c, byUser: true);
            return info;
        }
        catch
        {
            RemoveConnection(c, byUser: true);
            throw;
        }
    }

    /// <summary>Resolves and starts a call, publishing the new link. Used for the first dial and for retries.</summary>
    private async Task<NodeInfo?> DialAsync(NodeConnection c, CancellationToken cancellationToken)
    {
        var info = await _resolver.ResolveNodeAsync(c.Node, cancellationToken);
        if (info == null) return null;
        c.Info = info;

        var link = new NodeLink(Config.Settings, info, _audioFactory(), _audioOptions, _configureSession, Transmit);
        link.Ptt.ListenOnlyReason = $"Node {c.Node} is listen-only. Choose it as the transmit node to talk on it.";
        link.Audio.Muted = c.Muted;
        link.Error += m => ErrorOccurred?.Invoke(ForNode(c, m));
        link.Session.StateChanged += state => OnLinkState(c, link, state);

        c.Link = link;
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

    /// <summary>The user hangs up every link: stops any reconnecting and closes them all.</summary>
    public async Task DisconnectAsync()
    {
        foreach (var c in SnapshotConnections())
            await DisconnectConnectionAsync(c);
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
        if (link != null) await EndLinkAsync(c, link, transient: false, byUser: true);
        else RemoveConnection(c, byUser: true);
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
    private void RemoveConnection(NodeConnection c, bool byUser)
    {
        bool lostTransmit = false;
        int remaining;
        lock (_connLock)
        {
            if (!_connections.Remove(c)) return;
            c.Wanted = false;
            if (_transmitNode == c.Node)
            {
                lostTransmit = true;
                _transmitNode = byUser && _connections.Count == 1 ? _connections[0].Node : null;
            }
            remaining = _connections.Count;
        }

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

            var reason = link.DisconnectReason;
            string node = link.Target.NodeNumber;

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
                               && link.ConnectedDuration < _policy.EarlyHangupWindow;

            if (earlyHangup && c.Wanted && Config.Settings.AutoReconnect)
            {
                int n = Interlocked.Increment(ref c.EarlyHangups);
                if (n <= _policy.EarlyHangupMaxRetries)
                {
                    var delay = BackoffDelay(n);
                    await EndLinkAsync(c, link, transient: true);
                    Notice?.Invoke(
                        $"Node {node} hung up straight after accepting; it may not know this node yet. " +
                        $"Retrying in {Math.Ceiling(delay.TotalSeconds):0} s (retry {n} of {_policy.EarlyHangupMaxRetries}).");
                    StartReconnect(c, immediate: false, initialDelay: delay, attemptOffset: n - 1);
                    return;
                }

                ErrorOccurred?.Invoke(
                    $"Node {node} keeps hanging up right after accepting the link. It probably does not " +
                    "recognise this node yet (its node list refreshes every few minutes after registering). Try again shortly.");
                Volatile.Write(ref c.EarlyHangups, 0);
                c.Wanted = false;
                await EndLinkAsync(c, link, transient: false);
                return;
            }

            if (link.WasConnected && !earlyHangup) Volatile.Write(ref c.EarlyHangups, 0);   // it really was up

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

            switch (reason)
            {
                case Iax2DisconnectReason.LinkLost when !link.WasConnected:
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
                case Iax2DisconnectReason.RemoteHangup:
                    Notice?.Invoke($"Node {node} disconnected{up}: the far node hung up, so NodeX is not reconnecting.");
                    break;
            }

            c.Wanted = false;
            await EndLinkAsync(c, link, transient: false);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"Error handling link loss: {ex.Message}");
        }
    }

    /// <param name="transient">True when another attempt follows, so the connection is kept.</param>
    /// <param name="byUser">The user hung up (see <see cref="RemoveConnection"/>).</param>
    private async Task EndLinkAsync(NodeConnection c, NodeLink link, bool transient, bool byUser = false)
    {
        if (!c.TryClearLink(link)) return;

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
        if (!transient) RemoveConnection(c, byUser);
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
        string target = c.Node;
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
                        $"Reconnecting to node {target} in {Math.Ceiling(delay.TotalSeconds):0} s (attempt {attempt + 1})."));
                    await WaitAsync(delay, ct);      // a network change cuts the wait short
                }

                while (_monitor != null && !_monitor.IsAvailable)
                {
                    PublishReconnect(c, new(true, attempt + 1, null, "Waiting for a network connection…"));
                    await WaitAsync(TimeSpan.FromSeconds(5), ct);
                }

                attempt++;
                PublishReconnect(c, new(true, attempt, null, $"Reconnecting to node {target} (attempt {attempt})…"));

                if (await TryReconnectOnceAsync(c, ct))
                {
                    Notice?.Invoke(attempt == 1
                        ? $"Reconnected to node {target}."
                        : $"Reconnected to node {target} after {attempt} attempts.");
                    recovered = true;
                    return;
                }

                if (_policy.MaxAttempts is { } max && attempt >= max)
                {
                    ErrorOccurred?.Invoke($"Gave up reconnecting to node {target} after {attempt} attempts.");
                    RemoveConnection(c, byUser: false);
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

    /// <summary>One dial attempt. True only when the call reaches Connected within the policy's timeout.</summary>
    private async Task<bool> TryReconnectOnceAsync(NodeConnection c, CancellationToken ct)
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
            return false;
        }

        if (info == null)
        {
            ErrorOccurred?.Invoke($"Could not resolve node {c.Node}; will retry.");
            return false;
        }

        var link = c.Link;
        if (link == null) return false;

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
            await EndLinkAsync(c, link, transient: true);
            return false;
        }
        return true;
    }

    private static async Task<bool> WaitForConnectedAsync(NodeLink link, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnState(Iax2SessionState s)
        {
            if (s == Iax2SessionState.Connected) tcs.TrySetResult(true);
            else if (s == Iax2SessionState.Disconnected) tcs.TrySetResult(false);
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
        if (Interlocked.Exchange(ref _lastSignature, signature) == signature) return;

        bool available = monitor.IsAvailable;

        // 1. Tell the registry our new address right away rather than at the next 60 s cycle; remote
        //    nodes validate callers against it.
        Task<bool> registration = available ? RefreshRegistrationAsync() : Task.FromResult(false);

        // 2. A call is bound to the old source address, so it cannot continue: dial again from the new one.
        //    Every established link is affected, so each node recovers on its own.
        if (!Config.Settings.AutoReconnect) return;

        var affected = SnapshotConnections()
            .Where(c => c.Wanted && !IsReconnectRunning(c) && c.Link is { WasConnected: true })
            .ToArray();
        if (affected.Length == 0) return;

        if (available)
        {
            await Task.WhenAny(registration, Task.Delay(_policy.RegistrationWait));
        }

        Notice?.Invoke(available
            ? "Network changed; re-establishing the link from the new address."
            : "Network connection lost; will reconnect when it returns.");

        foreach (var c in affected)
        {
            var link = c.Link;
            if (link == null) continue;
            await EndLinkAsync(c, link, transient: true);
            StartReconnect(c, immediate: available);
        }
    }

    // =====================================================================================

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

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
    }
}
