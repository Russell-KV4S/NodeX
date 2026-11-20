using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using NodeX.Core.Audio;
using NodeX.Core.Settings;
using NodeX.UI;
using NodeX.UI.ViewModels;
using NodeX.UI.Views;

namespace NodeX.Core.Tests;

public class PrivateNodesUiTests
{
    private sealed class Store(NodeSettings settings) : ISettingsStore
    {
        public NodeSettings Current => settings;
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
        public readonly FakeIaxRemote PrivateRemote = new();
        public readonly Store Store;
        public readonly NodeRuntime Runtime;
        public readonly MainViewModel Vm;
        private readonly HttpClient _http = new(new Registry());

        public Fixture(NodeSettings? settings = null)
        {
            Store = new Store(settings ?? new()
            {
                NodeNumber = "523802",
                RegisterOnStartup = false,
                AutoReconnect = false,
                PrivateNodes = [new PrivateNodeEntry("1999", "club.local", 4570, "Club Hub")]
            });
            var secrets = new InMemorySecretStore();
            secrets.SetAsync(SecretKeys.NodePassword, "test-password").GetAwaiter().GetResult();
            var resolver = new NodeResolution(dnsResolver: (_, _) => Task.FromResult(new[] { IPAddress.Loopback }));
            Runtime = new(
                new NodeConfigService(Store, secrets),
                () => new NullAudioDeviceService(),
                resolver: resolver,
                registrationFactory: cfg => new(cfg, _http),
                networkMonitor: new Network(),
                configureSession: session => { session.KeepAliveInterval = TimeSpan.FromMinutes(1); session.HangupAckWait = TimeSpan.Zero; });
            Vm = new(Runtime);
        }

        public async ValueTask DisposeAsync()
        {
            Vm.Dispose();
            await Runtime.DisposeAsync();
            PrivateRemote.Dispose();
            _http.Dispose();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) { await Task.Delay(10, timeout.Token); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaFact]
    public async Task ConnectBox_ParsesAndDialsPrivateAddress_ShowsPrivateRow_AndRefusesSameNumberConflict()
    {
        await using var f = new Fixture();
        await f.Vm.InitializeAsync();
        Dispatcher.UIThread.RunJobs();

        // Typing a private address shows the live preview without connecting yet.
        f.Vm.TargetNodeInput = $"1999@127.0.0.1:{f.PrivateRemote.EndPoint.Port}";
        Assert.True(f.Vm.HasTargetPrivatePreview);
        Assert.Contains("1999 @ 127.0.0.1", f.Vm.TargetPrivatePreviewTitle);
        Assert.True(f.Vm.CanQuickSavePrivateTarget);

        // Dialing connects directly and displays `1999 @ host` marked PRIVATE.
        var acceptTask = f.PrivateRemote.AcceptCallAsync();
        await f.Vm.ConnectNodeAsync();
        await acceptTask;
        await Until(() => f.Vm.LinkItems.Count == 1 && f.Vm.LinkItems[0].IsConnected);

        Assert.True(f.Vm.LinkItems[0].IsPrivate);
        Assert.False(f.Vm.LinkItems[0].HasBubbleChart);   // private nodes are not on the stats site
        Assert.Contains("1999 @ 127.0.0.1", f.Vm.LinkItems[0].Title);
        Assert.Contains("1999 @ 127.0.0.1", f.Vm.ConnectedNodeDisplay);

        // Trying to link public 1999 simultaneously is refused with a clear inline error.
        f.Vm.TargetNodeInput = "1999";
        await f.Vm.ConnectNodeAsync();
        Assert.True(f.Vm.HasConnectError);
        Assert.Contains("Node 1999 is already linked via", f.Vm.ConnectErrorText);
    }

    [AvaloniaFact]
    public async Task DeepLink_OpensPreFilledConfirmation_NeverConnectsOrSavesOnItsOwn()
    {
        await using var f = new Fixture();
        await f.Vm.InitializeAsync();
        Dispatcher.UIThread.RunJobs();

        int savedBefore = f.Store.Current.PrivateNodes.Count;
        bool opened = f.Vm.OpenDeepLink("nodex://add?node=1950&host=ares.ddns.net&port=4572&name=ARES+Hub");

        Assert.True(opened);
        Assert.True(f.Vm.IsPrivateNodesOpen);
        Assert.True(f.Vm.PrivateNodes.IsFormMode);
        Assert.True(f.Vm.PrivateNodes.IsFromDeepLink);
        Assert.False(f.Vm.PrivateNodes.CanSaveAndConnect);
        Assert.Equal("1950", f.Vm.PrivateNodes.NodeInput);
        Assert.Equal("ares.ddns.net", f.Vm.PrivateNodes.HostInput);
        Assert.Equal("4572", f.Vm.PrivateNodes.PortInput);
        Assert.Equal("ARES Hub", f.Vm.PrivateNodes.NameInput);

        // Must NOT auto-connect and must NOT auto-save before user confirms.
        Assert.Empty(f.Runtime.Connections);
        Assert.Equal(savedBefore, f.Store.Current.PrivateNodes.Count);
    }

    [AvaloniaTheory]
    [InlineData("Dark", 1.0, 420, 860, 0)]
    [InlineData("Light", 1.5, 420, 860, 1)]
    [InlineData("HighContrast", 1.5, 1050, 760, 1)]
    [InlineData("Dark", 1.0, 420, 860, 2)]
    public async Task PrivateNodesOverlay_RendersAcrossThemesScalesAndModes(
        string theme, double scale, int width, int height, int mode)
    {
        Appearance.Apply(Application.Current!, new() { Theme = theme, TextScale = scale });
        await using var f = new Fixture();
        await f.Vm.InitializeAsync();

        if (mode == 0) f.Vm.OpenPrivateNodes();
        else if (mode == 1) f.Vm.OpenDeepLink("nodex://add?node=1950&host=ares.ddns.net&port=4572&name=ARES+Hub");
        else
        {
            f.Vm.OpenPrivateNodes();
            f.Vm.PrivateNodes.OpenShare(f.Store.Current.PrivateNodes[0]);
        }

        var view = new MainView { DataContext = f.Vm };
        var window = new Window { Content = view, Width = width, Height = height };
        window.Show();
        try
        {
            await Task.Delay(80);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
            using var bitmap = window.CaptureRenderedFrame();
            Assert.NotNull(bitmap);
            Assert.Equal(width, bitmap!.PixelSize.Width);
            Assert.Equal(height, bitmap.PixelSize.Height);
        }
        finally
        {
            window.Close();
        }
    }
}
