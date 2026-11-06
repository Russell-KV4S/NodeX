using System.Net;
using System.Text;
using NodeX.Core;

namespace NodeX.Core.Tests;

public class NodeResolutionTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);

    private sealed class DnsRecorder
    {
        public List<string> Hosts { get; } = [];
        public Dictionary<string, IPAddress[]> Answers { get; } = [];

        public Task<IPAddress[]> Resolve(string host, CancellationToken _)
        {
            Hosts.Add(host);
            return Answers.TryGetValue(host, out var a)
                ? Task.FromResult(a)
                : Task.FromException<IPAddress[]>(new System.Net.Sockets.SocketException(11001));
        }
    }

    private static (NodeResolution resolver, StubHandler http, DnsRecorder dns) Create(
        Func<HttpRequestMessage, HttpResponseMessage> respond, Func<DateTime>? clock = null)
    {
        var http = new StubHandler(respond);
        var dns = new DnsRecorder();
        var r = new NodeResolution(new HttpClient(http), dns.Resolve, clock) { PortProbe = null };   // no real UDP in tests
        return (r, http, dns);
    }

    [Fact]
    public async Task ListedPortThatIsSilent_FallsBackToTheStandardPort_WhenItAnswers()
    {
        // Verified live 2026-11-06: node 45192 is listed on 4579 but only answers on 4569.
        var (r, _, _) = Create(_ => Json("""{"node":{"ipaddr":"203.0.113.9","port":4579,"callsign":"W2ECR"}}"""));
        var probed = new List<int>();
        r.PortProbe = (ep, _) => { probed.Add(ep.Port); return Task.FromResult(ep.Port == 4569); };

        var info = await r.ResolveNodeAsync("45192");

        Assert.Equal(new IPEndPoint(IPAddress.Parse("203.0.113.9"), 4569), info!.EndPoint);
        Assert.Equal(new[] { 4579, 4569 }, probed);
    }

    [Fact]
    public async Task ListedPortThatAnswers_IsKept_AndStandardPortIsNotProbed()
    {
        var (r, _, _) = Create(_ => Json("""{"node":{"ipaddr":"203.0.113.9","port":4579,"callsign":"W2ECR"}}"""));
        var probed = new List<int>();
        r.PortProbe = (ep, _) => { probed.Add(ep.Port); return Task.FromResult(true); };

        var info = await r.ResolveNodeAsync("27339");

        Assert.Equal(4579, info!.EndPoint.Port);
        Assert.Equal(new[] { 4579 }, probed);
    }

    [Fact]
    public async Task NeitherPortAnswers_KeepsTheListedPort()
    {
        var (r, _, _) = Create(_ => Json("""{"node":{"ipaddr":"203.0.113.9","port":4579,"callsign":"X"}}"""));
        r.PortProbe = (_, _) => Task.FromResult(false);

        var info = await r.ResolveNodeAsync("1234");

        Assert.Equal(4579, info!.EndPoint.Port);
    }

    [Fact]
    public async Task StandardListedPort_IsNotProbed()
    {
        var (r, _, _) = Create(_ => Json("""{"node":{"ipaddr":"203.0.113.9","port":4569,"callsign":"X"}}"""));
        int probes = 0;
        r.PortProbe = (_, _) => { probes++; return Task.FromResult(false); };

        await r.ResolveNodeAsync("1234");

        Assert.Equal(0, probes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankNodeNumber_ReturnsNull_WithoutLookups(string? node)
    {
        var (r, http, dns) = Create(_ => NotFound());
        Assert.Null(await r.ResolveNodeAsync(node!));
        Assert.Empty(http.Requests);
        Assert.Empty(dns.Hosts);
    }

    [Fact]
    public async Task StaticOverride_WinsWithoutNetwork()
    {
        var (r, http, dns) = Create(_ => throw new InvalidOperationException("no network expected"));
        var ep = new IPEndPoint(IPAddress.Parse("192.168.1.50"), 4570);
        r.SetStaticOverride("2000", ep);

        var info = await r.ResolveNodeAsync("2000");

        Assert.NotNull(info);
        Assert.Equal(ep, info.EndPoint);
        Assert.Equal("STATIC", info.Callsign);
        Assert.Empty(http.Requests);
        Assert.Empty(dns.Hosts);
    }

    [Fact]
    public async Task RemoveStaticOverride_FallsBackToDirectory()
    {
        var (r, _, _) = Create(_ => Json("""{"node":{"ipaddr":"203.0.113.9","port":4569,"callsign":"W1AW"}}"""));
        r.SetStaticOverride("2000", new IPEndPoint(IPAddress.Loopback, 1));
        r.RemoveStaticOverride("2000");

        var info = await r.ResolveNodeAsync("2000");
        Assert.Equal("W1AW", info!.Callsign);
    }

    [Fact]
    public async Task Directory_ParsesAllFields_AndQueriesExpectedUrl()
    {
        var (r, http, dns) = Create(_ => Json(
            """{"node":{"ipaddr":"203.0.113.7","port":4570,"callsign":"K1ABC","node_frequency":"Hilltop Repeater"}}"""));

        var info = await r.ResolveNodeAsync(" 41522 ");

        Assert.NotNull(info);
        Assert.Equal("41522", info.NodeNumber);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("203.0.113.7"), 4570), info.EndPoint);
        Assert.Equal("K1ABC", info.Callsign);
        Assert.Equal("Hilltop Repeater", info.Description);
        Assert.Equal("https://stats.allstarlink.org/api/stats/41522", http.Requests.Single().ToString());
        Assert.Empty(dns.Hosts);
    }

    /// <summary>Shape captured live from GET /api/stats/2530 (trimmed): linked nodes carry their own ipaddr/port.</summary>
    [Fact]
    public async Task Directory_RealResponseShape_UsesTopLevelNode_NotLinkedNodes()
    {
        var (r, _, _) = Create(_ => Json("""
            {"stats":{"node":2530,"data":{"linkedNodes":[{"name":2333,"ipaddr":"198.51.100.1","port":4569,"callsign":"OTHER"}]}},
             "node":{"name":2530,"ipaddr":"203.0.113.50","port":4569,"callsign":"WD6AWP","node_frequency":"SoCal Hub","Status":"Active"},
             "keyups":[],"time":10.7}
            """));

        var info = await r.ResolveNodeAsync("2530");

        Assert.Equal(new IPEndPoint(IPAddress.Parse("203.0.113.50"), 4569), info!.EndPoint);
        Assert.Equal("WD6AWP", info.Callsign);
        Assert.Equal("SoCal Hub", info.Description);
    }

    [Fact]
    public async Task Directory_MissingPort_DefaultsTo4569()
    {
        var (r, _, _) = Create(_ => Json("""{"node":{"ipaddr":"203.0.113.7"}}"""));
        var info = await r.ResolveNodeAsync("1999");
        Assert.Equal(Iax2Protocol.DefaultPort, info!.EndPoint.Port);
        Assert.Equal(string.Empty, info.Callsign);
    }

    [Fact]
    public async Task Directory_EscapesNodeNumberInUrl()
    {
        var (r, http, _) = Create(_ => NotFound());
        await r.ResolveNodeAsync("12/../34");
        Assert.DoesNotContain("/../", http.Requests.Single().AbsoluteUri);
    }

    [Fact]
    public async Task Cache_ServesRepeatLookups_UntilTtlExpires()
    {
        DateTime now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
        var (r, http, _) = Create(_ => Json("""{"node":{"ipaddr":"203.0.113.7"}}"""), () => now);
        r.CacheTtl = TimeSpan.FromMinutes(15);

        await r.ResolveNodeAsync("2000");
        now = now.AddMinutes(14);
        await r.ResolveNodeAsync("2000");
        Assert.Single(http.Requests);

        now = now.AddMinutes(2);
        await r.ResolveNodeAsync("2000");
        Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public async Task ClearCache_ForcesFreshLookup()
    {
        var (r, http, _) = Create(_ => Json("""{"node":{"ipaddr":"203.0.113.7"}}"""));
        await r.ResolveNodeAsync("2000");
        r.ClearCache();
        await r.ResolveNodeAsync("2000");
        Assert.Equal(2, http.Requests.Count);
    }

    [Theory]
    [InlineData("""{"node":{"ipaddr":"not-an-ip"}}""")]
    [InlineData("""{"data":{"ip":"203.0.113.7"}}""")] // the old, wrong schema must no longer be accepted
    [InlineData("""{"error":"not found"}""")]
    [InlineData("this is not json")]
    public async Task BadDirectoryResponse_FallsBackToDnsInSpecOrder(string body)
    {
        var (r, _, dns) = Create(_ => Json(body));
        dns.Answers["2000.nodes.allstarlink.org"] = [IPAddress.Parse("198.51.100.20")];

        var info = await r.ResolveNodeAsync("2000");

        Assert.Equal(new IPEndPoint(IPAddress.Parse("198.51.100.20"), 4569), info!.EndPoint);
        Assert.Equal("DNS Fallback", info.Description);
        Assert.Equal(["2000.nodes.allstarlink.org"], dns.Hosts);
    }

    [Fact]
    public async Task Dns_FallsBackToLegacyHost()
    {
        var (r, _, dns) = Create(_ => NotFound());
        dns.Answers["node2000.allstarlink.org"] = [IPAddress.Parse("198.51.100.21")];

        var info = await r.ResolveNodeAsync("2000");

        Assert.Equal(IPAddress.Parse("198.51.100.21"), info!.EndPoint.Address);
        Assert.Equal(["2000.nodes.allstarlink.org", "node2000.allstarlink.org"], dns.Hosts);
    }

    [Fact]
    public async Task Dns_PrefersIPv4_ForTheIPv4Socket()
    {
        var (r, _, dns) = Create(_ => NotFound());
        dns.Answers["2000.nodes.allstarlink.org"] = [IPAddress.IPv6Loopback, IPAddress.Parse("198.51.100.22")];

        var info = await r.ResolveNodeAsync("2000");
        Assert.Equal(IPAddress.Parse("198.51.100.22"), info!.EndPoint.Address);
    }

    [Fact]
    public async Task AllLookupsFail_ReturnsNull()
    {
        var (r, _, dns) = Create(_ => throw new HttpRequestException("offline"));
        Assert.Null(await r.ResolveNodeAsync("2000"));
        Assert.Equal(2, dns.Hosts.Count);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        var (r, _, _) = Create(_ => NotFound());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => r.ResolveNodeAsync("2000", cts.Token));
    }
}
