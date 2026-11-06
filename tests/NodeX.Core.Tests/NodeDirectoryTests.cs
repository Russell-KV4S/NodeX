using System.Net;
using System.Text;
using NodeX.Core;

namespace NodeX.Core.Tests;

public class NodeDirectoryTests
{
    // Shape captured live from POST https://stats.allstarlink.org/api/stats/nodeList on 2026-11-06.
    private const string TwoRows = """
        {"draw":1,"recordsTotal":12987,"recordsFiltered":7,"data":[
          ["<a href=\"\/stats\/2200\" target=\"_blank\">2200<\/a> ","<a target=\"_blank\" href=\"\/stats\/2200\/networkMap\">Bubble<\/a>","VE3TNK","Hub Node","","Montreal, QC","Canada","AWS","VE3TNK, VA3PMO",1],
          ["<a href=\"\/stats\/44091\" target=\"_blank\">44091<\/a> ","<a target=\"_blank\" href=\"\/stats\/44091\/networkMap\">Bubble<\/a>","VE2REH","444.600+","114.8","Montreal, Quebec","Canada","Mont-Royal","ARAI",""]
        ],"totalRegistered":14314}
        """;

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public int Calls => Bodies.Count;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Bodies.Add(WebUtility.UrlDecode(await request.Content!.ReadAsStringAsync(ct)));
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (NodeDirectory dir, Stub stub) Create(Func<HttpRequestMessage, HttpResponseMessage>? respond = null, Func<DateTime>? clock = null)
    {
        var stub = new Stub(respond ?? (_ => Json(TwoRows)));
        return (new NodeDirectory(new HttpClient(stub), clock), stub);
    }

    [Fact]
    public async Task Parses_Rows_StrippingHtml_AndReadsCounts()
    {
        var (dir, _) = Create();
        var page = await dir.SearchAsync(new NodeSearchQuery { Text = "Montreal" });

        Assert.Equal(7, page.Matching);
        Assert.Equal(12987, page.Listed);
        Assert.Equal(2, page.Nodes.Count);
        Assert.True(page.HasMore);

        var first = page.Nodes[0];
        Assert.Equal("2200", first.NodeNumber);
        Assert.Equal("VE3TNK", first.Callsign);
        Assert.Equal("Hub Node", first.Description);
        Assert.Equal("Montreal, QC", first.Location);
        Assert.Equal("Canada", first.Country);
        Assert.Equal("AWS", first.SiteName);
        Assert.Equal("VE3TNK, VA3PMO", first.Affiliation);
        Assert.Equal(1, first.Connections);

        var second = page.Nodes[1];
        Assert.Equal("44091", second.NodeNumber);
        Assert.Equal("444.600+", second.Description);
        Assert.Equal("114.8", second.Tone);
        Assert.Null(second.Connections);   // "" means none
    }

    [Fact]
    public async Task FreeText_GoesToGlobalSearch_AndFieldsToTheirColumns()
    {
        var (dir, stub) = Create();
        await dir.SearchAsync(new NodeSearchQuery
        {
            Text = "repeater", Country = "Canada", Location = "Montreal", Callsign = "VE2",
            Description = "444", SiteName = "AWS", Start = 100, PageSize = 25, Sort = NodeSort.Description, Descending = true
        });

        string body = stub.Bodies.Single();
        Assert.Contains("search[value]=repeater", body);
        Assert.Contains("columns[6][search][value]=Canada", body);
        Assert.Contains("columns[5][search][value]=Montreal", body);
        Assert.Contains("columns[2][search][value]=VE2", body);
        Assert.Contains("columns[3][search][value]=444", body);
        Assert.Contains("columns[7][search][value]=AWS", body);
        Assert.Contains("start=100", body);
        Assert.Contains("length=25", body);
        Assert.Contains("order[0][column]=3", body);
        Assert.Contains("order[0][dir]=desc", body);
    }

    [Fact]
    public async Task OnlyDigits_AreTreatedAsANodeNumber()
    {
        var (dir, stub) = Create();
        await dir.SearchAsync(new NodeSearchQuery { Text = " 2530 " });

        string body = stub.Bodies.Single();
        Assert.Contains("columns[0][search][value]=2530", body);
        Assert.Contains("search[value]=&", body);   // global search left empty
    }

