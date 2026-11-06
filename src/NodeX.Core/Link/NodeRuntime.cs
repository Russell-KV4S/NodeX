using System.Net;
using NodeX.Core.Audio;
using NodeX.Core.Settings;

namespace NodeX.Core;

/// <summary>
/// Application-level coordinator, independent of any UI toolkit: owns the saved configuration, the
/// AllStarLink registration and the (single) active <see cref="NodeLink"/>, and keeps the link alive
/// across failures: it re-dials a link that was established and then lost (with back-off), and when the
/// device's network changes it re-registers immediately and re-establishes the call from the new address.
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
    private readonly object _reconnectLock = new();
    private readonly object _activeLock = new();

    private INetworkMonitor? _monitor;
    private AslRegistrationService? _registration;
    private NodeLink? _link;
    private string? _desiredTarget;          // node the user wants to be linked to; null = nothing wanted
    private CancellationTokenSource? _reconnectCts;
    private Task _reconnectTask = Task.CompletedTask;
    private CancellationTokenSource? _debounceCts;
    private TaskCompletionSource _networkSignal = NewSignal();
    private string _lastSignature = "";
    private int _earlyHangups;               // consecutive "remote hung up right after ACCEPT" outcomes
    private bool _active;
    private string? _activeNode;
    private bool _started;
    private bool _disposed;

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

    /// <summary>Latest loaded or saved configuration (including the password, in memory only).</summary>
    public NodeConfig Config { get; private set; }

    // ----- registration state -----

    public bool IsRegistered { get; private set; }
    public string RegistrationStatus { get; private set; } = "Not registered";
    public IPAddress? PerceivedAddress { get; private set; }

    /// <summary>Raised (on any thread) whenever registration status changes.</summary>
    public event Action? RegistrationChanged;

    // ----- link state -----

    /// <summary>The active link, or null when not connected.</summary>
    public NodeLink? Link => Volatile.Read(ref _link);

    /// <summary>Raised when a link is created (non-null) or ends (null). May be raised on any thread.</summary>
    public event Action<NodeLink?>? LinkChanged;

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
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Config = cfg;
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
        }
    }

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
    // Connecting and disconnecting
    // =====================================================================================

    /// <summary>
    /// Resolves <paramref name="targetNode"/> and dials it. Returns null when the node cannot be resolved.
    /// Any existing link or reconnect cycle is stopped first.
    /// </summary>
    /// <exception cref="InvalidOperationException">The node number or password has not been configured.</exception>
    public async Task<NodeInfo?> ConnectAsync(string targetNode, CancellationToken cancellationToken = default)
    {
        if (!Config.IsComplete)
            throw new InvalidOperationException("Enter your node number and password in Settings first.");
        targetNode = targetNode?.Trim() ?? "";
        if (!NodeSettingsValidator.IsValidNodeNumber(targetNode))
            throw new ArgumentException("Target must be a 3 to 7 digit node number.", nameof(targetNode));

        await DisconnectAsync();

        _desiredTarget = targetNode;
        Volatile.Write(ref _earlyHangups, 0);
        try
        {
            var info = await DialAsync(targetNode, cancellationToken);
            if (info == null)
            {
                _desiredTarget = null;
                SetActive(false, null);
            }
            return info;
        }
        catch
        {
            _desiredTarget = null;
            SetActive(false, null);
            throw;
        }
    }

    /// <summary>Resolves and starts a call, publishing the new link. Used for the first dial and for retries.</summary>
    private async Task<NodeInfo?> DialAsync(string target, CancellationToken cancellationToken)
    {
        var info = await _resolver.ResolveNodeAsync(target, cancellationToken);
        if (info == null) return null;

        var link = new NodeLink(Config.Settings, info, _audioFactory(), _audioOptions, _configureSession);
        link.Error += m => ErrorOccurred?.Invoke(m);
        link.Session.StateChanged += state => OnLinkState(link, state);

        Volatile.Write(ref _link, link);
        LinkChanged?.Invoke(link);
        SetActive(true, info.NodeNumber);

        try
        {
            await link.ConnectAsync(cancellationToken);
        }
        catch
        {
            await EndLinkAsync(link, transient: true);
            throw;
        }
        return info;
    }

    /// <summary>The user hangs up: stops any reconnecting and closes the link.</summary>
    public async Task DisconnectAsync()
    {
        _desiredTarget = null;
        await StopReconnectAsync();

        var link = Link;
        if (link != null) await EndLinkAsync(link, transient: false);
        else SetActive(false, null);
    }

    private void OnLinkState(NodeLink link, Iax2SessionState state)
    {
        // Handle off the receive thread: disposing a session from inside its own event handler would
        // wait on itself.
        if (state == Iax2SessionState.Disconnected && ReferenceEquals(Link, link))
        {
            _ = Task.Run(() => HandleLinkEndedAsync(link));
        }
    }

    private async Task HandleLinkEndedAsync(NodeLink link)
    {
        try
        {
            if (!ReferenceEquals(Link, link)) return;
            if (IsReconnectRunning) return;           // the reconnect loop owns the links it creates

            var reason = link.DisconnectReason;
            string node = link.Target.NodeNumber;

            // The remote accepted the call and hung up almost at once. Verified live: this is what an
            // ASL3 node does while its own node list does not contain this node yet (e.g. right after
            // registering or after the address changed). It resolves itself within minutes, so retry
            // with back-off instead of reporting a failure.
            bool earlyHangup = reason == Iax2DisconnectReason.RemoteHangup
                               && link.WasConnected
                               && link.ConnectedDuration < _policy.EarlyHangupWindow;

            if (earlyHangup && _desiredTarget != null && Config.Settings.AutoReconnect)
            {
                int n = Interlocked.Increment(ref _earlyHangups);
                if (n <= _policy.EarlyHangupMaxRetries)
                {
                    var delay = BackoffDelay(n);
                    await EndLinkAsync(link, transient: true);
                    Notice?.Invoke(
                        $"Node {node} hung up straight after accepting; it may not know this node yet. " +
                        $"Retrying in {Math.Ceiling(delay.TotalSeconds):0} s (retry {n} of {_policy.EarlyHangupMaxRetries}).");
                    StartReconnect(immediate: false, initialDelay: delay, attemptOffset: n - 1);
                    return;
                }

                ErrorOccurred?.Invoke(
                    $"Node {node} keeps hanging up right after accepting the link. It probably does not " +
                    "recognise this node yet (its node list refreshes every few minutes after registering). Try again shortly.");
                Volatile.Write(ref _earlyHangups, 0);
                _desiredTarget = null;
                await EndLinkAsync(link, transient: false);
                return;
            }

            if (link.WasConnected && !earlyHangup) Volatile.Write(ref _earlyHangups, 0);   // it really was up

            // Only a link that was up and then died is worth re-dialling. A call that was never answered,
            // or that the other side ended on purpose, is not.
            bool recoverable = _desiredTarget != null
                               && Config.Settings.AutoReconnect
                               && link.WasConnected
                               && reason == Iax2DisconnectReason.LinkLost;

            if (recoverable)
            {
                await EndLinkAsync(link, transient: true);
                Notice?.Invoke($"Link to node {node} lost.");
                StartReconnect(immediate: false);
                return;
            }

            switch (reason)
            {
                case Iax2DisconnectReason.LinkLost when !link.WasConnected:
                    ErrorOccurred?.Invoke($"No response from node {node}.");
                    break;
                case Iax2DisconnectReason.LinkLost:
                    ErrorOccurred?.Invoke($"Link to node {node} lost.");
                    break;
                case Iax2DisconnectReason.RemoteRejected:
                    ErrorOccurred?.Invoke($"Node {node} rejected the connection.");
                    break;
                case Iax2DisconnectReason.RemoteBusy:
                    ErrorOccurred?.Invoke($"Node {node} is busy.");
                    break;
                case Iax2DisconnectReason.RemoteHangup:
                    Notice?.Invoke($"Node {node} disconnected.");
                    break;
            }

            _desiredTarget = null;
            await EndLinkAsync(link, transient: false);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"Error handling link loss: {ex.Message}");
        }
    }

    /// <param name="transient">True when another attempt follows, so the "link wanted" state is kept.</param>
    private async Task EndLinkAsync(NodeLink link, bool transient)
    {
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _link, null, link), link)) return;

        try
        {
            await link.DisposeAsync();
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"Error while closing link: {ex.Message}");
        }

        LinkChanged?.Invoke(null);
        if (!transient) SetActive(false, null);
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
    }

    // =====================================================================================
    // Auto-reconnect
    // =====================================================================================

    private bool IsReconnectRunning
    {
        get { lock (_reconnectLock) return _reconnectCts != null; }
    }

    /// <summary>Delay before retry number <paramref name="n"/> (1-based): InitialDelay, doubling, capped at MaxDelay.</summary>
    private TimeSpan BackoffDelay(int n)
    {
        double ticks = _policy.InitialDelay.Ticks * Math.Pow(_policy.Factor, Math.Max(0, n - 1));
        return TimeSpan.FromTicks((long)Math.Min(ticks, _policy.MaxDelay.Ticks));
    }

    private void StartReconnect(bool immediate, TimeSpan? initialDelay = null, int attemptOffset = 0)
    {
        lock (_reconnectLock)
        {
            if (_disposed || _reconnectCts != null || _desiredTarget == null) return;

            var cts = new CancellationTokenSource();
            _reconnectCts = cts;
            string target = _desiredTarget;
            _reconnectTask = Task.Run(() => ReconnectLoopAsync(target, immediate, cts, initialDelay, attemptOffset));
        }
    }

    private async Task StopReconnectAsync()
    {
        CancellationTokenSource? cts;
        Task task;
        lock (_reconnectLock)
        {
            cts = _reconnectCts;
            task = _reconnectTask;
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

    private void PublishReconnect(ReconnectStatus status)
    {
        Reconnect = status;
        ReconnectChanged?.Invoke(status);
    }

    private async Task ReconnectLoopAsync(
        string target, bool immediate, CancellationTokenSource cts, TimeSpan? initialDelay = null, int attemptOffset = 0)
    {
        var ct = cts.Token;
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
                    PublishReconnect(new(true, attempt + 1, delay,
                        $"Reconnecting to node {target} in {Math.Ceiling(delay.TotalSeconds):0} s (attempt {attempt + 1})."));
                    await WaitAsync(delay, ct);      // a network change cuts the wait short
                }

                while (_monitor != null && !_monitor.IsAvailable)
                {
                    PublishReconnect(new(true, attempt + 1, null, "Waiting for a network connection…"));
                    await WaitAsync(TimeSpan.FromSeconds(5), ct);
                }

                attempt++;
                PublishReconnect(new(true, attempt, null, $"Reconnecting to node {target} (attempt {attempt})…"));

                if (await TryReconnectOnceAsync(target, ct))
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
                    _desiredTarget = null;
                    SetActive(false, null);
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
            lock (_reconnectLock)
            {
                if (ReferenceEquals(_reconnectCts, cts)) _reconnectCts = null;
            }
            PublishReconnect(ReconnectStatus.Inactive);

            // The recovered link may have dropped while we still owned it; make sure that is noticed.
            if (recovered && Link is { } live && live.Session.State == Iax2SessionState.Disconnected)
            {
                _ = Task.Run(() => HandleLinkEndedAsync(live));
            }
        }
    }

    /// <summary>One dial attempt. True only when the call reaches Connected within the policy's timeout.</summary>
    private async Task<bool> TryReconnectOnceAsync(string target, CancellationToken ct)
    {
        NodeInfo? info;
        try
        {
            info = await DialAsync(target, ct);
        }
        catch (OperationCanceledException)
        {
            if (Link is { } attemptLink) await EndLinkAsync(attemptLink, transient: true);
            throw;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"Reconnect attempt failed: {ex.Message}");
            return false;
        }

        if (info == null)
        {
            ErrorOccurred?.Invoke($"Could not resolve node {target}; will retry.");
            return false;
        }

        var link = Link;
        if (link == null) return false;

        bool connected;
        try
        {
            connected = await WaitForConnectedAsync(link, _policy.AttemptTimeout, ct);
        }
        catch (OperationCanceledException)
        {
            await EndLinkAsync(link, transient: true);
            throw;
        }

        if (!connected)
        {
            await EndLinkAsync(link, transient: true);
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
        if (_desiredTarget == null || !Config.Settings.AutoReconnect) return;
        if (IsReconnectRunning) return;             // already retrying; it was woken by the signal

        var link = Link;
        if (link == null || !link.WasConnected) return;

        if (available)
        {
            await Task.WhenAny(registration, Task.Delay(_policy.RegistrationWait));
        }

        Notice?.Invoke(available
            ? "Network changed; re-establishing the link from the new address."
            : "Network connection lost; will reconnect when it returns.");

        await EndLinkAsync(link, transient: true);
        StartReconnect(immediate: available);
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
