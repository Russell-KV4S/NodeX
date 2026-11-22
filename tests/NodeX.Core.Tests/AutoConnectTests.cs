using System.Net;
using NodeX.Core.Audio;
using NodeX.Core.Settings;

namespace NodeX.Core.Tests;

public sealed class AutoConnectTests
{
    private sealed class Store(NodeSettings settings) : ISettingsStore
    {
        public Task<NodeSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);
        public Task SaveAsync(NodeSettings value, CancellationToken cancellationToken = default) { settings = value; return Task.CompletedTask; }
    }

    private sealed class StubRegistry : HttpMessageHandler
    {
        public volatile bool AllowRegistration;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (AllowRegistration)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""{"data":"successfully registered","refresh":60}""") });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    private sealed class Network : INetworkMonitor
    {
        public event Action? Changed { add { } remove { } }
        public bool IsAvailable => true;
        public string GetSignature() => "test";
        public void Dispose() { }
    }

    [Fact]
    public void Validator_AllowsUpToEightPublicAndPrivateAutoConnectNodes()
    {
        var valid8 = new NodeSettings
        {
            NodeNumber = "523802",
            AutoConnectNodes =
            [
                "523800", "48168", "523801", "53431",
                "49234", "60108", "1999@10.0.0.5:4569", "1998@hub.example.org:4570"
            ]
        };
        Assert.Empty(NodeSettingsValidator.Validate(valid8, "secret"));

        var normalized = NodeSettingsValidator.Normalize(valid8);
        Assert.Equal("1999@10.0.0.5", normalized.AutoConnectNodes[6]); // default port omitted canonically
        Assert.Equal("1998@hub.example.org:4570", normalized.AutoConnectNodes[7]);

        var tooMany = valid8 with { AutoConnectNodes = [.. valid8.AutoConnectNodes, "27339"] };
        Assert.Contains(NodeSettingsValidator.Validate(tooMany, "secret"),
            i => i.Field == NodeSettingsValidator.Fields.AutoConnectNodes);

        var badEntry = valid8 with { AutoConnectNodes = ["not-a-node"] };
        Assert.Contains(NodeSettingsValidator.Validate(badEntry, "secret"),
            i => i.Field == NodeSettingsValidator.Fields.AutoConnectNodes);
    }

    [Fact]
    public async Task Runtime_AutoConnectsOnceAfterRegistrationSucceeds_AndDoesNotRedialAfterManualDisconnect()
    {
        using var remoteA = new FakeIaxRemote();
        using var remoteB = new FakeIaxRemote();
        var handler = new StubRegistry { AllowRegistration = true };
        using var http = new HttpClient(handler);

        var settings = new NodeSettings
        {
            NodeNumber = "523802",
            RegisterOnStartup = true,
            AutoReconnect = false,
            AutoConnectNodes = ["2000", $"1999@127.0.0.1:{remoteB.EndPoint.Port}"],
            PrivateNodes = [new PrivateNodeEntry("1999", "127.0.0.1", remoteB.EndPoint.Port, "Home Hub")]
        };

        var secrets = new InMemorySecretStore();
        await secrets.SetAsync(SecretKeys.NodePassword, "test-password");

        var resolver = new NodeResolution();
        resolver.SetStaticOverride("2000", remoteA.EndPoint);

        await using var runtime = new NodeRuntime(
            new NodeConfigService(new Store(settings), secrets),
            () => new NullAudioDeviceService(),
            resolver: resolver,
            registrationFactory: cfg => new AslRegistrationService(cfg, http),
            networkMonitor: new Network(),
            configureSession: s => { s.KeepAliveInterval = TimeSpan.FromMinutes(1); s.HangupAckWait = TimeSpan.Zero; });

        var acceptA = remoteA.AcceptCallAsync();
        var acceptB = remoteB.AcceptCallAsync();
        await runtime.StartAsync();

        await Task.WhenAll(acceptA, acceptB).WaitAsync(TimeSpan.FromSeconds(5));
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (runtime.Connections.Count < 2 && !wait.IsCancellationRequested)
            await Task.Delay(20);

        Assert.Equal(2, runtime.Connections.Count);
        Assert.Equal("2000", runtime.TransmitNode); // first startup node is TX
        Assert.Contains(runtime.Connections, c => c.Node == "1999" && c.IsPrivate && c.PrivateName == "Home Hub");

        // Manual disconnect followed by registration renewal must NOT auto-connect again in the same session.
        await runtime.DisconnectAsync();
        Assert.Empty(runtime.Connections);

        bool renewed = await runtime.RenewRegistrationAsync();
        Assert.True(renewed);
        await Task.Delay(150);
        Assert.Empty(runtime.Connections);
    }
}