    [Theory]
    [InlineData(NodeSort.NodeNumber, 0)]
    [InlineData(NodeSort.Callsign, 2)]
    [InlineData(NodeSort.Description, 3)]
    [InlineData(NodeSort.SiteName, 7)]
    public async Task Sort_MapsToTheServerColumn(NodeSort sort, int column)
    {
        var (dir, stub) = Create();
        await dir.SearchAsync(new NodeSearchQuery { Sort = sort });
        Assert.Contains($"order[0][column]={column}&", stub.Bodies.Single());
    }

    [Fact]
    public void IsUnfiltered_IgnoresBlanksAndSort()
    {
        Assert.True(new NodeSearchQuery().IsUnfiltered);
        Assert.True(new NodeSearchQuery { Text = "  ", Sort = NodeSort.Callsign, Start = 50 }.IsUnfiltered);
        Assert.False(new NodeSearchQuery { Country = "Canada" }.IsUnfiltered);
        Assert.False(new NodeSearchQuery { Text = "2530" }.IsUnfiltered);
    }

    [Fact]
    public async Task PageSize_IsClamped()
    {
        var (dir, stub) = Create();
        await dir.SearchAsync(new NodeSearchQuery { PageSize = 100000, Start = -5 });
        Assert.Contains("length=200", stub.Bodies.Single());
        Assert.Contains("start=0", stub.Bodies.Single());
    }

    [Fact]
    public async Task IdenticalQuery_IsServedFromCache_UntilItExpires()
    {
        var now = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
        var (dir, stub) = Create(clock: () => now);
        var q = new NodeSearchQuery { Text = "hub" };

        await dir.SearchAsync(q);
        await dir.SearchAsync(q with { Text = "HUB " });   // same search once normalised? text differs only by case/space
        Assert.Equal(1, stub.Calls);

        await dir.SearchAsync(q with { Country = "Canada" });
        Assert.Equal(2, stub.Calls);

        now = now.AddMinutes(3);
        await dir.SearchAsync(q);
        Assert.Equal(3, stub.Calls);
    }

    [Fact]
    public async Task TooManyRequests_BecomesAFriendlyError()
    {
        var (dir, _) = Create(_ => new HttpResponseMessage((HttpStatusCode)429));
        var ex = await Assert.ThrowsAsync<NodeDirectoryException>(() => dir.SearchAsync(new NodeSearchQuery()));
        Assert.Contains("busy", ex.Message);
    }

    [Fact]
    public async Task ServerError_AndBadJson_AreReportedNotCrashed()
    {
        var (a, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await Assert.ThrowsAsync<NodeDirectoryException>(() => a.SearchAsync(new NodeSearchQuery()));

        var (b, _) = Create(_ => Json("<html>not json</html>"));
        var ex = await Assert.ThrowsAsync<NodeDirectoryException>(() => b.SearchAsync(new NodeSearchQuery()));
        Assert.Contains("format", ex.Message);

        var (c, _) = Create(_ => Json("""{"draw":1,"recordsTotal":1}"""));   // no data array
        await Assert.ThrowsAsync<NodeDirectoryException>(() => c.SearchAsync(new NodeSearchQuery()));
    }

    [Fact]
    public async Task RowsWithoutAUsableNodeNumber_AreSkipped()
    {
        const string body = """
            {"draw":1,"recordsTotal":3,"recordsFiltered":3,"data":[
              ["no number here","","X","","","","","","",""],
              ["<a href=\"\/stats\/2000\">2000<\/a>","","WB6NIL","ASL Public Hub","","","United States","AllStarLink","AllStarLink",""],
              ["too short"]
            ]}
            """;
        var (dir, _) = Create(_ => Json(body));
        var page = await dir.SearchAsync(new NodeSearchQuery());
        Assert.Equal(["2000"], page.Nodes.Select(n => n.NodeNumber).ToArray());
    }

    [Fact]
    public async Task Cancellation_IsNotReportedAsATimeout()
    {
        var (dir, _) = Create();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dir.SearchAsync(new NodeSearchQuery(), cts.Token));
    }
}
