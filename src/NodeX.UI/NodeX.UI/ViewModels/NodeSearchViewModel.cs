using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NodeX.Core;

namespace NodeX.UI.ViewModels;

/// <summary>A way to order the results.</summary>
public sealed record SortChoice(string Label, NodeSort Sort, bool Descending)
{
    public override string ToString() => Label;
}

/// <summary>One node in the results list.</summary>
public partial class NodeResultItem : ObservableObject
{
    public NodeResultItem(DirectoryNode node, bool isFavorite)
    {
        Node = node;
        IsFavorite = isFavorite;
    }

    public DirectoryNode Node { get; }
    public string NodeNumber => Node.NodeNumber;
    public string Callsign => Node.Callsign;

    /// <summary>The frequency or name the owner gave it, plus the CTCSS tone if there is one.</summary>
    public string Title
    {
        get
        {
            string d = Node.Description;
            if (Node.Tone.Length > 0) d = d.Length > 0 ? $"{d} · tone {Node.Tone}" : $"tone {Node.Tone}";
            return d.Length > 0 ? d : "(no description)";
        }
    }

    public string Place => string.Join(" · ", new[] { Node.Location, Node.Country }.Where(s => s.Length > 0));
    public bool IsActive => Node.Connections > 0;
    public string ActivityText => Node.Connections is > 0 ? $"{Node.Connections} linked" : "idle";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavoriteName), nameof(FavoriteGlyph))]
    public partial bool IsFavorite { get; set; }

    public string FavoriteGlyph => IsFavorite ? "★" : "☆";

    public string FavoriteName => IsFavorite ? $"Remove node {NodeNumber} from favorites" : $"Add node {NodeNumber} to favorites";
    public string UseName => $"Use node {NodeNumber}";
}

/// <summary>
/// "Find a node": searches the public AllStarLink node list as you type. One search box understands node
/// numbers (digits only) and free text (callsign, city, name, frequency); optional filters narrow by country,
/// frequency or name, and callsign; results can be ordered by activity. "Use" puts a node in the connect box.
/// </summary>
public partial class NodeSearchViewModel : ViewModelBase
{
    private readonly NodeDirectory _directory;
    private readonly Func<IReadOnlyCollection<string>> _favorites;
    private readonly Func<string, bool, Task<bool>> _setFavorite;
    private CancellationTokenSource? _cts;
    private bool _opened;

    public NodeSearchViewModel(
        NodeDirectory directory,
        Func<IReadOnlyCollection<string>> favorites,
        Func<string, bool, Task<bool>> setFavorite)
    {
        _directory = directory;
        _favorites = favorites;
        _setFavorite = setFavorite;
        _selectedSort = SortChoices[0];
    }

    /// <summary>Wait this long after the last keystroke before asking the server.</summary>
    public TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(450);

    /// <summary>Countries offered as one-tap choices (any other country can be typed).</summary>
    public string[] CountryChoices { get; } =
        ["United States", "Canada", "United Kingdom", "Australia", "Germany", "Japan", "Brazil", "New Zealand"];

    public SortChoice[] SortChoices { get; } =
    [
        new("Node number", NodeSort.NodeNumber, false),
        new("Callsign A–Z", NodeSort.Callsign, false),
        new("Frequency / name A–Z", NodeSort.Description, false)
    ];

    private SortChoice _selectedSort;

    public SortChoice SelectedSort
    {
        get => _selectedSort;
        set
        {
            if (value == null || SetProperty(ref _selectedSort, value)) ScheduleSearch(immediate: true);
        }
    }

