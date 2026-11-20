using System.Net;
using System.Text;
using NodeX.Core.Audio;
using NodeX.Core.Settings;
using NodeX.UI.ViewModels;
using Xunit;

namespace NodeX.Core.Tests;

public class PrivateNodesTests
{
    // =========================================================================
    // 1. Parser table (§4.9 ManualNodeAddress.TryParse)
    // =========================================================================

    [Theory]
    [InlineData("1999", false, "1999", null, 4569, false)]
    [InlineData("  27339  ", false, "27339", null, 4569, false)]
    [InlineData("1999@club.ddns.net", true, "1999", "club.ddns.net", 4569, false)]
    [InlineData("1999@CLUB.DDNS.NET:4570", true, "1999", "club.ddns.net", 4570, false)]
    [InlineData("1999 @ club.ddns.net : 4570", true, "1999", "club.ddns.net", 4570, false)]
    [InlineData("1999@192.168.1.50:4570", true, "1999", "192.168.1.50", 4570, false)]
    [InlineData("1005@shackpi", true, "1005", "shackpi", 4569, false)]
    [InlineData("1999@[2001:db8::5]:4569", true, "1999", "2001:db8::5", 4569, true)]
    [InlineData("1999@[2001:db8::5]", true, "1999", "2001:db8::5", 4569, true)]
    [InlineData("1999 @ [2001:DB8::5] : 4571", true, "1999", "2001:db8::5", 4571, true)]
    [InlineData("radio@club.ddns.net:4569/1999", true, "1999", "club.ddns.net", 4569, false)]
    [InlineData("radio@club.ddns.net/1999", true, "1999", "club.ddns.net", 4569, false)]
    [InlineData("radio@club.ddns.net:4570/1999,NONE", true, "1999", "club.ddns.net", 4570, false)]
    [InlineData("radio@club.ddns.net:4570/1999,198.51.100.14", true, "1999", "club.ddns.net", 4570, false)]
    [InlineData("1999 = radio@club.ddns.net:4570/1999,NONE", true, "1999", "club.ddns.net", 4570, false)]
    [InlineData("1999 = radio @ club.ddns.net : 4570 / 1999 , 198.51.100.14", true, "1999", "club.ddns.net", 4570, false)]
    [InlineData("1999 = radio@[2001:db8::5]:4569/1999,NONE", true, "1999", "2001:db8::5", 4569, true)]
    public void TryParse_ValidInputs_ParseAsExpected(
        string input, bool expectedPrivate, string expectedNode, string? expectedHost, int expectedPort, bool expectedIpv6)
    {
        bool ok = ManualNodeAddress.TryParse(input, out var addr, out string? error);

        Assert.True(ok, $"Expected '{input}' to parse, got error: {error}");
        Assert.Null(error);
        Assert.NotNull(addr);
        Assert.Equal(expectedPrivate, addr!.IsPrivate);
        Assert.Equal(expectedNode, addr.Node);
        Assert.Equal(expectedHost, addr.Host);
        Assert.Equal(expectedPort, addr.Port);
        Assert.Equal(expectedIpv6, addr.IsIpv6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12")]
    [InlineData("12345678")]
    [InlineData("abc")]
    [InlineData("1999@")]
    [InlineData("12@club.ddns.net")]
    [InlineData("1999@club.ddns.net:0")]
    [InlineData("1999@club.ddns.net:65536")]
    [InlineData("1999@club.ddns.net:abc")]
    [InlineData("1999@999.1.1.1")]
    [InlineData("1999@2001:db8::5:4569")]
    [InlineData("1999@[not-ipv6]:4569")]
    [InlineData("1999@[2001:db8::5")]
    [InlineData("1999@a@b")]
    [InlineData("1999 = radio@club.ddns.net:4569/1998,NONE")]
    [InlineData("1999 = 1999@club.ddns.net")]
    [InlineData("radio@club.ddns.net:4569/12")]
    public void TryParse_InvalidInputs_FailWithHelpfulMessage(string input)
    {
        bool ok = ManualNodeAddress.TryParse(input, out var addr, out string? error);

        Assert.False(ok);
        Assert.Null(addr);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("admin@club.ddns.net:4569/1999")]
    [InlineData("iaxrpt@club.ddns.net:4569/1999,NONE")]
    [InlineData("1999 = allstar-public@club.ddns.net:4569/1999,NONE")]
    public void TryParse_RptConfWithNonRadioUser_ReportsExplicitRadioError(string input)
    {
        bool ok = ManualNodeAddress.TryParse(input, out var addr, out string? error);

        Assert.False(ok);
        Assert.Null(addr);
        Assert.Contains("radio", error!, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // 2. Share URI & rpt.conf line formatting
    // =========================================================================

    [Fact]
    public void ShareUri_RoundTrips_WithoutSecrets()
    {
        var original = new PrivateNodeEntry("1999", "club.ddns.net", 4570, "Club Repeater Hub");
        string uri = ManualNodeAddress.BuildShareUri(original);

        Assert.StartsWith("nodex://add?", uri);
        Assert.DoesNotContain("pass", uri, StringComparison.OrdinalIgnoreCase);

        bool ok = ManualNodeAddress.TryParseShareUri(uri, out var parsed, out string? error);
        Assert.True(ok, error);
        Assert.Equal("1999", parsed!.Node);
        Assert.Equal("club.ddns.net", parsed.Host);
        Assert.Equal(4570, parsed.Port);
        Assert.Equal("Club Repeater Hub", parsed.Name);
    }

    [Theory]
    [InlineData("https://example.com/add?node=1999&host=club.ddns.net")]
    [InlineData("nodex://connect?node=1999&host=club.ddns.net")]
    [InlineData("nodex://add?node=12&host=club.ddns.net")]
    [InlineData("nodex://add?node=1999&host=")]
    [InlineData("nodex://add?node=1999&host=club.ddns.net&port=99999")]
    public void ShareUri_InvalidInputs_Rejected(string badUri)
    {
        Assert.False(ManualNodeAddress.TryParseShareUri(badUri, out var entry, out string? error));
        Assert.Null(entry);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void FormatRptConfLine_UsesPerceivedAddressAndPort_AndHasPlaceholderWhenUnregistered()
    {
        string registered = ManualNodeAddress.FormatRptConfLine("523802", IPAddress.Parse("74.254.145.15"), 4569);
        Assert.Equal("523802 = radio@74.254.145.15:4569/523802,NONE", registered);

        string unregistered = ManualNodeAddress.FormatRptConfLine("523802", null, 0);
        Assert.Equal("523802 = radio@<your-public-ip>:4569/523802,NONE", unregistered);
    }

    // =========================================================================
    // 3. Settings validation, normalization, round-trip & export
    // =========================================================================

    [Fact]
    public void Settings_NormalizeAndValidate_PrivateNodes_DeduplicatesAndAllowsSameNumberSideBySide()
    {
        var raw = new NodeSettings
        {
            NodeNumber = "523802",
            FavoriteNodes = ["1999"],
            PrivateNodes =
            [
                new PrivateNodeEntry(" 1999 ", " CLUB.DDNS.NET ", 4569, "  Club A  "),
                new PrivateNodeEntry("1999", "club.ddns.net", 4569, "Duplicate ignored"),
                new PrivateNodeEntry("1999", "other.ddns.net", 4570, "Club B")
            ]
        };

        var normalized = NodeSettingsValidator.Normalize(raw);
        Assert.Equal(2, normalized.PrivateNodes.Count);
        Assert.Equal("club.ddns.net", normalized.PrivateNodes[0].Host);
        Assert.Equal("Club A", normalized.PrivateNodes[0].Name);
        Assert.Equal("other.ddns.net", normalized.PrivateNodes[1].Host);
        Assert.Equal(4570, normalized.PrivateNodes[1].Port);

        var issues = NodeSettingsValidator.Validate(normalized, "valid-pw");
        Assert.Empty(issues);
    }

    [Fact]
    public void Settings_Validate_RejectsBadPrivateNodeFields()
    {
        var bad = new NodeSettings
        {
            NodeNumber = "523802",
            PrivateNodes =
            [
                new PrivateNodeEntry("12", "bad host!", 70000, "Broken")
            ]
        };

        var issues = NodeSettingsValidator.Validate(bad, "valid-pw");
        Assert.Contains(issues, i => i.Field == NodeSettingsValidator.Fields.PrivateNodes && i.Message.Contains("3 to 7"));
        Assert.Contains(issues, i => i.Field == NodeSettingsValidator.Fields.PrivateNodes && i.Message.Contains("host"));
        Assert.Contains(issues, i => i.Field == NodeSettingsValidator.Fields.PrivateNodes && i.Message.Contains("port"));
    }

    [Fact]
    public void SettingsTransfer_ExportAndImport_PreservesPrivateNodes()
    {
        var settings = new NodeSettings
        {
            NodeNumber = "523802",
            FavoriteNodes = ["27339"],
            PrivateNodes =
            [
                new PrivateNodeEntry("1999", "club.ddns.net", 4570, "Club Hub"),
                new PrivateNodeEntry("1005", "192.168.1.50", 4569, "Shack LAN")
            ]
        };

        string exported = SettingsTransfer.Export(settings);
        var imported = SettingsTransfer.TryImport(exported);

        Assert.True(imported.Success, imported.Error);
        Assert.Equal(2, imported.Settings!.PrivateNodes.Count);
        Assert.Equal("club.ddns.net", imported.Settings.PrivateNodes[0].Host);
        Assert.Equal(4570, imported.Settings.PrivateNodes[0].Port);
        Assert.Equal("Club Hub", imported.Settings.PrivateNodes[0].Name);
        Assert.Equal("192.168.1.50", imported.Settings.PrivateNodes[1].Host);
    }

    // =========================================================================
    // 4. Resolution rules (§4.9)
    // =========================================================================

    private sealed class CountingHttpHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task Resolution_PrivateEntry_SkipsAllStarLink_ResolvesFreshOnEveryDial_UsesExactPortWithoutProbing()
    {
        var http = new CountingHttpHandler();
        int dnsCalls = 0;
        int probeCalls = 0;
        int dnsPortCalls = 0;
        IPAddress currentIp = IPAddress.Parse("198.51.100.10");

        var resolver = new NodeResolution(
            httpClient: new HttpClient(http),
            dnsResolver: (host, _) =>
            {
                Assert.Equal("club.ddns.net", host);
                dnsCalls++;
                // Return IPv6 first + IPv4 second to verify IPv4 preference.
                return Task.FromResult(new[] { IPAddress.Parse("2001:db8::1"), currentIp });
            })
        {
            PortProbe = (_, _) => { probeCalls++; return Task.FromResult(false); },
            DnsPort = (_, _) => { dnsPortCalls++; return Task.FromResult<int?>(4569); }
        };

        var addr = new ManualNodeAddress("1999", "club.ddns.net", 4570);

        var first = await resolver.ResolvePrivateAsync(addr, "Club Hub");
        Assert.NotNull(first);
        Assert.Equal(IPAddress.Parse("198.51.100.10"), first!.EndPoint.Address);
        Assert.Equal(4570, first.EndPoint.Port);

        // Simulate dynamic DNS updating to a new IP — second dial must resolve fresh (no 15-min cache).
        currentIp = IPAddress.Parse("198.51.100.22");
        var second = await resolver.ResolvePrivateAsync(addr, "Club Hub");
        Assert.NotNull(second);
        Assert.Equal(IPAddress.Parse("198.51.100.22"), second!.EndPoint.Address);
        Assert.Equal(4570, second.EndPoint.Port);

        Assert.Equal(2, dnsCalls);
        Assert.Equal(0, http.Calls);
        Assert.Equal(0, probeCalls);
        Assert.Equal(0, dnsPortCalls);
    }

    [Fact]
    public async Task Resolution_ProbePrivateAsync_ReportsReachable_NoAnswer_AndUnresolved()
    {
        var reachableResolver = new NodeResolution(
            dnsResolver: (_, _) => Task.FromResult(new[] { IPAddress.Loopback }))
        {
            PortProbe = (_, _) => Task.FromResult(true)
        };
        var ok = await reachableResolver.ProbePrivateAsync("club.ddns.net", 4570);
        Assert.True(ok.Resolved);
        Assert.True(ok.Reachable);
        Assert.Contains("Reachable", ok.Message);

        var silentResolver = new NodeResolution(
            dnsResolver: (_, _) => Task.FromResult(new[] { IPAddress.Loopback }))
        {
            PortProbe = (_, _) => Task.FromResult(false)
        };
        var silent = await silentResolver.ProbePrivateAsync("club.ddns.net", 4570);
        Assert.True(silent.Resolved);
        Assert.False(silent.Reachable);
        Assert.Contains("No answer", silent.Message);

        var missingResolver = new NodeResolution(
            dnsResolver: (_, _) => Task.FromResult(Array.Empty<IPAddress>()));
        var missing = await missingResolver.ProbePrivateAsync("missing.local", 4569);
        Assert.False(missing.Resolved);
        Assert.False(missing.Reachable);
        Assert.Contains("Cannot find host missing.local", missing.Message);
    }

    // =========================================================================
    // 5. Runtime: private login as `radio` with no secret, same-number refusal,
    //    unresolvable host retry with no public fallback
    // =========================================================================

    private sealed class MemStore(NodeSettings s) : ISettingsStore
    {
        public Task<NodeSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(s);
        public Task SaveAsync(NodeSettings value, CancellationToken ct = default) { s = value; return Task.CompletedTask; }
    }

    private sealed class StubNetwork : INetworkMonitor
    {
        public event Action? Changed { add { } remove { } }
        public bool IsAvailable => true;
        public string GetSignature() => "stub";
        public void Dispose() { }
    }

    private sealed class FakeRegHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"data":"successfully registered","refresh":60}""") });
    }

    [Fact]
    public async Task Runtime_PrivateLink_LogsInAsRadioWithNoSecret_AndRefusesSameNumberConflict()
    {
        const string regPassword = "SUPER_SECRET_REG_PASSWORD_99";
        using var remoteA = new FakeIaxRemote();
        using var remoteB = new FakeIaxRemote();
        var dirHttp = new CountingHttpHandler();
        using var regHttp = new HttpClient(new FakeRegHandler());

        var resolver = new NodeResolution(
            httpClient: new HttpClient(dirHttp),
            dnsResolver: (host, _) => Task.FromResult(new[] { IPAddress.Loopback }));
        resolver.SetStaticOverride("1999", remoteB.EndPoint); // public 1999 target

        var store = new MemStore(new NodeSettings { NodeNumber = "523802", RegisterOnStartup = false, AutoReconnect = false });
        var secrets = new InMemorySecretStore();
        await secrets.SetAsync(SecretKeys.NodePassword, regPassword);

        await using var runtime = new NodeRuntime(
            new NodeConfigService(store, secrets),
            () => new NullAudioDeviceService(),
            resolver: resolver,
            registrationFactory: cfg => new AslRegistrationService(cfg, regHttp),
            networkMonitor: new StubNetwork(),
            configureSession: s => { s.KeepAliveInterval = TimeSpan.FromMinutes(1); s.HangupAckWait = TimeSpan.Zero; });
        await runtime.StartAsync();

        var privAddr = new ManualNodeAddress("1999", "club-a.local", remoteA.EndPoint.Port);
        var info = await runtime.ConnectAsync(privAddr, "Club A");
        Assert.NotNull(info);

        // Inspect NEW frame from the fake remote: user must be "radio" and registration password must never be sent.
        var newFrame = await remoteA.WaitForIaxAsync(Iax2Protocol.IaxControlSubclass.New, timeoutMs: 3000);
        string rawAscii = Encoding.Latin1.GetString(newFrame.Payload);
        Assert.Contains("radio", rawAscii);
        Assert.DoesNotContain(regPassword, rawAscii);

        await remoteA.SendIaxAsync(Iax2Protocol.IaxControlSubclass.Accept);
        await Task.Delay(80);
        Assert.Equal(0, dirHttp.Calls);

        // Refusal 1: another private node with same number 1999 on a different host/port.
        var otherPriv = new ManualNodeAddress("1999", "club-b.local", remoteB.EndPoint.Port);
        var ex1 = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ConnectAsync(otherPriv, "Club B"));
        Assert.Contains("Node 1999 is already linked via", ex1.Message);
        Assert.Contains("club-a.local", ex1.Message);

        // Refusal 2: public node 1999 while private 1999 is already linked.
        var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ConnectAsync("1999"));
        Assert.Contains("Node 1999 is already linked via", ex2.Message);
        Assert.Contains("club-a.local", ex2.Message);

        // IPv6 literal refused cleanly on dial.
        var ipv6 = new ManualNodeAddress("1998", "2001:db8::5", 4569, IsIpv6: true);
        await Assert.ThrowsAsync<NotSupportedException>(() => runtime.ConnectAsync(ipv6));

        await runtime.DisconnectAsync();
    }

    [Fact]
    public async Task Runtime_PrivateHostUnresolvable_ReportsCannotFindHost_RetriesWithNoPublicFallback()
    {
        var dirHttp = new CountingHttpHandler();
        using var regHttp = new HttpClient(new FakeRegHandler());
        int dnsCalls = 0;

        var resolver = new NodeResolution(
            httpClient: new HttpClient(dirHttp),
            dnsResolver: (_, _) =>
            {
                Interlocked.Increment(ref dnsCalls);
                return Task.FromResult(Array.Empty<IPAddress>());
            });

        var store = new MemStore(new NodeSettings { NodeNumber = "523802", RegisterOnStartup = false, AutoReconnect = true });
        var secrets = new InMemorySecretStore();
        await secrets.SetAsync(SecretKeys.NodePassword, "pw");

        var policy = new ReconnectPolicy
        {
            InitialDelay = TimeSpan.FromMilliseconds(40),
            MaxDelay = TimeSpan.FromMilliseconds(80),
            MaxAttempts = 2
        };

        await using var runtime = new NodeRuntime(
            new NodeConfigService(store, secrets),
            () => new NullAudioDeviceService(),
            resolver: resolver,
            registrationFactory: cfg => new AslRegistrationService(cfg, regHttp),
            networkMonitor: new StubNetwork(),
            reconnectPolicy: policy);
        await runtime.StartAsync();

        var errors = new List<string>();
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.ErrorOccurred += msg =>
        {
            lock (errors) errors.Add(msg);
            if (Volatile.Read(ref dnsCalls) >= 2) retried.TrySetResult();
        };

        var info = await runtime.ConnectAsync(new ManualNodeAddress("1999", "offline.club.local", 4569));
        Assert.Null(info);

        await retried.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await runtime.DisconnectAsync();

        Assert.Equal(0, dirHttp.Calls);
        Assert.True(Volatile.Read(ref dnsCalls) >= 2);
        lock (errors)
        {
            Assert.Contains(errors, e => e.Contains("Cannot find host offline.club.local"));
        }
    }

    // =========================================================================
    // 6. CarBrowse & SettingsViewModel preservation
    // =========================================================================

    [Fact]
    public void CarBrowse_IncludesPrivateNodesMarkedPrivate_AndParsesMediaIds()
    {
        var settings = new NodeSettings
        {
            FavoriteNodes = ["27339"],
            FavoriteLabels = new() { ["27339"] = "ECR" },
            PrivateNodes = [new PrivateNodeEntry("1999", "club.ddns.net", 4570, "Club Hub")]
        };

        Assert.Equal(2, CarBrowse.TotalFavoritesCount(settings));

        var items = CarBrowse.BuildFavoritesItems(settings);
        Assert.Equal(2, items.Count);
        Assert.Contains("(Private)", items[1].Title);
        Assert.Contains("Private · 1999 @ club.ddns.net:4570", items[1].Subtitle);
        Assert.True(CarBrowse.TryParsePrivateMediaId(items[1].MediaId, out var parsed));
        Assert.Equal("1999", parsed!.Node);
        Assert.Equal("club.ddns.net", parsed.Host);
        Assert.Equal(4570, parsed.Port);
    }

    [Fact]
    public async Task SettingsViewModel_Save_PreservesExistingPrivateNodes()
    {
        var initial = new NodeSettings
        {
            NodeNumber = "523802",
            PrivateNodes = [new PrivateNodeEntry("1999", "club.ddns.net", 4570, "Club Hub")]
        };
        NodeSettings? saved = null;
        var store = new CapturingStore(initial, s => saved = s);
        var secrets = new InMemorySecretStore();
        await secrets.SetAsync(SecretKeys.NodePassword, "test-pw");

        var configService = new NodeConfigService(store, secrets);
        var vm = new SettingsViewModel(configService);
        await vm.LoadAsync();
        Assert.Contains("1 private node", vm.PrivateNodesSummaryText);

        vm.TogglePtt = true;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(saved);
        Assert.Single(saved!.PrivateNodes);
        Assert.Equal("club.ddns.net", saved.PrivateNodes[0].Host);
        Assert.Equal(4570, saved.PrivateNodes[0].Port);
    }

    private sealed class CapturingStore(NodeSettings current, Action<NodeSettings> onSave) : ISettingsStore
    {
        public Task<NodeSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(current);
        public Task SaveAsync(NodeSettings settings, CancellationToken ct = default)
        {
            current = settings;
            onSave(settings);
            return Task.CompletedTask;
        }
    }
}
