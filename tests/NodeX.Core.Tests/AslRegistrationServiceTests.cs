using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using NodeX.Core;

namespace NodeX.Core.Tests;

public class AslRegistrationServiceTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string? ContentType, string? UserAgent, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Requests)
                Requests.Add((request.Method, request.RequestUri!, request.Content?.Headers.ContentType?.MediaType,
                    request.Headers.UserAgent.ToString(), body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string OkReply =
        """{"ipaddr":"203.0.113.45","port":4569,"refresh":60,"data":"Node 55553 successfully registered"}""";

    private static AslRegistrationService Create(StubHandler h, TimeSpan? interval = null) =>
        new(new AslRegistrationConfig("55553", "s3cret", Interval: interval ?? default), new HttpClient(h));

    [Fact]
    public async Task Request_MatchesOfficialAsl3ModuleFormat()
    {
        var h = new StubHandler((_, _) => Json(OkReply));
        using var svc = Create(h);

        Assert.True(await svc.SendHeartbeatAsync());

        var req = Assert.Single(h.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://register.allstarlink.org/", req.Uri.ToString());   // root path, not /api/...
        Assert.Equal("application/json", req.ContentType);
        Assert.Contains("NodeX", req.UserAgent);

        var json = JsonNode.Parse(req.Body)!;
        Assert.Equal(4569, (int)json["port"]!);
        var node = json["data"]!["nodes"]!["55553"]!;
        Assert.Equal("55553", (string)node["node"]!);       // node number is a JSON string
        Assert.Equal("s3cret", (string)node["passwd"]!);
        Assert.Equal(0, (int)node["remote"]!);              // a real node, not a remote/web transceiver
        Assert.Equal(["port", "data"], json.AsObject().Select(p => p.Key).ToArray());
    }

    [Fact]
    public void Request_OmitsPort_WhenZero()
    {
        string json = AslRegistrationService.BuildRequestJson(new AslRegistrationConfig("1", "p", Port: 0));
        Assert.Null(JsonNode.Parse(json)!["port"]);
    }

    [Fact]
    public async Task Success_RecordsPerceivedAddress_AndServerRefresh()
    {
        var h = new StubHandler((_, _) => Json(OkReply));
        using var svc = Create(h);
        string? status = null;
        svc.RegistrationStatusChanged += (_, m) => status = m;

        Assert.True(await svc.SendHeartbeatAsync());

        Assert.True(svc.IsRegistered);
        Assert.NotNull(svc.LastSuccessfulHeartbeat);
        Assert.Equal(IPAddress.Parse("203.0.113.45"), svc.PerceivedAddress);
        Assert.Equal(4569, svc.PerceivedPort);
        Assert.Equal(TimeSpan.FromSeconds(60), svc.ServerRefresh);
        Assert.Equal("Registered as 203.0.113.45:4569", status);
    }

    [Fact]
    public async Task HttpOkButWrongStatusText_IsNotRegistered_AndShowsServerMessage()
    {
        var h = new StubHandler((_, _) => Json("""{"data":"Invalid password for node 55553"}"""));
        using var svc = Create(h);

        Assert.False(await svc.SendHeartbeatAsync());

        Assert.False(svc.IsRegistered);
        Assert.Contains("Invalid password", svc.LastStatusMessage);
        Assert.DoesNotContain("s3cret", svc.LastStatusMessage); // the password must never reach the UI
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("")]
    public async Task MalformedReply_IsNotRegistered(string body)
    {
        var h = new StubHandler((_, _) => Json(body));
        using var svc = Create(h);
        Assert.False(await svc.SendHeartbeatAsync());
        Assert.False(svc.IsRegistered);
    }

    [Fact]
    public async Task HttpError_IsNotRegistered_WithStatusCode()
    {
        var h = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var svc = Create(h);
        Assert.False(await svc.SendHeartbeatAsync());
        Assert.Contains("401", svc.LastStatusMessage);
    }

    [Fact]
    public async Task NetworkFailure_IsReported_NotThrown()
    {
        var h = new StubHandler((_, _) => throw new HttpRequestException("no route"));
        using var svc = Create(h);
        Assert.False(await svc.SendHeartbeatAsync());
        Assert.Contains("no route", svc.LastStatusMessage);
    }

    [Fact]
    public async Task Interval_DefaultsTo60s_AndNeverExceedsServerRefresh()
    {
        var h = new StubHandler((_, _) => Json("""{"refresh":30,"data":"successfully registered"}"""));
        using var svc = Create(h);
        Assert.Equal(TimeSpan.FromSeconds(60), svc.CurrentInterval);

        await svc.SendHeartbeatAsync();
        Assert.Equal(TimeSpan.FromSeconds(30), svc.CurrentInterval);   // server asked for faster

        var h2 = new StubHandler((_, _) => Json("""{"refresh":600,"data":"successfully registered"}"""));
        using var svc2 = Create(h2);
        await svc2.SendHeartbeatAsync();
        Assert.Equal(TimeSpan.FromSeconds(60), svc2.CurrentInterval);  // never slower than configured
    }

    [Fact]
    public async Task BackgroundLoop_RegistersImmediately_ThenRepeats_AndStops()
    {
        var h = new StubHandler((_, _) => Json(OkReply.Replace("\"refresh\":60", "\"refresh\":0")));
        using var svc = Create(h, interval: TimeSpan.FromMilliseconds(40));

        await svc.StartAsync();
        await Task.Delay(300);
        await svc.StopAsync();

        int count;
        lock (h.Requests) count = h.Requests.Count;
        Assert.InRange(count, 3, 12);
        Assert.False(svc.IsRegistered);
        Assert.Equal("Stopped", svc.LastStatusMessage);

        await Task.Delay(120);
        lock (h.Requests) Assert.Equal(count, h.Requests.Count);   // no requests after stop
    }
}
