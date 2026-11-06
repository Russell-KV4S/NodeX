using System.Net;
using System.Text;
using NodeX.Core.Audio;
using NodeX.Core.Settings;
using static NodeX.Core.Iax2Protocol;

namespace NodeX.Core.Tests;

internal sealed class FakeAudioDevice : IAudioDeviceService
{
    public bool IsCapturing { get; private set; }
    public bool IsPlaying { get; private set; }
    public bool Disposed { get; private set; }
    public TimeSpan BufferedPlayback => TimeSpan.Zero;
    public event Action<string>? ErrorOccurred;
    public void StartCapture(Action<ReadOnlyMemory<short>> cb) => IsCapturing = true;
    public void StopCapture() => IsCapturing = false;
    public void StartPlayback() => IsPlaying = true;
    public void StopPlayback() => IsPlaying = false;
    public void EnqueuePlaybackPcm(ReadOnlyMemory<short> pcm) { }
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    public void RaiseError(string m) => ErrorOccurred?.Invoke(m);
}

public class NodeRuntimeTests : IDisposable
{
    private readonly string _settingsPath = Path.Combine(Path.GetTempPath(), "nodex-rt-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly InMemorySecretStore _secrets = new();
    private readonly List<FakeAudioDevice> _devices = [];
    private readonly FakeIaxRemote _remote = new();

    public void Dispose()
    {
        _remote.Dispose();
        foreach (string f in new[] { _settingsPath, _settingsPath + ".tmp" })
            try { File.Delete(f); } catch (IOException) { }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage RegistryOk() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"ipaddr":"203.0.113.9","port":4569,"refresh":60,"data":"successfully registered"}""",
            Encoding.UTF8, "application/json")
    };

    private (NodeRuntime runtime, NodeConfigService config, StubHandler registry) Create(NodeSettings? settings = null)
    {
        var config = new NodeConfigService(new JsonFileSettingsStore(_settingsPath), _secrets);
        var resolver = new NodeResolution();
        resolver.SetStaticOverride("2000", _remote.EndPoint);
        var registry = new StubHandler(_ => RegistryOk());

        var runtime = new NodeRuntime(
            config,
            audioFactory: () => { var d = new FakeAudioDevice(); lock (_devices) _devices.Add(d); return d; },
            resolver: resolver,
            registrationFactory: cfg => new AslRegistrationService(cfg, new HttpClient(registry)));
        return (runtime, config, registry);
    }

    private static NodeSettings Station => new() { NodeNumber = "55553", Callsign = "W1AW" };

    /// <summary>
    /// Waits until the audio device has really started. (Audio.IsRunning turns true at the top of
    /// Start(), before the device is opened, so it is not a reliable signal.)
    /// </summary>
    private async Task WaitForAudioAsync()
    {
        await WaitUntil(() =>
        {
            lock (_devices) return _devices.Count > 0 && _devices[^1].IsCapturing && _devices[^1].IsPlaying;
        });
    }

    private static async Task WaitUntil(Func<bool> cond, int ms = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > ms) throw new TimeoutException("Condition not met");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task StartWithoutConfig_DoesNotRegister_AndReportsNotConfigured()
    {
        var (rt, _, registry) = Create();
        await using var _ = rt;

        await rt.StartAsync();

        Assert.False(rt.IsRegistered);
        Assert.Equal("Not configured", rt.RegistrationStatus);
        Assert.Equal(0, registry.Requests);
    }

    [Fact]
    public async Task StartWithSavedConfig_RegistersAndReportsPublicAddress()
    {
        var (rt, config, registry) = Create();
        await using var _ = rt;
        await config.SaveAsync(Station, "pw");

        await rt.StartAsync();
        await WaitUntil(() => rt.IsRegistered);

        Assert.Equal(IPAddress.Parse("203.0.113.9"), rt.PerceivedAddress);
        Assert.True(registry.Requests >= 1);
    }

    [Fact]
    public async Task SavingSettingsWhileRunning_StartsRegistration()
    {
        var (rt, config, _) = Create();
        await using var _ = rt;
        await rt.StartAsync();
        Assert.False(rt.IsRegistered);

        await config.SaveAsync(Station, "pw");   // the settings screen does exactly this

        await WaitUntil(() => rt.IsRegistered);
        Assert.True(rt.Config.IsComplete);
    }

    [Fact]
    public async Task RegisterOnStartupOff_DoesNotRegister()
    {
        var (rt, config, registry) = Create();
        await using var _ = rt;
        await config.SaveAsync(Station with { RegisterOnStartup = false }, "pw");

        await rt.StartAsync();
        await Task.Delay(100);

        Assert.Equal(0, registry.Requests);
        Assert.Equal("Registration off", rt.RegistrationStatus);
    }

    [Fact]
    public async Task Connect_WithoutConfig_Throws()
    {
        var (rt, _, _) = Create();
        await using var _ = rt;
        await rt.StartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => rt.ConnectAsync("2000"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("12")]
    public async Task Connect_WithBadTarget_Throws(string target)
    {
        var (rt, config, _) = Create();
        await using var _ = rt;
        await config.SaveAsync(Station, "pw");
        await rt.StartAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => rt.ConnectAsync(target));
    }

    [Fact]
    public async Task Connect_UnknownNode_ReturnsNull_AndCreatesNoLink()
    {
        var (_, config, _) = Create();
        await config.SaveAsync(Station, "pw");

        // A resolver whose directory and DNS lookups both fail, so no network access is involved.
        var resolver = new NodeResolution(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))),
            dnsResolver: (_, _) => Task.FromException<IPAddress[]>(new System.Net.Sockets.SocketException(11001)));
        await using var rt2 = new NodeRuntime(config, () => new FakeAudioDevice(), resolver,
            registrationFactory: c => new AslRegistrationService(c, new HttpClient(new StubHandler(_ => RegistryOk()))));
        await rt2.StartAsync();

        Assert.Null(await rt2.ConnectAsync("99999"));
        Assert.Null(rt2.Link);
    }

    [Fact]
    public async Task Connect_LogsInAsRadio_WithNodeNumberAndNoPassword_ThenStartsAudio()
    {
        var (rt, config, _) = Create();
        await using var _ = rt;
        await config.SaveAsync(Station, "TopSecretPw");
        await rt.StartAsync();
        var activity = new List<(bool Active, string? Node)>();
        rt.LinkActiveChanged += (a, n) => { lock (activity) activity.Add((a, n)); };

        var info = await rt.ConnectAsync("2000");
        Assert.NotNull(info);

        var neu = await _remote.ReceiveFrameAsync();
        Assert.Equal((byte)IaxControlSubclass.New, neu.Subclass);
        var ies = ParseInformationElements(neu.Payload);
        Assert.Equal("radio", ies.Single(i => i.Id == InformationElementId.Username).AsString());
        Assert.Equal("55553", ies.Single(i => i.Id == InformationElementId.CallingNumber).AsString());
        Assert.Equal("2000", ies.Single(i => i.Id == InformationElementId.CalledNumber).AsString());
        Assert.DoesNotContain("TopSecretPw", Encoding.UTF8.GetString(neu.Payload));  // never sent to the remote node

        await _remote.SendIaxAsync(IaxControlSubclass.Accept);
        await WaitUntil(() => rt.Link!.Session.State == Iax2SessionState.Connected);
        await WaitForAudioAsync();

        var device = _devices.Single();
        Assert.True(device.IsCapturing && device.IsPlaying);
        lock (activity) Assert.Equal([(true, "2000")], activity);
    }

    [Fact]
    public async Task Settings_AreAppliedToTheLink()
    {
        var (rt, config, _) = Create();
        await using var _ = rt;
        await config.SaveAsync(Station with { TogglePtt = true, TimeOutTimerSeconds = 42 }, "pw");
        await rt.StartAsync();

        await rt.ConnectAsync("2000");

        Assert.Equal(PttMode.Toggle, rt.Link!.Ptt.Mode);
        Assert.Equal(TimeSpan.FromSeconds(42), rt.Link.Ptt.TimeOutTimerLimit);
    }

    [Fact]
    public async Task Disconnect_StopsAudio_ReleasesDevice_AndNotifies()
    {
        var (rt, config, _) = Create();
        await using var _ = rt;
        await config.SaveAsync(Station, "pw");
        await rt.StartAsync();
        var changes = new List<bool>();      // true = link created, false = link ended
        rt.LinkAdded += _ => { lock (changes) changes.Add(true); };
        rt.LinkRemoved += _ => { lock (changes) changes.Add(false); };

        await rt.ConnectAsync("2000");
        await _remote.AcceptCallAsync();
        await WaitForAudioAsync();

        await rt.DisconnectAsync();

        Assert.Null(rt.Link);
        var device = _devices.Single();
        Assert.False(device.IsCapturing || device.IsPlaying);
        Assert.True(device.Disposed);
        lock (changes) Assert.Equal([true, false], changes);
    }

    [Fact]
    public async Task RemoteHangup_TearsDownTheLink_Automatically()
    {
        var (rt, config, _) = Create();
        await using var _ = rt;
        await config.SaveAsync(Station, "pw");
        await rt.StartAsync();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rt.LinkRemoved += _ => ended.TrySetResult();

        await rt.ConnectAsync("2000");
        await _remote.AcceptCallAsync();
        await WaitUntil(() => rt.Link?.Session.State == Iax2SessionState.Connected);

        await _remote.SendIaxAsync(IaxControlSubclass.Hangup);

        await ended.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Null(rt.Link);
        Assert.True(_devices.Single().Disposed);
    }

    [Fact]
    public async Task SecondConnect_ReplacesTheFirstLink()
    {
        var (rt, config, _) = Create();
        await using var _ = rt;
        await config.SaveAsync(Station, "pw");
        await rt.StartAsync();

        await rt.ConnectAsync("2000");
        var first = rt.Link;
        await rt.ConnectAsync("2000");

        Assert.NotSame(first, rt.Link);
        Assert.Equal(2, _devices.Count);
        Assert.True(_devices[0].Disposed);
    }

    [Fact]
    public async Task AudioFailure_IsSurfacedAsAnError()
    {
        var (rt, config, _) = Create();
        await using var _ = rt;
        await config.SaveAsync(Station, "pw");
        await rt.StartAsync();
        var errors = new List<string>();
        rt.ErrorOccurred += e => { lock (errors) errors.Add(e); };

        await rt.ConnectAsync("2000");
        await _remote.AcceptCallAsync();
        await WaitForAudioAsync();
        _devices.Single().RaiseError("mic unplugged");

        lock (errors) Assert.Contains(errors, e => e.Contains("mic unplugged"));
    }
}
