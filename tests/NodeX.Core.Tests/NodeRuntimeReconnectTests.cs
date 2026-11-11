using System.Net;
using System.Text;
using NodeX.Core.Settings;
using static NodeX.Core.Iax2Protocol;

namespace NodeX.Core.Tests;

internal sealed class FakeNetworkMonitor : INetworkMonitor
{
    public string Signature { get; set; } = "wifi=10.0.0.5";
    public bool Available { get; set; } = true;

    public event Action? Changed;
    public bool IsAvailable => Available;
    public string GetSignature() => Available ? Signature : "";

    public void Raise() => Changed?.Invoke();

    public void Switch(string signature)
    {
        Signature = signature;
        Available = true;
        Raise();
    }

    public void GoOffline()
    {
        Available = false;
        Raise();
    }

    public void Dispose() { }
}

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 15000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Condition not met in time");
            await Task.Delay(5);
        }
    }
}

public class NodeRuntimeReconnectTests : IDisposable
{
    private readonly string _settingsPath = Path.Combine(Path.GetTempPath(), "nodex-rc-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly InMemorySecretStore _secrets = new();
    private readonly FakeIaxRemote _remote = new();
    private readonly FakeNetworkMonitor _net = new();
    private readonly List<FakeAudioDevice> _devices = [];
    private int _registryRequests;

    public void Dispose()
    {
        _remote.Dispose();
        foreach (string f in new[] { _settingsPath, _settingsPath + ".tmp" })
            try { File.Delete(f); } catch (IOException) { }
    }

    private sealed class CountingHandler(Action onRequest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            onRequest();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ipaddr":"203.0.113.9","port":4569,"refresh":0,"data":"successfully registered"}""",
                    Encoding.UTF8, "application/json")
            });
        }
    }

    private static readonly ReconnectPolicy FastPolicy = new()
    {
        InitialDelay = TimeSpan.FromMilliseconds(40),
        MaxDelay = TimeSpan.FromMilliseconds(160),
        Factor = 2,
        NetworkChangeDebounce = TimeSpan.FromMilliseconds(30),
        RegistrationWait = TimeSpan.FromMilliseconds(800),
        AttemptTimeout = TimeSpan.FromMilliseconds(200)
    };

    private async Task<NodeRuntime> CreateAsync(
        NodeSettings? settings = null,
        ReconnectPolicy? policy = null,
        Action<Iax2Session>? configureSession = null)
    {
        var config = new NodeConfigService(new JsonFileSettingsStore(_settingsPath), _secrets);
        await config.SaveAsync(settings ?? new NodeSettings { NodeNumber = "55553", Callsign = "W1AW" }, "pw");

        var resolver = new NodeResolution();
        resolver.SetStaticOverride("2000", _remote.EndPoint);
        var http = new HttpClient(new CountingHandler(() => Interlocked.Increment(ref _registryRequests)));

        var runtime = new NodeRuntime(
            config,
            audioFactory: () => { var d = new FakeAudioDevice(); lock (_devices) _devices.Add(d); return d; },
            resolver: resolver,
            registrationFactory: cfg => new AslRegistrationService(cfg, http),
            networkMonitor: _net,
            reconnectPolicy: policy ?? FastPolicy,
            // Quick dead-link detection (3 x 40 ms) instead of the production 3 x 15 s.
            configureSession: configureSession ?? (s => { s.KeepAliveInterval = TimeSpan.FromMilliseconds(40); s.MaxMissedKeepAlives = 2; }));

        await runtime.StartAsync();
        return runtime;
    }

    /// <summary>Connects and completes the handshake; returns the first call's number.</summary>
    private async Task<ushort> ConnectAndAcceptAsync(NodeRuntime rt)
    {
        await rt.ConnectAsync("2000");
        var neu = await _remote.WaitForIaxAsync(IaxControlSubclass.New);
        await _remote.SendIaxAsync(IaxControlSubclass.Accept);
        await Wait.UntilAsync(() => rt.Link?.Session.State == Iax2SessionState.Connected);
        return neu.SourceCallNumber;
    }

    private async Task AcceptNewCallAsync(ushort previousCall)
    {
        await _remote.WaitForFrameAsync(f => f.FrameType == FrameType.IaxControl
                                             && f.Subclass == (byte)IaxControlSubclass.New
                                             && f.SourceCallNumber != previousCall, 5000);
        await _remote.SendIaxAsync(IaxControlSubclass.Accept);
    }

    /// <summary>Counts NEW frames from other calls arriving within the window.</summary>
    private async Task<int> NewCallsWithinAsync(ushort previousCall, int windowMs)
    {
        int count = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (sw.ElapsedMilliseconds < windowMs)
            {
                var f = await _remote.ReceiveFrameAsync(Math.Max(20, windowMs - (int)sw.ElapsedMilliseconds));
                if (f.FrameType == FrameType.IaxControl && f.Subclass == (byte)IaxControlSubclass.New
                    && f.SourceCallNumber != previousCall) count++;
            }
        }
        catch (OperationCanceledException) { }
        return count;
    }

    private static List<T> Collect<T>(Action<Action<T>> subscribe)
    {
        var list = new List<T>();
        subscribe(item => { lock (list) list.Add(item); });
        return list;
    }

    // ----- link loss -----

    [Fact]
    public async Task LostLink_IsRedialled_AndRecovers_WithoutDroppingBackgroundServices()
    {
        await using var rt = await CreateAsync();
        var activity = Collect<(bool, string?)>(add => rt.LinkActiveChanged += (a, n) => add((a, n)));
        var notices = Collect<string>(add => rt.Notice += add);
        var statuses = Collect<ReconnectStatus>(add => rt.ReconnectChanged += add);

        ushort first = await ConnectAndAcceptAsync(rt);
        var firstLink = rt.Link;

        // The remote now stays silent: no PONGs, so the keep-alive watchdog declares the link dead.
        await AcceptNewCallAsync(first);
        await Wait.UntilAsync(() => rt.Link != null && !ReferenceEquals(rt.Link, firstLink)
                                    && rt.Link.Session.State == Iax2SessionState.Connected);
        await Wait.UntilAsync(() => !rt.Reconnect.Active);

        lock (activity) Assert.Equal([(true, "2000")], activity);          // never flapped to false
        lock (statuses) Assert.Contains(statuses, s => s.Active && s.Attempt == 1);
        lock (notices) Assert.Contains(notices, n => n.Contains("lost")) ;
        lock (notices) Assert.Contains(notices, n => n.Contains("Reconnected to node 2000"));
        Assert.True(_devices.Count >= 2);                                   // a fresh audio device per call
    }

    // ----- early hangup (live-verified: an ASL3 node hangs up ~90 ms after ACCEPT until its node list knows us) -----

    [Fact]
    public async Task EarlyHangupAfterAccept_IsRetried_UntilTheRemoteAccepts()
    {
        await using var rt = await CreateAsync(configureSession: s => s.KeepAliveInterval = TimeSpan.FromSeconds(30));
        var notices = Collect<string>(add => rt.Notice += add);
        var errors = Collect<string>(add => rt.ErrorOccurred += add);
        var activity = Collect<(bool, string?)>(add => rt.LinkActiveChanged += (a, n) => add((a, n)));

        ushort first = await ConnectAndAcceptAsync(rt);
        await _remote.SendIaxAsync(IaxControlSubclass.Hangup);          // "Reported node ... cannot be found"

        // Second attempt: still not recognised.
        var second = await _remote.WaitForFrameAsync(f => f.FrameType == FrameType.IaxControl
                                                          && f.Subclass == (byte)IaxControlSubclass.New
                                                          && f.SourceCallNumber != first, 5000);
        await _remote.SendIaxAsync(IaxControlSubclass.Accept);
        await _remote.SendIaxAsync(IaxControlSubclass.Hangup);

        // Third attempt: the remote's list has refreshed, so the call stays up.
        await _remote.WaitForFrameAsync(f => f.FrameType == FrameType.IaxControl
                                             && f.Subclass == (byte)IaxControlSubclass.New
                                             && f.SourceCallNumber != first
                                             && f.SourceCallNumber != second.SourceCallNumber, 5000);
        await _remote.SendIaxAsync(IaxControlSubclass.Accept);

        await Wait.UntilAsync(() => rt.Link?.Session.State == Iax2SessionState.Connected && !rt.Reconnect.Active);
        await Task.Delay(150);                                          // it must stay up, not hang up again
        Assert.Equal(Iax2SessionState.Connected, rt.Link!.Session.State);

        lock (notices) Assert.Contains(notices, n => n.Contains("hung up straight after accepting") && n.Contains("retry 1 of"));
        lock (errors) Assert.Empty(errors);
        lock (activity) Assert.Equal([(true, "2000")], activity);       // link stayed "wanted" throughout
    }

    [Fact]
    public async Task EarlyHangup_GivesUpAfterTheConfiguredRetries_WithAHelpfulError()
    {
        var policy = FastPolicy with { EarlyHangupMaxRetries = 2 };
        await using var rt = await CreateAsync(policy: policy, configureSession: s => s.KeepAliveInterval = TimeSpan.FromSeconds(30));
        var errors = Collect<string>(add => rt.ErrorOccurred += add);
        var activity = Collect<(bool, string?)>(add => rt.LinkActiveChanged += (a, n) => add((a, n)));

        ushort previous = await ConnectAndAcceptAsync(rt);
        await _remote.SendIaxAsync(IaxControlSubclass.Hangup);          // 1st early hangup -> retry 1

        for (int i = 0; i < 2; i++)                                     // retries 2 (allowed) and 3 (over the limit)
        {
            var neu = await _remote.WaitForFrameAsync(f => f.FrameType == FrameType.IaxControl
                                                           && f.Subclass == (byte)IaxControlSubclass.New
                                                           && f.SourceCallNumber != previous, 5000);
            previous = neu.SourceCallNumber;
            await _remote.SendIaxAsync(IaxControlSubclass.Accept);
            await _remote.SendIaxAsync(IaxControlSubclass.Hangup);
        }

        await Wait.UntilAsync(() => { lock (errors) return errors.Count > 0; });
        await Wait.UntilAsync(() => rt.Link == null);
        lock (errors) Assert.Contains(errors, e => e.Contains("keeps hanging up") && e.Contains("node list"));
        await Wait.UntilAsync(() => { lock (activity) return activity.Count > 0 && !activity[^1].Item1; });
        Assert.Equal(0, await NewCallsWithinAsync(previous, 400));      // and it stops dialling
    }

    [Fact]
    public async Task RemoteHangup_LongAfterConnecting_IsNotRetried()
    {
        var policy = FastPolicy with { EarlyHangupWindow = TimeSpan.FromMilliseconds(50) };
        await using var rt = await CreateAsync(policy: policy, configureSession: s => s.KeepAliveInterval = TimeSpan.FromSeconds(30));
        var notices = Collect<string>(add => rt.Notice += add);

        ushort first = await ConnectAndAcceptAsync(rt);
        await Task.Delay(200);                                          // established well past the window
        await _remote.SendIaxAsync(IaxControlSubclass.Hangup);

        await Wait.UntilAsync(() => rt.Link == null);
        lock (notices) Assert.Contains(notices, n => n.Contains("disconnected"));
        Assert.Equal(0, await NewCallsWithinAsync(first, 400));
    }

    [Fact]
    public async Task EarlyHangup_IsNotRetried_WhenAutoReconnectIsOff()
    {
        await using var rt = await CreateAsync(
            new NodeSettings { NodeNumber = "55553", Callsign = "W1AW", AutoReconnect = false },
            configureSession: s => s.KeepAliveInterval = TimeSpan.FromSeconds(30));
        var notices = Collect<string>(add => rt.Notice += add);

        ushort first = await ConnectAndAcceptAsync(rt);
        await _remote.SendIaxAsync(IaxControlSubclass.Hangup);

        await Wait.UntilAsync(() => rt.Link == null);
        lock (notices) Assert.Contains(notices, n => n.Contains("disconnected"));
        Assert.Equal(0, await NewCallsWithinAsync(first, 400));
    }

    [Fact]
    public async Task UserDisconnect_DoesNotReconnect()
    {
        await using var rt = await CreateAsync();
        var activity = Collect<(bool, string?)>(add => rt.LinkActiveChanged += (a, n) => add((a, n)));
        ushort first = await ConnectAndAcceptAsync(rt);

        await rt.DisconnectAsync();

        Assert.Equal(0, await NewCallsWithinAsync(first, 400));
        Assert.Null(rt.Link);
        Assert.False(rt.Reconnect.Active);
        lock (activity) Assert.Equal([(true, "2000"), (false, null)], activity);
    }

    [Fact]
    public async Task RemoteHangup_DoesNotReconnect_AndSaysSo()
    {
        // A hangup right after ACCEPT is retried (see the early-hangup tests); one on an established
        // link is the remote ending the call on purpose.
        var policy = FastPolicy with { EarlyHangupWindow = TimeSpan.FromMilliseconds(50) };
        await using var rt = await CreateAsync(policy: policy, configureSession: s => s.KeepAliveInterval = TimeSpan.FromSeconds(30));
        var notices = Collect<string>(add => rt.Notice += add);
        ushort first = await ConnectAndAcceptAsync(rt);
        await Task.Delay(200);

        await _remote.SendIaxAsync(IaxControlSubclass.Hangup);
        await Wait.UntilAsync(() => rt.Link == null);

        Assert.Equal(0, await NewCallsWithinAsync(first, 400));
        lock (notices) Assert.Contains(notices, n => n.Contains("Node 2000 disconnected"));
    }

    [Fact]
    public async Task CallThatWasNeverAnswered_IsNotRetried_AndReportsNoResponse()
    {
        await using var rt = await CreateAsync(configureSession: s =>
        {
            s.InitialRetransmitTimeout = TimeSpan.FromMilliseconds(30);
            s.MaxRetransmits = 2;
        });
        var errors = Collect<string>(add => rt.ErrorOccurred += add);

        await rt.ConnectAsync("2000");
        var neu = await _remote.WaitForIaxAsync(IaxControlSubclass.New);     // never answered
        await Wait.UntilAsync(() => rt.Link == null);

        Assert.Equal(0, await NewCallsWithinAsync(neu.SourceCallNumber, 400));
        lock (errors) Assert.Contains(errors, e => e.Contains("No response from node 2000"));
    }

    [Fact]
    public async Task AutoReconnectDisabled_DoesNotRedial()
    {
        await using var rt = await CreateAsync(new NodeSettings { NodeNumber = "55553", Callsign = "W1AW", AutoReconnect = false });
        var errors = Collect<string>(add => rt.ErrorOccurred += add);
        ushort first = await ConnectAndAcceptAsync(rt);

        await Wait.UntilAsync(() => rt.Link == null);                        // watchdog ends it

        Assert.Equal(0, await NewCallsWithinAsync(first, 400));
        lock (errors) Assert.Contains(errors, e => e.Contains("Link to node 2000 lost"));
    }

    [Fact]
    public async Task FailedAttempts_BackOffExponentially_ThenGiveUp_WhenLimited()
    {
        await using var rt = await CreateAsync(policy: FastPolicy with { MaxAttempts = 3, AttemptTimeout = TimeSpan.FromMilliseconds(120) });
        var statuses = Collect<ReconnectStatus>(add => rt.ReconnectChanged += add);
        var errors = Collect<string>(add => rt.ErrorOccurred += add);
        var activity = Collect<(bool, string?)>(add => rt.LinkActiveChanged += (a, n) => add((a, n)));

        await ConnectAndAcceptAsync(rt);       // first call works; every re-dial is left unanswered

        await Wait.UntilAsync(() =>
        {
            lock (errors) return errors.Any(e => e.Contains("Gave up"));
        }, 8000);
        await Wait.UntilAsync(() => !rt.Reconnect.Active);

        double[] waits;
        lock (statuses) waits = statuses.Where(s => s.NextAttemptIn.HasValue).Select(s => s.NextAttemptIn!.Value.TotalMilliseconds).ToArray();
        Assert.Equal([40d, 80d, 160d], waits);                                // doubles, capped at MaxDelay

        lock (errors) Assert.Contains(errors, e => e.Contains("after 3 attempts"));
        Assert.Null(rt.Link);
        lock (activity) Assert.Equal((false, (string?)null), activity[^1]);
    }

    // ----- network handoff -----

    [Fact]
    public async Task NetworkChange_ReRegistersFirst_ThenRedialsImmediatelyFromTheNewAddress()
    {
        // Keep-alive is relaxed here so only the network change can end the call.
        await using var rt = await CreateAsync(configureSession: s => s.KeepAliveInterval = TimeSpan.FromSeconds(30));
        var notices = Collect<string>(add => rt.Notice += add);
        var activity = Collect<(bool, string?)>(add => rt.LinkActiveChanged += (a, n) => add((a, n)));
        ushort first = await ConnectAndAcceptAsync(rt);
        await Wait.UntilAsync(() => Volatile.Read(ref _registryRequests) >= 1);
        int before = Volatile.Read(ref _registryRequests);
        var firstLink = rt.Link;

        _net.Switch("cell=100.64.0.9");                                       // Wi-Fi -> cellular

        await _remote.WaitForFrameAsync(f => f.FrameType == FrameType.IaxControl
                                              && f.Subclass == (byte)IaxControlSubclass.New
                                              && f.SourceCallNumber != first, 5000);
        int registrationsAtRedial = Volatile.Read(ref _registryRequests);
        await _remote.SendIaxAsync(IaxControlSubclass.Accept);
        await Wait.UntilAsync(() => rt.Link != null && !ReferenceEquals(rt.Link, firstLink)
                                    && rt.Link.Session.State == Iax2SessionState.Connected);

        Assert.True(registrationsAtRedial > before, "the registry must hear about the new address before the re-dial");
        lock (notices) Assert.Contains(notices, n => n.Contains("Network changed"));
        lock (activity) Assert.Equal([(true, "2000")], activity);
    }

    [Fact]
    public async Task NetworkEventWithUnchangedAddresses_IsIgnored()
    {
        await using var rt = await CreateAsync(configureSession: s => s.KeepAliveInterval = TimeSpan.FromSeconds(30));
        ushort first = await ConnectAndAcceptAsync(rt);
        await Wait.UntilAsync(() => Volatile.Read(ref _registryRequests) >= 1);
        int before = Volatile.Read(ref _registryRequests);
        var link = rt.Link;

        _net.Raise();                                                          // e.g. a DHCP renewal
        _net.Raise();

        Assert.Equal(0, await NewCallsWithinAsync(first, 400));
        Assert.Same(link, rt.Link);
        Assert.Equal(before, Volatile.Read(ref _registryRequests));
    }

    [Fact]
    public async Task NetworkLoss_WaitsForTheNetwork_ThenReconnectsWhenItReturns()
    {
        await using var rt = await CreateAsync(configureSession: s => s.KeepAliveInterval = TimeSpan.FromSeconds(30));
        var statuses = Collect<ReconnectStatus>(add => rt.ReconnectChanged += add);
        ushort first = await ConnectAndAcceptAsync(rt);

        _net.GoOffline();

        await Wait.UntilAsync(() =>
        {
            lock (statuses) return statuses.Any(s => s.Active && s.Message.Contains("Waiting for a network"));
        });
        Assert.Null(rt.Link);
        Assert.Equal(0, await NewCallsWithinAsync(first, 300));                // nothing is sent while offline

        _net.Switch("wifi=192.168.1.9");

        await AcceptNewCallAsync(first);
        await Wait.UntilAsync(() => rt.Link?.Session.State == Iax2SessionState.Connected);
        await Wait.UntilAsync(() => !rt.Reconnect.Active);
    }

    [Fact]
    public async Task UserCanCancelWhileWaitingForTheNetwork()
    {
        await using var rt = await CreateAsync(configureSession: s => s.KeepAliveInterval = TimeSpan.FromSeconds(30));
        var activity = Collect<(bool, string?)>(add => rt.LinkActiveChanged += (a, n) => add((a, n)));
        ushort first = await ConnectAndAcceptAsync(rt);
        _net.GoOffline();
        await Wait.UntilAsync(() => rt.Reconnect.Active);

        await rt.DisconnectAsync();

        Assert.False(rt.Reconnect.Active);
        _net.Switch("wifi=192.168.1.9");                                       // network returns, but we were cancelled
        Assert.Equal(0, await NewCallsWithinAsync(first, 400));
        lock (activity) Assert.Equal((false, (string?)null), activity[^1]);
    }

    // ----- background presence: stay registered while idle, stop only on Exit -----

    [Fact]
    public async Task ConfiguredNode_IsKeptAliveInTheBackground_WhileIdle()
    {
        await using var rt = await CreateAsync();
        await Wait.UntilAsync(() => rt.Background.Registered);

        Assert.True(rt.Background.Needed);
        Assert.Null(rt.Background.LinkedNode);
        Assert.Contains("Ready to connect", rt.Background.Summary);
    }

    [Fact]
    public async Task Disconnecting_LeavesTheNodeRegisteredAndBackgroundOn()
    {
        await using var rt = await CreateAsync();
        var states = new List<BackgroundState>();
        rt.BackgroundChanged += states.Add;

        await ConnectAndAcceptAsync(rt);
        Assert.Equal("2000", rt.Background.LinkedNode);
        Assert.Contains("Linked to node 2000", rt.Background.Summary);

        await rt.DisconnectAsync();
        Assert.Null(rt.Background.LinkedNode);
        Assert.True(rt.Background.Needed);          // still registered and kept alive
        Assert.All(states, st => Assert.True(st.Needed));
    }

    [Fact]
    public async Task IpChange_WhileIdle_ReRegistersImmediately()
    {
        await using var rt = await CreateAsync();
        await Wait.UntilAsync(() => rt.Background.Registered);
        int before = Volatile.Read(ref _registryRequests);

        _net.Switch("cell=100.64.0.7");
        await Wait.UntilAsync(() => Volatile.Read(ref _registryRequests) > before);
        Assert.Null(rt.Link);                        // nothing was linked; it only refreshed the registration
    }

    [Fact]
    public async Task Resume_ReRegistersNow()
    {
        await using var rt = await CreateAsync();
        await Wait.UntilAsync(() => rt.Background.Registered);
        int before = Volatile.Read(ref _registryRequests);

        rt.NotifyResumed();
        await Wait.UntilAsync(() => Volatile.Read(ref _registryRequests) > before);
    }

    [Fact]
    public async Task Exit_StopsEverything_AndNothingRestarts()
    {
        var rt = await CreateAsync();
        await Wait.UntilAsync(() => rt.Background.Registered);
        await ConnectAndAcceptAsync(rt);
        var states = new List<BackgroundState>();
        rt.BackgroundChanged += states.Add;

        await rt.ExitAsync();

        Assert.True(rt.HasExited);
        Assert.False(rt.Background.Needed);
        Assert.Null(rt.Link);
        Assert.False(states[^1].Needed);

        int after = Volatile.Read(ref _registryRequests);
        _net.Switch("wifi=10.9.9.9");                // a later network change must not wake anything
        await rt.ApplyConfigAsync(rt.Config);        // nor a later config change
        await Task.Delay(300);
        Assert.Equal(after, Volatile.Read(ref _registryRequests));
        Assert.False(rt.Background.Needed);
    }

    [Fact]
    public async Task NotConfigured_NeedsNoBackgroundExecution()
    {
        await using var rt = await CreateAsync(new NodeSettings());   // invalid, so nothing is saved
        Assert.False(rt.Background.Needed);
    }

    [Fact]
    public async Task RegistrationOff_NeedsBackground_OnlyWhileLinked()
    {
        await using var rt = await CreateAsync(
            new NodeSettings { NodeNumber = "55553", Callsign = "W1AW", RegisterOnStartup = false });
        Assert.False(rt.Background.Needed);

        await ConnectAndAcceptAsync(rt);
        Assert.True(rt.Background.Needed);

        await rt.DisconnectAsync();
        Assert.False(rt.Background.Needed);
    }
}
