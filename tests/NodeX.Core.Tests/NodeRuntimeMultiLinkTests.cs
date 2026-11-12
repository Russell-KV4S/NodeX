using System.Net;
using System.Text;
using NodeX.Core.Audio;
using NodeX.Core.Settings;
using static NodeX.Core.Iax2Protocol;

namespace NodeX.Core.Tests;

/// <summary>Several nodes linked at once: one transmit node, the rest listen-only.</summary>
public class NodeRuntimeMultiLinkTests : IDisposable
{
    private readonly string _settingsPath = Path.Combine(Path.GetTempPath(), "nodex-ml-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly InMemorySecretStore _secrets = new();
    // Nodes 2000, 3000 ... 10000: MaxLinks of them plus one more to prove the limit.
    private readonly Dictionary<string, FakeIaxRemote> _remotes =
        Enumerable.Range(2, 9).ToDictionary(i => (i * 1000).ToString(), _ => new FakeIaxRemote());
    private readonly FakeNetworkMonitor _net = new();
    private readonly List<FakeAudioDevice> _devices = [];

    public void Dispose()
    {
        foreach (var r in _remotes.Values) r.Dispose();
        foreach (string f in new[] { _settingsPath, _settingsPath + ".tmp" })
            try { File.Delete(f); } catch (IOException) { }
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ipaddr":"203.0.113.9","port":4569,"refresh":0,"data":"successfully registered"}""",
                    Encoding.UTF8, "application/json")
            });
    }

    private static readonly ReconnectPolicy FastPolicy = new()
    {
        InitialDelay = TimeSpan.FromMilliseconds(40),
        MaxDelay = TimeSpan.FromMilliseconds(160),
        Factor = 2,
        NetworkChangeDebounce = TimeSpan.FromMilliseconds(30),
        RegistrationWait = TimeSpan.FromMilliseconds(800),
        AttemptTimeout = TimeSpan.FromMilliseconds(400),
        RegistrationSettleDelay = TimeSpan.Zero   // production waits 15 s after registering; tests must not
    };

    private async Task<NodeRuntime> CreateAsync(bool quickKeepAlive = false, bool autoReconnect = true)
    {
        var config = new NodeConfigService(new JsonFileSettingsStore(_settingsPath), _secrets);
        await config.SaveAsync(new NodeSettings { NodeNumber = "55553", Callsign = "W1AW", AutoReconnect = autoReconnect }, "pw");

        var resolver = new NodeResolution();
        foreach (var (node, remote) in _remotes) resolver.SetStaticOverride(node, remote.EndPoint);
        var http = new HttpClient(new OkHandler());

        var runtime = new NodeRuntime(
            config,
            audioFactory: () => { var d = new FakeAudioDevice(); lock (_devices) _devices.Add(d); return d; },
            resolver: resolver,
            registrationFactory: cfg => new AslRegistrationService(cfg, http),
            networkMonitor: _net,
            reconnectPolicy: FastPolicy,
            // The fake remotes never PONG: long keep-alives so a link only dies when a test says so.
            configureSession: s => s.KeepAliveInterval = quickKeepAlive ? TimeSpan.FromMilliseconds(40) : TimeSpan.FromSeconds(30));
        await runtime.StartAsync();
        return runtime;
    }

    /// <summary>Dials <paramref name="node"/> and completes its handshake.</summary>
    private async Task<NodeLink> LinkAsync(NodeRuntime rt, string node)
    {
        await rt.ConnectAsync(node);
        await _remotes[node].AcceptCallAsync();
        await Wait.UntilAsync(() => rt.Connections.FirstOrDefault(c => c.Node == node)?.Link?.Session.State
                                    == Iax2SessionState.Connected);
        return rt.Connections.Single(c => c.Node == node).Link!;
    }

    private static NodeConnection Conn(NodeRuntime rt, string node) => rt.Connections.Single(c => c.Node == node);

    [Fact]
    public async Task SecondNode_IsAdded_NotSwapped_AndTheFirstStaysTheTransmitNode()
    {
        await using var rt = await CreateAsync();
        var a = await LinkAsync(rt, "2000");
        var b = await LinkAsync(rt, "3000");

        Assert.Equal(2, rt.Links.Count);
        Assert.Equal(["2000", "3000"], rt.Connections.Select(c => c.Node));
        Assert.Equal("2000", rt.TransmitNode);
        Assert.Same(a, rt.Link);                       // Link is the transmit link
        Assert.True(a.Ptt.TransmitEnabled);
        Assert.False(b.Ptt.TransmitEnabled);           // the second is listen-only
        Assert.True(Conn(rt, "2000").IsTransmitTarget);
        Assert.False(Conn(rt, "3000").IsTransmitTarget);
        lock (_devices) Assert.All(_devices, d => Assert.False(d.Disposed));
    }

    [Fact]
    public async Task OnlyTheTransmitLink_CanKey()
    {
        await using var rt = await CreateAsync();
        var a = await LinkAsync(rt, "2000");
        var b = await LinkAsync(rt, "3000");
        var warnings = new List<string>();
        b.Ptt.WarningLogged += warnings.Add;

        await b.Ptt.SetPttStateAsync(true, PttSource.UiButton);
        Assert.False(b.Ptt.IsPttActive);
        Assert.Contains(warnings, w => w.Contains("3000") && w.Contains("listen-only"));

        await a.Ptt.SetPttStateAsync(true, PttSource.UiButton);
        Assert.True(a.Ptt.IsPttActive);
        await a.Ptt.SetPttStateAsync(false, PttSource.UiButton);
    }

    [Fact]
    public async Task ChoosingAnotherTransmitNode_ReleasesTheKey_AndMovesTheRight()
    {
        await using var rt = await CreateAsync();
        var a = await LinkAsync(rt, "2000");
        var b = await LinkAsync(rt, "3000");
        await a.Ptt.SetPttStateAsync(true, PttSource.UiButton);

        Assert.True(rt.SetTransmitNode("3000"));

        await Wait.UntilAsync(() => !a.Ptt.IsPttActive);       // the old transmit link was unkeyed
        Assert.Equal("3000", rt.TransmitNode);
        Assert.Same(b, rt.Link);
        Assert.False(a.Ptt.TransmitEnabled);
        await b.Ptt.SetPttStateAsync(true, PttSource.UiButton);
        Assert.True(b.Ptt.IsPttActive);
        await b.Ptt.SetPttStateAsync(false, PttSource.UiButton);
    }

    [Fact]
    public async Task SetTransmitNode_RefusesANodeThatIsNotLinked()
    {
        await using var rt = await CreateAsync();
        await LinkAsync(rt, "2000");

        Assert.False(rt.SetTransmitNode("9999"));
        Assert.Equal("2000", rt.TransmitNode);
    }

    [Fact]
    public async Task HangingUpOneNode_LeavesTheOthers()
    {
        await using var rt = await CreateAsync();
        await LinkAsync(rt, "2000");
        var b = await LinkAsync(rt, "3000");
        var activity = new List<(bool, string?)>();
        rt.LinkActiveChanged += (a, n) => { lock (activity) activity.Add((a, n)); };

        await rt.DisconnectAsync("2000");               // the transmit node, hung up by the user

        Assert.Equal(["3000"], rt.Connections.Select(c => c.Node));
        Assert.Same(b, rt.Links.Single());
        Assert.Equal("3000", rt.TransmitNode);          // one left, and the user caused it: it becomes the transmit node
        Assert.True(b.Ptt.TransmitEnabled);
        lock (activity) Assert.Equal([(true, "3000")], activity);   // still wanted; never went false
    }

    [Fact]
    public async Task WhenTheFarEndDropsTheTransmitNode_NothingIsSilentlyRedirected()
    {
        // Auto-reconnect off: a hang-up right after ACCEPT would otherwise be retried as the "early hang-up" case.
        await using var rt = await CreateAsync(autoReconnect: false);
        await LinkAsync(rt, "2000");
        var b = await LinkAsync(rt, "3000");
        var notices = new List<string>();
        rt.Notice += n => { lock (notices) notices.Add(n); };

        await _remotes["2000"].SendIaxAsync(IaxControlSubclass.Hangup);   // far node hangs up on the transmit link

        await Wait.UntilAsync(() => rt.Connections.Count == 1);
        Assert.Null(rt.TransmitNode);                   // the user must choose again
        Assert.False(b.Ptt.TransmitEnabled);
        Assert.Null(rt.Link);
        lock (notices) Assert.Contains(notices, n => n.Contains("Choose a node to transmit to"));

        Assert.True(rt.SetTransmitNode("3000"));
        Assert.True(b.Ptt.TransmitEnabled);
    }

    [Fact]
    public async Task AtMostMaxLinks_AreAllowed()
    {
        await using var rt = await CreateAsync();
        for (int i = 0; i < NodeRuntime.MaxLinks; i++)
            await LinkAsync(rt, ((i + 2) * 1000).ToString());
        Assert.Equal(NodeRuntime.MaxLinks, rt.Connections.Count);

        string overLimit = ((NodeRuntime.MaxLinks + 2) * 1000).ToString();
        await Assert.ThrowsAsync<InvalidOperationException>(() => rt.ConnectAsync(overLimit));
        Assert.Equal(NodeRuntime.MaxLinks, rt.Connections.Count);
    }

    [Fact]
    public async Task ConnectingTheSameNodeAgain_RedialsOnlyThatNode_KeepingItsChoices()
    {
        await using var rt = await CreateAsync();
        var a1 = await LinkAsync(rt, "2000");
        var b = await LinkAsync(rt, "3000");
        rt.SetMuted("2000", true);
        ushort firstCall = _remotes["2000"].SessionCall;

        await rt.ConnectAsync("2000");
        await _remotes["2000"].WaitForFrameAsync(f => f.FrameType == FrameType.IaxControl
                                                      && f.Subclass == (byte)IaxControlSubclass.New
                                                      && f.SourceCallNumber != firstCall);

        Assert.Equal(2, rt.Connections.Count);
        Assert.NotSame(a1, Conn(rt, "2000").Link);
        Assert.Same(b, Conn(rt, "3000").Link);
        Assert.Equal("2000", rt.TransmitNode);
        Assert.True(Conn(rt, "2000").Muted);
        Assert.True(Conn(rt, "2000").Link!.Audio.Muted);   // the mute carried over to the new call
    }

    [Fact]
    public async Task Mute_AppliesToThatLinkOnly_AndSurvivesInTheConnection()
    {
        await using var rt = await CreateAsync();
        var a = await LinkAsync(rt, "2000");
        var b = await LinkAsync(rt, "3000");
        int changes = 0;
        rt.ConnectionsChanged += () => Interlocked.Increment(ref changes);

        rt.SetMuted("3000", true);

        Assert.False(a.Audio.Muted);
        Assert.True(b.Audio.Muted);
        Assert.True(Conn(rt, "3000").Muted);
        Assert.True(changes > 0);

        rt.SetMuted("3000", false);
        Assert.False(b.Audio.Muted);
    }

    [Fact]
    public async Task EachLinkGetsItsOwnAudioDevice_AndOnlyHangingUpReleasesIt()
    {
        await using var rt = await CreateAsync();
        await LinkAsync(rt, "2000");
        await LinkAsync(rt, "3000");
        lock (_devices) Assert.Equal(2, _devices.Count);

        await rt.DisconnectAsync("3000");

        lock (_devices)
        {
            Assert.False(_devices[0].Disposed);
            Assert.True(_devices[1].Disposed);
        }
    }

    [Fact]
    public async Task DisconnectAll_HangsUpEveryLink_AndClearsTheTransmitNode()
    {
        await using var rt = await CreateAsync();
        await LinkAsync(rt, "2000");
        await LinkAsync(rt, "3000");

        await rt.DisconnectAsync();

        Assert.Empty(rt.Connections);
        Assert.Empty(rt.Links);
        Assert.Null(rt.TransmitNode);
        Assert.Null(rt.Link);
        lock (_devices) Assert.All(_devices, d => Assert.True(d.Disposed));
    }

    [Fact]
    public async Task OneLinkLost_RecoversWhileTheOtherStaysUp()
    {
        await using var rt = await CreateAsync(quickKeepAlive: true);
        var a = await LinkAsync(rt, "2000");
        var b = await LinkAsync(rt, "3000");
        ushort firstCall = _remotes["2000"].SessionCall;

        // Keep node 3000 alive by answering its pings; node 2000 stays silent and is declared lost.
        using var cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var f = await _remotes["3000"].ReceiveFrameAsync(500);
                    if (f.FrameType == FrameType.IaxControl && f.Subclass == (byte)IaxControlSubclass.Ping)
                        await _remotes["3000"].SendIaxAsync(IaxControlSubclass.Pong, f.Timestamp);
                }
            }
            catch (Exception) { }
        });

        await _remotes["2000"].WaitForFrameAsync(f => f.FrameType == FrameType.IaxControl
                                                      && f.Subclass == (byte)IaxControlSubclass.New
                                                      && f.SourceCallNumber != firstCall, 5000);
        await _remotes["2000"].SendIaxAsync(IaxControlSubclass.Accept);

        await Wait.UntilAsync(() => Conn(rt, "2000").Link is { } l && !ReferenceEquals(l, a)
                                    && l.Session.State == Iax2SessionState.Connected);
        cts.Cancel();

        Assert.Same(b, Conn(rt, "3000").Link);
        Assert.Equal(2, rt.Connections.Count);
        Assert.Equal("2000", rt.TransmitNode);          // its transmit choice survived the outage
    }

    /// <summary>A runtime that, like production, holds the first dial for a while after registering.</summary>
    private async Task<NodeRuntime> CreateWithSettleDelayAsync(TimeSpan settle)
    {
        var config = new NodeConfigService(new JsonFileSettingsStore(_settingsPath), _secrets);
        await config.SaveAsync(new NodeSettings { NodeNumber = "55553", Callsign = "W1AW" }, "pw");
        var resolver = new NodeResolution();
        foreach (var (node, remote) in _remotes) resolver.SetStaticOverride(node, remote.EndPoint);

        var runtime = new NodeRuntime(
            config,
            audioFactory: () => { var d = new FakeAudioDevice(); lock (_devices) _devices.Add(d); return d; },
            resolver: resolver,
            registrationFactory: cfg => new AslRegistrationService(cfg, new HttpClient(new OkHandler())),
            networkMonitor: _net,
            reconnectPolicy: FastPolicy with { RegistrationSettleDelay = settle },
            configureSession: s => s.KeepAliveInterval = TimeSpan.FromSeconds(30));
        await runtime.StartAsync();
        await Wait.UntilAsync(() => runtime.IsRegistered);
        return runtime;
    }

    private sealed class DirectoryHandler(Func<int> port) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"node\":{\"ipaddr\":\"127.0.0.1\",\"port\":" + port() + ",\"callsign\":\"W1AW\"}}",
                    Encoding.UTF8, "application/json")
            });
        }
    }

    [Fact]
    public async Task ReconnectAfterTheNodeMovedAddress_LooksItUpAgain_InsteadOfRedialingTheCachedOne()
    {
        // Node 2000 is found through the directory (not a static override), so its address is cached for 15 minutes.
        var oldHome = _remotes["2000"];
        var newHome = _remotes["3000"];
        var current = oldHome;
        var directory = new DirectoryHandler(() => Volatile.Read(ref current).EndPoint.Port);
        var resolver = new NodeResolution(new HttpClient(directory)) { PortProbe = null, DnsPort = null };

        var config = new NodeConfigService(new JsonFileSettingsStore(_settingsPath), _secrets);
        await config.SaveAsync(new NodeSettings { NodeNumber = "55553", Callsign = "W1AW" }, "pw");
        await using var rt = new NodeRuntime(
            config,
            audioFactory: () => new FakeAudioDevice(),
            resolver: resolver,
            registrationFactory: cfg => new AslRegistrationService(cfg, new HttpClient(new OkHandler())),
            networkMonitor: _net,
            reconnectPolicy: FastPolicy,
            // The fakes never answer PINGs, so the link is declared lost about 160 ms after it connects.
            configureSession: s => s.KeepAliveInterval = TimeSpan.FromMilliseconds(40));
        await rt.StartAsync();

        await rt.ConnectAsync("2000");
        await oldHome.AcceptCallAsync();
        await Wait.UntilAsync(() => Conn(rt, "2000").Link?.Session.State == Iax2SessionState.Connected);
        Assert.Equal(1, Volatile.Read(ref directory.Requests));

        // The node's public address changes; the old one goes quiet, so the link is lost and re-dialled.
        Volatile.Write(ref current, newHome);

        // First retry: the cached (old) address, which never answers. That failure must drop the cached address,
        // so the next retry asks the directory again and reaches the node at its new home.
        await newHome.AcceptCallAsync();
        await Wait.UntilAsync(() => rt.Connections.FirstOrDefault(c => c.Node == "2000")?.Link is { } l
                                    && l.Session.State == Iax2SessionState.Connected
                                    && l.Target.EndPoint.Port == newHome.EndPoint.Port);
        Assert.True(Volatile.Read(ref directory.Requests) >= 2);
    }

    [Fact]
    public async Task HangUpDuringTheJustRegisteredWait_CancelsTheConnect_AndNothingIsDialled()
    {
        await using var rt = await CreateWithSettleDelayAsync(TimeSpan.FromSeconds(30));
        int linksCreated = 0;
        rt.LinkAdded += _ => Interlocked.Increment(ref linksCreated);

        var connect = rt.ConnectAsync("2000");                      // held by the "just registered" wait
        await Wait.UntilAsync(() => rt.Connections.Count == 1);     // the row is on screen, so it can be hung up
        await rt.DisconnectAsync("2000");

        // The connect ends promptly (not after the 30 s wait) and reports that it was cancelled.
        var finished = await Task.WhenAny(connect, Task.Delay(5000));
        Assert.Same(connect, finished);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);

        await Task.Delay(200);
        Assert.Empty(rt.Connections);
        Assert.Empty(rt.Links);
        Assert.Equal(0, Volatile.Read(ref linksCreated));           // no hidden call that the user cannot hang up
        Assert.Null(rt.TransmitNode);
        lock (_devices) Assert.Empty(_devices);                     // no audio device was opened for it
        await Assert.ThrowsAnyAsync<Exception>(() => _remotes["2000"].ReceiveFrameAsync(200));   // nothing reached the node
    }

    [Fact]
    public async Task DisconnectAllDuringTheWait_CancelsEveryPendingConnect_AndANewConnectStillWorks()
    {
        await using var rt = await CreateWithSettleDelayAsync(TimeSpan.FromSeconds(30));

        var first = rt.ConnectAsync("2000");
        var second = rt.ConnectAsync("3000");
        await Wait.UntilAsync(() => rt.Connections.Count == 2);
        await rt.DisconnectAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(rt.Connections);
        Assert.Empty(rt.Links);
    }

    [Fact]
    public async Task AfterACancelledConnect_TheSameNodeCanBeConnectedAgain()
    {
        await using var rt = await CreateWithSettleDelayAsync(TimeSpan.FromMilliseconds(600));

        var cancelled = rt.ConnectAsync("2000");
        await Wait.UntilAsync(() => rt.Connections.Count == 1);
        await rt.DisconnectAsync("2000");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(TimeSpan.FromSeconds(5)));

        var link = await LinkAsync(rt, "2000");                     // waits out the rest of the 600 ms, then dials
        Assert.Equal(Iax2SessionState.Connected, link.Session.State);
        Assert.Equal("2000", rt.TransmitNode);
        Assert.Single(rt.Links);
    }
}
