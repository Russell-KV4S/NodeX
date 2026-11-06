using System.Net;
using System.Text;
using NodeX.Core;

namespace NodeX.Core.Tests;

public class UsStatesTests
{
    [Theory]
    [InlineData("Austin, TX", "TX", true)]
    [InlineData("Memphis,TN", "TN", true)]
    [InlineData("Las Vegas NV ", "NV", true)]
    [InlineData("Dallas, tx", "TX", true)]
    [InlineData("Houston/Texas", "TX", true)]
    [InlineData("Omaha, Nebraska", "NE", true)]
    [InlineData("Wellsville NY", "NY", true)]
    [InlineData("(442.850) Titusville, FL", "FL", true)]
    [InlineData("Charleston, West Virginia", "WV", true)]
    [InlineData("Charleston, West Virginia", "VA", false)]   // West Virginia is not Virginia
    [InlineData("Richmond, Virginia", "VA", true)]
    [InlineData("Kansas City, MO", "KS", false)]             // a city name is not the state
    [InlineData("Kansas City, MO", "MO", true)]
    [InlineData("Oklahoma City, OK", "OK", true)]
    [InlineData("Repeater Site", "IN", false)]               // "in" inside a word, no code
    [InlineData("Repeater or Link", "OR", false)]            // lower-case word "or" is not Oregon
    [InlineData("Portland, OR", "OR", true)]
    [InlineData("Portland, ME", "OR", false)]
    [InlineData("Washington, DC", "WA", false)]
    [InlineData("Seattle, Washington", "WA", true)]
    [InlineData("", "TX", false)]
    [InlineData("Austin, TX", "ZZ", false)]
    public void Matches_RecognisesTheUsualSpellings(string location, string code, bool expected) =>
        Assert.Equal(expected, UsStates.Matches(location, code));

    [Fact]
    public void AllFiftyOneEntries_HaveUniqueCodesAndNames()
    {
        Assert.Equal(51, UsStates.All.Count);
        Assert.Equal(51, UsStates.All.Select(s => s.Code).Distinct().Count());
        Assert.Equal(51, UsStates.All.Select(s => s.Name).Distinct().Count());
    }
}

public class NodeDirectoryStateTests
{
    private static string Row(string node, string call, string location, string country = "United States") =>
        $"[\"<a href=\\\"/stats/{node}\\\">{node}</a> \",\"b\",\"{call}\",\"d{node}\",\"\",\"{location}\",\"{country}\",\"Site\",\"\",\"\"]";

    private static string Page(int total, params string[] rows) =>
        $"{{\"draw\":1,\"recordsTotal\":9000,\"recordsFiltered\":{total},\"data\":[{string.Join(',', rows)}]}}";

    private sealed class Stub(Func<string, string> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body = WebUtility.UrlDecode(await request.Content!.ReadAsStringAsync(ct));
            lock (Bodies) Bodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(body), Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task State_AsksForTheCodeAndTheName_KeepsOnlyRealMatches_AndMerges()
    {
        var stub = new Stub(body =>
            body.Contains("columns[5][search][value]=TX&")
                ? Page(3, Row("2002", "A", "Austin, TX"), Row("2001", "B", "Dallas, TX"), Row("2003", "C", "Boston, MA, Text"))   // 3rd: "TX" only inside a word
                : body.Contains("columns[5][search][value]=Texas&")
                    ? Page(2, Row("2001", "B", "Dallas, TX"), Row("2005", "D", "Houston/Texas"))
                    : Page(0));
        var dir = new NodeDirectory(new HttpClient(stub));

        var page = await dir.SearchAsync(new NodeSearchQuery { State = "tx" });

        Assert.Equal(["2001", "2002", "2005"], page.Nodes.Select(n => n.NodeNumber).ToArray());   // merged, de-duplicated, by node number
        Assert.Equal(3, page.Matching);
        Assert.False(page.HasMore);
        Assert.Contains("Texas", page.Note);
        Assert.Contains("not included", page.Note);

        Assert.Equal(2, stub.Bodies.Count);
        Assert.All(stub.Bodies, b => Assert.Contains("columns[6][search][value]=United States", b));   // implies the US
    }

    [Fact]
    public async Task State_WithACity_UsesTheCityOnTheServer_AndChecksTheState()
    {
        var stub = new Stub(_ => Page(2, Row("3000", "A", "Columbus, OH"), Row("3001", "B", "Columbus, GA")));
        var dir = new NodeDirectory(new HttpClient(stub));

        var page = await dir.SearchAsync(new NodeSearchQuery { State = "OH", Location = "Columbus" });

        Assert.Equal(["3000"], page.Nodes.Select(n => n.NodeNumber).ToArray());
        Assert.Single(stub.Bodies);
        Assert.Contains("columns[5][search][value]=Columbus", stub.Bodies[0]);
    }

    [Fact]
    public async Task State_SortsAndPagesLocally()
    {
        var rows = Enumerable.Range(0, 5).Select(i => Row((4000 + i).ToString(), $"K{5 - i}XYZ", "Reno, NV")).ToArray();
        var stub = new Stub(body => body.Contains("=NV&") ? Page(5, rows) : Page(0));
        var dir = new NodeDirectory(new HttpClient(stub));

        var first = await dir.SearchAsync(new NodeSearchQuery { State = "NV", Sort = NodeSort.Callsign, PageSize = 2 });
        Assert.Equal(["K1XYZ", "K2XYZ"], first.Nodes.Select(n => n.Callsign).ToArray());
        Assert.True(first.HasMore);

        var second = await dir.SearchAsync(new NodeSearchQuery { State = "NV", Sort = NodeSort.Callsign, PageSize = 2, Start = 2 });
        Assert.Equal(["K3XYZ", "K4XYZ"], second.Nodes.Select(n => n.Callsign).ToArray());
    }

    [Fact]
    public async Task UnknownState_ReturnsNothing_WithoutAskingTheServer()
    {
        var stub = new Stub(_ => Page(0));
        var dir = new NodeDirectory(new HttpClient(stub));

        var page = await dir.SearchAsync(new NodeSearchQuery { State = "ZZ" });

        Assert.Empty(page.Nodes);
        Assert.Empty(stub.Bodies);
        Assert.Contains("not a US state", page.Note);
    }

    [Fact]
    public void State_CountsAsAFilter()
    {
        Assert.False(new NodeSearchQuery { State = "TX" }.IsUnfiltered);
    }
}