    public ObservableCollection<NodeResultItem> Results { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchText))]
    public partial string SearchText { get; set; } = "";

    public bool HasSearchText => SearchText.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveFilterCount), nameof(HasActiveFilters))]
    public partial string Country { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveFilterCount), nameof(HasActiveFilters))]
    public partial string FrequencyOrName { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveFilterCount), nameof(HasActiveFilters))]
    public partial string Callsign { get; set; } = "";

    [ObservableProperty]
    public partial bool FiltersOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorText { get; set; } = "";

    [ObservableProperty]
    public partial string CountText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial string EmptyText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasMore { get; set; }

    public bool IsIdle => !IsBusy;
    public bool HasError => ErrorText.Length > 0;
    public bool ShowEmpty => EmptyText.Length > 0;

    public int ActiveFilterCount =>
        (Country.Trim().Length > 0 ? 1 : 0) + (FrequencyOrName.Trim().Length > 0 ? 1 : 0) + (Callsign.Trim().Length > 0 ? 1 : 0);

    public bool HasActiveFilters => ActiveFilterCount > 0;

    /// <summary>Raised when the user picks a node with "Use".</summary>
    public event Action<string>? NodeChosen;

    /// <summary>Raised when the user closes the screen.</summary>
    public event Action? Closed;

    partial void OnSearchTextChanged(string value) => ScheduleSearch();
    partial void OnCountryChanged(string value) => ScheduleSearch();
    partial void OnFrequencyOrNameChanged(string value) => ScheduleSearch();
    partial void OnCallsignChanged(string value) => ScheduleSearch();

    /// <summary>Called when the screen opens: refreshes the favourite stars and loads the first page once.</summary>
    public void Opened()
    {
        var favs = _favorites();
        foreach (var r in Results) r.IsFavorite = favs.Contains(r.NodeNumber);
        if (!_opened)
        {
            _opened = true;
            ScheduleSearch(immediate: true);
        }
    }

    private NodeSearchQuery BuildQuery(int start) => new()
    {
        Text = SearchText ?? "",
        Country = Country.Trim(),
        Description = FrequencyOrName.Trim(),
        Callsign = Callsign.Trim(),
        Sort = SelectedSort.Sort,
        Descending = SelectedSort.Descending,
        Start = start,
        PageSize = 50
    };

    private void ScheduleSearch(bool immediate = false)
    {
        if (!_opened) return;   // nothing is shown until the screen is opened

        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _cts, cts)?.Cancel();
        _ = RunAsync(BuildQuery(0), append: false, immediate ? TimeSpan.Zero : Debounce, cts);
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (IsBusy || !HasMore) return;
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _cts, cts)?.Cancel();
        await RunAsync(BuildQuery(Results.Count), append: true, TimeSpan.Zero, cts);
    }

    private async Task RunAsync(NodeSearchQuery query, bool append, TimeSpan delay, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);

            IsBusy = true;
            ErrorText = "";
            var page = await _directory.SearchAsync(query, ct);
            ct.ThrowIfCancellationRequested();

            var favs = _favorites();
            if (!append) Results.Clear();
            var have = Results.Select(r => r.NodeNumber).ToHashSet();
            foreach (var n in page.Nodes)
                if (have.Add(n.NodeNumber)) Results.Add(new NodeResultItem(n, favs.Contains(n.NodeNumber)));

            HasMore = page.HasMore && page.Nodes.Count > 0;
            CountText = query.IsUnfiltered
                ? $"{page.Matching.ToString("N0", CultureInfo.CurrentCulture)} nodes"
                : $"{page.Matching.ToString("N0", CultureInfo.CurrentCulture)} of {page.Listed.ToString("N0", CultureInfo.CurrentCulture)} nodes";
            EmptyText = Results.Count == 0
                ? query.IsUnfiltered ? "The node list is empty." : "No nodes match. Try fewer words or remove a filter."
                : "";
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer search.
        }
        catch (NodeDirectoryException ex)
        {
            if (!ct.IsCancellationRequested)
            {
                ErrorText = ex.Message;
                if (!append) { Results.Clear(); HasMore = false; CountText = ""; EmptyText = ""; }
            }
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) IsBusy = false;
        }
    }

    // ----- commands -----

    [RelayCommand]
    private void PickCountry(string country) => Country = string.Equals(Country, country, StringComparison.OrdinalIgnoreCase) ? "" : country;

    [RelayCommand]
    private void ClearFilters()
    {
        Country = "";
        FrequencyOrName = "";
        Callsign = "";
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    [RelayCommand]
    private void ToggleFilters() => FiltersOpen = !FiltersOpen;

    [RelayCommand]
    private void Retry() => ScheduleSearch(immediate: true);

    [RelayCommand]
    private void Use(NodeResultItem? item)
    {
        if (item == null) return;
        NodeChosen?.Invoke(item.NodeNumber);
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync(NodeResultItem? item)
    {
        if (item == null) return;
        bool want = !item.IsFavorite;
        if (await _setFavorite(item.NodeNumber, want)) item.IsFavorite = want;
    }

    [RelayCommand]
    private void Close() => Closed?.Invoke();
}
