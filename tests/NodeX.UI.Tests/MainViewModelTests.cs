using System.Net;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using NodeX.Core.Audio;
using NodeX.Core.Settings;
using NodeX.UI.ViewModels;

namespace NodeX.Core.Tests;

public class MainViewModelTests
{
    private sealed class Store(NodeSettings settings) : ISettingsStore
    {
        public Task<NodeSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);
        public Task SaveAsync(NodeSettings value, CancellationToken cancellationToken = default) { settings = value; return Task.CompletedTask; }
    }
    private sealed class Registry : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"data":"successfully registered","refresh":60}""") });
    }
    private sealed class Network : INetworkMonitor
    {
        public event Action? Changed { add { } remove { } }
        public bool IsAvailable => true;
        public string GetSignature() => "test";
        public void Dispose() { }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public readonly FakeIaxRemote A = new(), B = new();
        public readonly NodeRuntime Runtime;
        public readonly MainViewModel Vm;
        private readonly HttpClient _http = new(new Registry());
        public Fixture(NodeSettings? settings = null)
        {
            var store = new Store(settings ?? new() { NodeNumber = "55553", Callsign = "W1AW", RegisterOnStartup = false, AutoReconnect = false });
            var secrets = new InMemorySecretStore();
            secrets.SetAsync(SecretKeys.NodePassword, "test-password").GetAwaiter().GetResult();
            var resolver = new NodeResolution();
            resolver.SetStaticOverride("2000", A.EndPoint);
            resolver.SetStaticOverride("3000", B.EndPoint);
            Runtime = new(new NodeConfigService(store, secrets), () => new NullAudioDeviceService(),
                resolver: resolver, registrationFactory: cfg => new(cfg, _http), networkMonitor: new Network(),
                reconnectPolicy: new() { RegistrationSettleDelay = TimeSpan.Zero },
                configureSession: session => { session.KeepAliveInterval = TimeSpan.FromMinutes(1); session.HangupAckWait = TimeSpan.Zero; });
            Vm = new(Runtime);
        }
        public async Task Connect(string node)
        {
            await Runtime.ConnectAsync(node);
            await (node == "2000" ? A : B).AcceptCallAsync();
            await Until(() => Runtime.Connections.Single(c => c.Node == node).Link?.Session.State == Iax2SessionState.Connected);
            Dispatcher.UIThread.RunJobs();
        }
        public async ValueTask DisposeAsync()
        {
            Vm.Dispose();
            await Runtime.DisposeAsync();
            A.Dispose(); B.Dispose(); _http.Dispose();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) { await Task.Delay(10, timeout.Token); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaFact]
    public async Task FirstRun_OpensSettings_AndDoesNotConnect()
    {
        await using var f = new Fixture(new NodeSettings());
        await f.Vm.InitializeAsync();
        Assert.True(f.Vm.IsSettingsOpen);
        Assert.Empty(f.Runtime.Connections);
        Assert.Contains(f.Vm.ActivityLog, line => line.Contains("Welcome to NodeX"));
    }

    [AvaloniaFact]
    public async Task ForegroundLifecycle_StopsBothTimers_AndRestartsOnResume()
    {
        await using var f = new Fixture();
        await f.Vm.InitializeAsync();
        Assert.True(f.Vm.MeterTimerRunning);
        Assert.True(f.Vm.AgeTimerRunning);
        f.Vm.SetForeground(false);
        Assert.False(f.Vm.MeterTimerRunning);
        Assert.False(f.Vm.AgeTimerRunning);
        f.Vm.SetForeground(true);
        Assert.True(f.Vm.MeterTimerRunning);
        Assert.True(f.Vm.AgeTimerRunning);
        f.Vm.Dispose();
        Assert.False(f.Vm.MeterTimerRunning);
        Assert.False(f.Vm.AgeTimerRunning);
        string callsign = f.Vm.LocalCallsign;
        await f.Runtime.ConfigService.SaveAsync(f.Runtime.Config.Settings with { Callsign = "K2ABC" }, "pw");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(callsign, f.Vm.LocalCallsign); // disposed view models no longer follow runtime events
    }

    [AvaloniaFact]
    public async Task MultiLink_DisconnectAllNeedsConfirmation_AndCancelKeepsLinks()
    {
        await using var f = new Fixture();
        await f.Vm.InitializeAsync();
        await f.Connect("2000"); await f.Connect("3000");
        Assert.Equal(2, f.Vm.LinkItems.Count);
        Assert.Equal("2 / 8 linked", f.Vm.LinkCountText);
        await f.Vm.RequestDisconnectNodeAsync();
        Assert.True(f.Vm.IsConfirmingDisconnectAll);
        Assert.Equal(2, f.Runtime.Connections.Count);
        f.Vm.CancelDisconnectAll();
        Assert.False(f.Vm.IsConfirmingDisconnectAll);
        await f.Vm.RequestDisconnectNodeAsync();
        await f.Vm.RequestDisconnectNodeAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(f.Runtime.Connections);
        Assert.Empty(f.Vm.LinkItems);
    }

    [AvaloniaFact]
    public async Task LosingTransmitNode_LeavesOtherLinks_AndRequiresExplicitSelection()
    {
        await using var f = new Fixture();
        await f.Vm.InitializeAsync();
        await f.Connect("2000"); await f.Connect("3000");
        await f.A.SendIaxAsync(Iax2Protocol.IaxControlSubclass.Hangup);
        await Until(() => f.Runtime.Connections.Count == 1);
        Dispatcher.UIThread.RunJobs();
        Assert.True(f.Vm.NeedsTransmitNode);
        Assert.False(f.Vm.CanKey);
        Assert.Equal("CHOOSE TX NODE", f.Vm.PttHeadline);
        await f.Vm.HandleSpacebarPttAsync(true);
        Assert.False(f.Runtime.Links.Single().Ptt.IsPttActive);
        f.Runtime.SetTransmitNode("3000");
        Dispatcher.UIThread.RunJobs();
        Assert.True(f.Vm.CanKey);
        Assert.False(f.Vm.NeedsTransmitNode);
    }

    [AvaloniaFact]
    public async Task Spacebar_ReleaseStillUnkeys_WhenSettingsOpenMidTransmission()
    {
        await using var f = new Fixture();
        await f.Vm.InitializeAsync(); await f.Connect("2000");
        await f.Vm.HandleSpacebarPttAsync(true);
        Assert.True(f.Runtime.Link!.Ptt.IsPttActive);
        f.Vm.IsSettingsOpen = true;
        await f.Vm.HandleSpacebarPttAsync(false);
        Assert.False(f.Runtime.Link.Ptt.IsPttActive);
        await f.Vm.HandleSpacebarPttAsync(true);
        Assert.False(f.Runtime.Link.Ptt.IsPttActive);
    }

    [AvaloniaFact]
    public async Task ListenOnlyAndPermissionChanges_UpdateUI_AndNeverAllowKeying()
    {
        await using var f = new Fixture(new() { NodeNumber = "55553", ListenOnly = true, RegisterOnStartup = false });
        await f.Vm.InitializeAsync(); await f.Connect("2000");
        Assert.True(f.Vm.IsTransmitBlocked);
        Assert.Equal("LISTEN ONLY", f.Vm.PttHeadline);
        await f.Vm.PttDownAsync();
        Assert.False(f.Runtime.Link!.Ptt.IsPttActive);
        await f.Runtime.ConfigService.SaveAsync(f.Runtime.Config.Settings with { ListenOnly = false, Callsign = "W1AW" }, "pw");
        Dispatcher.UIThread.RunJobs();
        f.Runtime.Transmit.MicrophoneAllowed = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(f.Vm.CanKey);
        await f.Vm.HandleSpacebarPttAsync(true);
        Assert.False(f.Runtime.Link.Ptt.IsPttActive);
    }
}
