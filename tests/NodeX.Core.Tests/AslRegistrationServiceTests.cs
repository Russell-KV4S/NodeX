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

    /// <summary>A registry that hangs on the first <paramref name="hangFirst"/> requests (so HttpClient times out) and then answers.</summary>
    private sealed class SlowThenOkHandler(int hangFirst) : HttpMessageHandler
    {
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _requests) <= hangFirst)
                await Task.Delay(Timeout.Infinite, ct);     // HttpClient.Timeout cancels this
            return Json(OkReply.Replace("\"refresh\":60", "\"refresh\":0"));
        }
    }

    [Fact]
    public async Task BackgroundLoop_SurvivesARequestThatTimesOut()
    {
        // A stalled connection (HttpClient.Timeout) surfaces as a cancellation exception that is not ours.
        // It must count as one failed beat, not end the loop: a node that stops re-registering drops out of
        // the registry and other nodes then refuse its links.
        var h = new SlowThenOkHandler(hangFirst: 1);
        var http = new HttpClient(h) { Timeout = TimeSpan.FromMilliseconds(80) };
        using var svc = new AslRegistrationService(
            new AslRegistrationConfig("55553", "s3cret", Interval: TimeSpan.FromMilliseconds(40)), http);

        await svc.StartAsync();
        await Wait.UntilAsync(() => svc.IsRegistered, timeoutMs: 5000);

        Assert.True(h.Requests >= 2);
        await svc.StopAsync();
    }

    private const string RefusedReply = """{"ipaddr":"203.0.113.45","port":4569,"refresh":60,"data":"Node 55553 bad password"}""";

    /// <summary>A registry whose answers are scripted per request ("ok", "refused", "offline", or an HTTP status code).</summary>
    private static StubHandler Scripted(params string[] script)
    {
        int n = 0;
        return new StubHandler((_, _) =>
        {
            string step = script[Math.Min(Interlocked.Increment(ref n) - 1, script.Length - 1)];
            return step switch
            {
                "ok" => Json(OkReply),
                "refused" => Json(RefusedReply),
                "offline" => throw new HttpRequestException("No route to host"),
                "garbage" => Json("<html>Sign in to this Wi-Fi network</html>"),
                _ => Json("{}", (HttpStatusCode)int.Parse(step))
            };
        });
    }

    private static List<(bool Registered, string Message)> Track(AslRegistrationService svc)
    {
        var events = new List<(bool, string)>();
        svc.RegistrationStatusChanged += (ok, msg) => { lock (events) events.Add((ok, msg)); };
        return events;
    }

    [Theory]
    [InlineData("offline")]     // no connection
    [InlineData("503")]         // registry (or its front end) in trouble
    [InlineData("429")]
    [InlineData("garbage")]     // a captive portal page instead of the registry's JSON
    public async Task OneRequestThatNeverReachesTheRegistry_IsRiddenOut_WhileRegistered(string blip)
    {
        using var svc = Create(Scripted("ok", blip, "ok"));
        var events = Track(svc);
        Assert.True(await svc.SendHeartbeatAsync());

        Assert.False(await svc.SendHeartbeatAsync());        // the caller is told this one failed...
        Assert.True(svc.IsRegistered);                       // ...but the node is still registered: nothing refused it
        Assert.StartsWith("Registered", svc.LastStatusMessage);
        lock (events) Assert.DoesNotContain(events, e => !e.Registered);

        Assert.True(await svc.SendHeartbeatAsync());
        Assert.True(svc.IsRegistered);
    }

    [Fact]
    public async Task TwoRequestsInARowThatFail_AreReported_AndRecoveryIsReportedToo()
    {
        using var svc = Create(Scripted("ok", "offline", "offline", "ok"));
        var events = Track(svc);
        await svc.SendHeartbeatAsync();

        Assert.False(await svc.SendHeartbeatAsync());
        Assert.True(svc.IsRegistered);
        Assert.False(await svc.SendHeartbeatAsync());        // still failing: now it is a real outage
        Assert.False(svc.IsRegistered);
        Assert.Contains("Connection failed", svc.LastStatusMessage);

        Assert.True(await svc.SendHeartbeatAsync());
        Assert.True(svc.IsRegistered);
        lock (events) Assert.Equal([true, false, true], events.Select(e => e.Registered));
    }

    [Theory]
    [InlineData("refused")]     // the registry answered and said no
    [InlineData("401")]
    public async Task ARefusalFromTheRegistry_IsReportedAtOnce_EvenWhileRegistered(string refusal)
    {
        using var svc = Create(Scripted("ok", refusal));
        await svc.SendHeartbeatAsync();

        Assert.False(await svc.SendHeartbeatAsync());
        Assert.False(svc.IsRegistered);
    }

    [Fact]
    public async Task AFailedFirstRegistration_IsReportedAtOnce()
    {
        // Nothing to ride out: the node was never registered (this is also what "Test registration" relies on).
        using var svc = Create(Scripted("offline"));
        Assert.False(await svc.SendHeartbeatAsync());
        Assert.False(svc.IsRegistered);
        Assert.Contains("Connection failed", svc.LastStatusMessage);
    }

    [Fact]
    public async Task RetryDelay_IsShortAfterALostRequest_BacksOff_AndReturnsToTheIntervalOnAnyRealAnswer()
    {
        using var svc = Create(Scripted("ok", "offline", "offline", "offline", "offline", "offline", "refused", "offline", "ok"));
        await svc.SendHeartbeatAsync();
        Assert.Equal(TimeSpan.FromSeconds(60), svc.NextDelay());

        var seen = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            await svc.SendHeartbeatAsync();
            seen.Add(svc.NextDelay().TotalSeconds);
        }
        Assert.Equal([5, 10, 20, 40, 60], seen);             // 5 s, doubling, never beyond the normal interval

        await svc.SendHeartbeatAsync();                      // refused: an answer, so no hurry and no hammering
        Assert.Equal(TimeSpan.FromSeconds(60), svc.NextDelay());

        await svc.SendHeartbeatAsync();                      // lost again: the back-off starts over
        Assert.Equal(TimeSpan.FromSeconds(5), svc.NextDelay());
        await svc.SendHeartbeatAsync();                      // ok
        Assert.Equal(TimeSpan.FromSeconds(60), svc.NextDelay());
    }

    [Fact]
    public async Task BackgroundLoop_RetriesSoonAfterALostRequest_InsteadOfWaitingAWholeInterval()
    {
        var h = Scripted("offline", "ok");
        using var svc = new AslRegistrationService(
            new AslRegistrationConfig("55553", "s3cret", Interval: TimeSpan.FromSeconds(30), RetryDelay: TimeSpan.FromMilliseconds(40)),
            new HttpClient(h));

        await svc.StartAsync();
        await Wait.UntilAsync(() => svc.IsRegistered, timeoutMs: 5000);     // would take 30 s without the retry

        await svc.StopAsync();
    }

    [Fact]
    public async Task ImmediateRequestThatFails_BringsTheNextScheduledOneForward()
    {
        // What happens on a network change: the runtime registers at once; if that request is lost the loop must
        // try again within seconds, not at its next 60 s beat.
        var h = Scripted("ok", "offline", "ok");
        using var svc = new AslRegistrationService(
            new AslRegistrationConfig("55553", "s3cret", Interval: TimeSpan.FromSeconds(30), RetryDelay: TimeSpan.FromMilliseconds(40)),
            new HttpClient(h));
        await svc.StartAsync();
        await Wait.UntilAsync(() => svc.IsRegistered, timeoutMs: 5000);

        Assert.False(await svc.SendHeartbeatAsync());

        await Wait.UntilAsync(() => { lock (h.Requests) return h.Requests.Count >= 3; }, timeoutMs: 5000);
        Assert.True(svc.IsRegistered);
        await svc.StopAsync();
    }
}
