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
        public readonly FakeIaxRemote A = new(), B = new(), C = new();
        public readonly NodeRuntime Runtime;
        public readonly MainViewModel Vm;
        private readonly HttpClient _http = new(new Registry());
        public Fixture(NodeSettings? settings = null)
        {
            var store = new Store(settings ?? new() { NodeNumber = "55553", RegisterOnStartup = false, AutoReconnect = false });
            var secrets = new InMemorySecretStore();
            secrets.SetAsync(SecretKeys.NodePassword, "test-password").GetAwaiter().GetResult();
            var resolver = new NodeResolution();
            resolver.SetStaticOverride("2000", A.EndPoint);
            resolver.SetStaticOverride("3000", B.EndPoint);
            resolver.SetStaticOverride("4000", C.EndPoint);
            Runtime = new(new NodeConfigService(store, secrets), () => new NullAudioDeviceService(),
                resolver: resolver, registrationFactory: cfg => new(cfg, _http), networkMonitor: new Network(),
                configureSession: session => { session.KeepAliveInterval = TimeSpan.FromMinutes(1); session.HangupAckWait = TimeSpan.Zero; });
            Vm = new(Runtime);
        }
        public async Task Connect(string node)
        {
            await Runtime.ConnectAsync(node);
            var remote = node switch
            {
                "2000" => A,
                "3000" => B,
                "4000" => C,
                _ => throw new ArgumentOutOfRangeException(nameof(node))
            };
            await remote.AcceptCallAsync();
            await Until(() => Runtime.Connections.Single(c => c.Node == node).Link?.Session.State == Iax2SessionState.Connected);
            Dispatcher.UIThread.RunJobs();
        }
        public async ValueTask DisposeAsync()
        {
            Vm.Dispose();
            await Runtime.DisposeAsync();
            A.Dispose(); B.Dispose(); C.Dispose(); _http.Dispose();
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
        string node = f.Vm.LocalNodeNumber;
        await f.Runtime.ConfigService.SaveAsync(f.Runtime.Config.Settings with { NodeNumber = "55554" }, "pw");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(node, f.Vm.LocalNodeNumber); // disposed view models no longer follow runtime events
    }

    [AvaloniaFact]
    public async Task MultiLink_DisconnectAllNeedsConfirmation_AndCancelKeepsLinks()
    {
        await using var f = new Fixture();
        await f.Vm.InitializeAsync();
        await f.Connect("2000"); await f.Connect("3000");
        Assert.Equal(2, f.Vm.LinkItems.Count);
        Assert.Equal("2 / 8 linked", f.Vm.LinkCountText);
        Assert.True(f.Vm.LinkItems[0].HasBubbleChart);
        Assert.Equal("https://stats.allstarlink.org/stats/2000/networkMap", f.Vm.LinkItems[0].BubbleChartUri!.ToString());
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
    public async Task PermissionChanges_UpdateUI_AndNeverAllowKeying()
    {
        await using var f = new Fixture(new() { NodeNumber = "55553", RegisterOnStartup = false });
        await f.Vm.InitializeAsync(); await f.Connect("2000");
        f.Runtime.Transmit.MicrophoneAllowed = false;
        Dispatcher.UIThread.RunJobs();
        Assert.True(f.Vm.IsTransmitBlocked);
        Assert.Equal("LISTEN ONLY", f.Vm.PttHeadline);
        await f.Vm.PttDownAsync();
        Assert.False(f.Runtime.Link!.Ptt.IsPttActive);
        Assert.False(f.Vm.CanKey);
        await f.Vm.HandleSpacebarPttAsync(true);
        Assert.False(f.Runtime.Link.Ptt.IsPttActive);
    }

    [AvaloniaFact]
    public async Task LiveRxGainAndAutoLevel_UpdateActiveLinkAndPersist()
    {
        await using var f = new Fixture();
        await f.Vm.InitializeAsync();
        await f.Connect("2000");

        f.Vm.RxGain = 2.5;
        Assert.Equal("2.5×", f.Vm.RxGainDisplay);
        Assert.Equal(2.5f, f.Runtime.LiveRxGain);
        Assert.Equal(2.5f, f.Runtime.Link!.Audio.RxGain);

        f.Vm.ToggleRxAutoLevelCommand.Execute(null);
        Assert.True(f.Vm.RxAutoLevel);
        Assert.True(f.Runtime.LiveRxAutoLevel);
        Assert.True(f.Runtime.Link.Audio.RxAutoLevel);

        f.Vm.MicGain = 1.8;
        Assert.Equal("1.8×", f.Vm.MicGainDisplay);
        Assert.Equal(1.8f, f.Runtime.LiveMicGain);
        Assert.Equal(1.8f, f.Runtime.Link.Audio.MicGain);

        await Until(() =>
            Math.Abs(f.Runtime.Config.Settings.RxGain - 2.5f) < 0.01f
            && f.Runtime.Config.Settings.RxAutoLevel
            && Math.Abs(f.Runtime.Config.Settings.MicGain - 1.8f) < 0.01f);
    }

    [AvaloniaFact]
    public async Task DynamicLinkOrder_MovesActiveRxAndLocalTxToTop_SkipsMuted_AndTogglesFixed()
    {
        await using var f = new Fixture();
        await f.Vm.InitializeAsync();
        await f.Connect("2000");
        await f.Connect("3000");
        await f.Connect("4000");

        Assert.True(f.Vm.SortLinksByActivity);
        Assert.Equal("Sort: Recent", f.Vm.LinkSortButtonText);
        Assert.Equal(["2000", "3000", "4000"], f.Vm.LinkItems.Select(l => l.Node).ToArray());

        // Remote KEY from C moves 4000 to the top; UNKEY leaves it there until another node talks.
        await f.C.SendControlAsync(Iax2Protocol.ControlSubclass.AstControlKey);
        await Until(() => f.Vm.LinkItems[0].Node == "4000" && f.Vm.LinkItems[0].IsReceiving);
        Assert.Equal(["4000", "2000", "3000"], f.Vm.LinkItems.Select(l => l.Node).ToArray());

        await f.C.SendControlAsync(Iax2Protocol.ControlSubclass.AstControlUnkey);
        await Until(() => !f.Vm.LinkItems[0].IsReceiving);
        Assert.Equal(["4000", "2000", "3000"], f.Vm.LinkItems.Select(l => l.Node).ToArray());

        // Muted node still updates its Receiving pill, but does not jump ahead of unmuted cards.
        f.Runtime.SetMuted("3000", true);
        Dispatcher.UIThread.RunJobs();
        await f.B.SendControlAsync(Iax2Protocol.ControlSubclass.AstControlKey);
        await Until(() => f.Vm.LinkItems.Single(l => l.Node == "3000").IsReceiving);
        Assert.Equal(["4000", "2000", "3000"], f.Vm.LinkItems.Select(l => l.Node).ToArray());
        await f.B.SendControlAsync(Iax2Protocol.ControlSubclass.AstControlUnkey);
        await Until(() => !f.Vm.LinkItems.Single(l => l.Node == "3000").IsReceiving);

        // Local TX on the transmit node (2000) moves 2000 to the top without altering its remote LastHeardText.
        await f.Vm.HandleSpacebarPttAsync(true);
        await Until(() => f.Vm.LinkItems[0].Node == "2000");
        Assert.Equal(["2000", "4000", "3000"], f.Vm.LinkItems.Select(l => l.Node).ToArray());
        Assert.Equal("Not heard yet", f.Vm.LinkItems[0].LastHeardText);
        await f.Vm.HandleSpacebarPttAsync(false);

        // Toggle to fixed connect order and persist, then toggle back to recent order.
        f.Vm.ToggleSortLinksByActivityCommand.Execute(null);
        Assert.False(f.Vm.SortLinksByActivity);
        Assert.Equal("Sort: Fixed", f.Vm.LinkSortButtonText);
        Assert.Equal(["2000", "3000", "4000"], f.Vm.LinkItems.Select(l => l.Node).ToArray());
        await Until(() => !f.Runtime.Config.Settings.SortLinksByActivity);

        f.Vm.ToggleSortLinksByActivityCommand.Execute(null);
        Assert.True(f.Vm.SortLinksByActivity);
        Assert.Equal("Sort: Recent", f.Vm.LinkSortButtonText);
        Assert.Equal(["2000", "4000", "3000"], f.Vm.LinkItems.Select(l => l.Node).ToArray());
        await Until(() => f.Runtime.Config.Settings.SortLinksByActivity);

        // Inline favorite rename pauses automatic moves so the input box never loses focus, then catches up on close.
        f.Vm.LinkItems[0].ToggleEditLabelCommand.Execute(null);
        Assert.True(f.Vm.LinkItems[0].IsEditingLabel);
        await f.C.SendControlAsync(Iax2Protocol.ControlSubclass.AstControlKey);
        await Until(() => f.Vm.LinkItems.Single(l => l.Node == "4000").IsReceiving);
        Assert.Equal(["2000", "4000", "3000"], f.Vm.LinkItems.Select(l => l.Node).ToArray());

        f.Vm.LinkItems[0].ToggleEditLabelCommand.Execute(null);
        Assert.Equal(["4000", "2000", "3000"], f.Vm.LinkItems.Select(l => l.Node).ToArray());
    }

    [AvaloniaFact]
    public async Task FixedSortSetting_KeepsConnectOrderOnConnectAndRemoteKey()
    {
        await using var f = new Fixture(new()
        {
            NodeNumber = "55553",
            SortLinksByActivity = false,
            RegisterOnStartup = false,
            AutoReconnect = false
        });
        await f.Vm.InitializeAsync();
        await f.Connect("2000");
        await f.Connect("3000");

        Assert.False(f.Vm.SortLinksByActivity);
        Assert.Equal("Sort: Fixed", f.Vm.LinkSortButtonText);

        await f.B.SendControlAsync(Iax2Protocol.ControlSubclass.AstControlKey);
        await Until(() => f.Vm.LinkItems.Single(l => l.Node == "3000").IsReceiving);
        Assert.Equal(["2000", "3000"], f.Vm.LinkItems.Select(l => l.Node).ToArray());
    }
}
