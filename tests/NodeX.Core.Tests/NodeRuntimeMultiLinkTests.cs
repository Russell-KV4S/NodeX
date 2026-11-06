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
    private readonly Dictionary<string, FakeIaxRemote> _remotes = new()
    {
        ["2000"] = new(), ["3000"] = new(), ["4000"] = new(), ["5000"] = new(), ["6000"] = new()
    };
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
        AttemptTimeout = TimeSpan.FromMilliseconds(400)
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
        await LinkAsync(rt, "2000");
        await LinkAsync(rt, "3000");
        await LinkAsync(rt, "4000");
        await LinkAsync(rt, "5000");
        Assert.Equal(NodeRuntime.MaxLinks, rt.Connections.Count);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rt.ConnectAsync("6000"));
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
}
