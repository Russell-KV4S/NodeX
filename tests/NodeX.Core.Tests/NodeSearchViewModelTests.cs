using System.Net;
using System.Text;
using NodeX.Core;
using NodeX.UI.ViewModels;

namespace NodeX.Core.Tests;

public class NodeSearchViewModelTests
{
    private static string Row(string node, string call, string desc, string country, int? conn = null) =>
        $"[\"<a href=\\\"\\/stats\\/{node}\\\">{node}<\\/a> \",\"b\",\"{call}\",\"{desc}\",\"\",\"City\",\"{country}\",\"Site\",\"\",{(conn?.ToString() ?? "\"\"")}]";

    private static string Page(int matching, params string[] rows) =>
        $"{{\"draw\":1,\"recordsTotal\":1000,\"recordsFiltered\":{matching},\"data\":[{string.Join(',', rows)}]}}";

    private sealed class Stub(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body = WebUtility.UrlDecode(await request.Content!.ReadAsStringAsync(ct));
            lock (Bodies) Bodies.Add(body);
            return respond(body);
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (NodeSearchViewModel vm, Stub stub, List<(string node, bool fav)> saved) Create(
        Func<string, HttpResponseMessage>? respond = null, IReadOnlyCollection<string>? favorites = null, bool saveOk = true)
    {
        var stub = new Stub(respond ?? (_ => Json(Page(2, Row("2000", "WB6NIL", "ASL Hub", "United States"), Row("2200", "VE3TNK", "Hub Node", "Canada", 3)))));
        var saved = new List<(string, bool)>();
        var vm = new NodeSearchViewModel(
            new NodeDirectory(new HttpClient(stub)),
            () => favorites ?? [],
            (n, f) => { saved.Add((n, f)); return Task.FromResult(saveOk); })
        {
            Debounce = TimeSpan.Zero
        };
        return (vm, stub, saved);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition(), "condition not reached in time");
    }

    [Fact]
    public async Task Opening_LoadsTheFirstPage_Once()
    {
        var (vm, stub, _) = Create();
        vm.Opened();
        await WaitUntil(() => vm.Results.Count == 2);

        Assert.Equal("2000", vm.Results[0].NodeNumber);
        Assert.Equal("ASL Hub", vm.Results[0].Title);
        Assert.Equal("City · United States", vm.Results[0].Place);
        Assert.False(vm.Results[0].IsActive);
        Assert.True(vm.Results[1].IsActive);
        Assert.Equal("3 linked", vm.Results[1].ActivityText);

        vm.Opened();                       // reopening does not search again
        await Task.Delay(100);
        Assert.Single(stub.Bodies);
    }

    [Fact]
    public async Task Typing_Searches_AndDigitsGoToTheNodeColumn()
    {
        var (vm, stub, _) = Create();
        vm.Opened();
        await WaitUntil(() => vm.Results.Count == 2);

        vm.SearchText = "2530";
        await WaitUntil(() => stub.Bodies.Count == 2);
        Assert.Contains("columns[0][search][value]=2530", stub.Bodies[^1]);

        vm.SearchText = "montreal";
        await WaitUntil(() => stub.Bodies.Count == 3);
        Assert.Contains("search[value]=montreal", stub.Bodies[^1]);
    }

    [Fact]
    public async Task PickingACountry_FiltersAndTogglesOff()
    {
        var (vm, stub, _) = Create();
        vm.Opened();
        await WaitUntil(() => vm.Results.Count == 2);

        vm.PickCountryCommand.Execute("Canada");
        await WaitUntil(() => stub.Bodies.Count == 2);
        Assert.Contains("columns[6][search][value]=Canada", stub.Bodies[^1]);
        Assert.Equal(1, vm.ActiveFilterCount);
        Assert.True(vm.HasActiveFilters);

        vm.PickCountryCommand.Execute("canada");   // same one again clears it (the unfiltered page is cached, so no new request)
        await WaitUntil(() => !vm.IsBusy);
        Assert.Equal("", vm.Country);
        Assert.False(vm.HasActiveFilters);
    }

    [Fact]
    public async Task ClearFilters_ResetsAllThree()
    {
        var (vm, _, _) = Create();
        vm.Opened();
        vm.Country = "Canada"; vm.Callsign = "VE3"; vm.FrequencyOrName = "444";
        Assert.Equal(3, vm.ActiveFilterCount);

        vm.ClearFiltersCommand.Execute(null);
        Assert.Equal(0, vm.ActiveFilterCount);
    }

    [Fact]
    public async Task FailedSearch_ShowsTheMessage_AndRetryRecovers()
    {
        int calls = 0;
        var (vm, _, _) = Create(_ => ++calls == 1
            ? new HttpResponseMessage((HttpStatusCode)429)
            : Json(Page(1, Row("2000", "WB6NIL", "ASL Hub", "United States"))));

        vm.Opened();
        await WaitUntil(() => vm.HasError);
        Assert.Contains("busy", vm.ErrorText);
        Assert.Empty(vm.Results);

        vm.RetryCommand.Execute(null);
        await WaitUntil(() => vm.Results.Count == 1);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task NoMatches_SaysSo()
    {
        var (vm, _, _) = Create(_ => Json(Page(0)));
        vm.Opened();
        await WaitUntil(() => vm.ShowEmpty);
        vm.SearchText = "zzzz";
        await WaitUntil(() => vm.EmptyText.Contains("No nodes match"));
    }

    [Fact]
    public async Task ShowMore_AppendsWithoutDuplicates()
    {
        var (vm, stub, _) = Create(body => body.Contains("start=0")
            ? Json(Page(3, Row("2000", "A", "x", "US"), Row("2001", "B", "y", "US")))
            : Json(Page(3, Row("2001", "B", "y", "US"), Row("2002", "C", "z", "US"))));

        vm.Opened();
        await WaitUntil(() => vm.Results.Count == 2);
        Assert.True(vm.HasMore);

        await vm.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(["2000", "2001", "2002"], vm.Results.Select(r => r.NodeNumber).ToArray());
        Assert.Contains("start=2", stub.Bodies[^1]);
        Assert.False(vm.HasMore);
    }

    [Fact]
    public async Task Favorites_ShowStars_AndToggleSavesOnlyOnSuccess()
    {
        var (vm, _, saved) = Create(favorites: new[] { "2200" });
        vm.Opened();
        await WaitUntil(() => vm.Results.Count == 2);
        Assert.False(vm.Results[0].IsFavorite);
        Assert.True(vm.Results[1].IsFavorite);
        Assert.Equal("★", vm.Results[1].FavoriteGlyph);

        await vm.ToggleFavoriteCommand.ExecuteAsync(vm.Results[0]);
        Assert.True(vm.Results[0].IsFavorite);
        await vm.ToggleFavoriteCommand.ExecuteAsync(vm.Results[1]);
        Assert.False(vm.Results[1].IsFavorite);
        Assert.Equal([("2000", true), ("2200", false)], saved);

        var (vm2, _, _) = Create(saveOk: false);
        vm2.Opened();
        await WaitUntil(() => vm2.Results.Count == 2);
        await vm2.ToggleFavoriteCommand.ExecuteAsync(vm2.Results[0]);
        Assert.False(vm2.Results[0].IsFavorite);   // save failed, so the star does not change
    }

    [Fact]
    public async Task Use_RaisesNodeChosen()
    {
        var (vm, _, _) = Create();
        vm.Opened();
        await WaitUntil(() => vm.Results.Count == 2);
        string? chosen = null;
        vm.NodeChosen += n => chosen = n;

        vm.UseCommand.Execute(vm.Results[1]);
        Assert.Equal("2200", chosen);
    }

    [Fact]
    public async Task Sorting_ChangesTheRequest()
    {
        var (vm, stub, _) = Create();
        vm.Opened();
        await WaitUntil(() => vm.Results.Count == 2);

        vm.SelectedSort = vm.SortChoices.First(s => s.Sort == NodeSort.Callsign);
        await WaitUntil(() => stub.Bodies.Count == 2);
        Assert.Contains("order[0][column]=2&", stub.Bodies[^1]);
    }

    [Fact]
    public async Task StatePicker_SearchesBothSpellings_AndShowsTheBestEffortNote()
    {
        var (vm, stub, _) = Create(body => Json(Page(1, Row("2001", "K5ABC", "x", "United States"))));
        vm.Opened();
        await WaitUntil(() => vm.Results.Count == 1);

        vm.SelectedState = vm.StateChoices.First(c => c.Code == "TX");
        await WaitUntil(() => vm.HasNote);

        Assert.Equal(1, vm.ActiveFilterCount);
        Assert.Contains("Texas", vm.Note);
        Assert.Contains(stub.Bodies, b => b.Contains("columns[5][search][value]=TX&"));
        Assert.Contains(stub.Bodies, b => b.Contains("columns[5][search][value]=Texas&"));
        Assert.True(vm.FiltersOpen);                       // the picker opens the filter panel so the choice is visible
    }

    [Fact]
    public async Task City_GoesToTheLocationColumn_AndClearFiltersResetsStateAndCity()
    {
        var (vm, stub, _) = Create();
        vm.Opened();
        await WaitUntil(() => vm.Results.Count == 2);

        vm.City = "Austin";
        await WaitUntil(() => stub.Bodies.Count == 2);
        Assert.Contains("columns[5][search][value]=Austin", stub.Bodies[^1]);

        vm.SelectedState = vm.StateChoices.First(c => c.Code == "TX");
        Assert.Equal(2, vm.ActiveFilterCount);

        vm.ClearFiltersCommand.Execute(null);
        Assert.Equal(0, vm.ActiveFilterCount);
        Assert.Equal("", vm.SelectedState.Code);
        Assert.Equal("", vm.City);
    }
}
