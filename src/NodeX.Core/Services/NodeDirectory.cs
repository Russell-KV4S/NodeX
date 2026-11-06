using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NodeX.Core;

/// <summary>One row of the AllStarLink public node list.</summary>
/// <param name="Callsign">Owner / callsign shown in the list's "Node ID" column.</param>
/// <param name="Description">The list's free-text "Freq" column: a frequency such as "444.250+", or a name such as "Hub Node".</param>
/// <param name="Connections">How many nodes it is currently linked to; null when the list shows none.</param>
public sealed record DirectoryNode(
    string NodeNumber,
    string Callsign,
    string Description,
    string Tone,
    string Location,
    string Country,
    string SiteName,
    string Affiliation,
    int? Connections);

/// <summary>
/// What the node list can be sorted by. The list's own page only lets these columns be ordered; Location, Country
/// and Connections are not sortable on the server, so they are deliberately not offered.
/// </summary>
public enum NodeSort
{
    NodeNumber,
    Callsign,
    Description,
    SiteName
}

/// <summary>
/// A node search. Every non-empty text field narrows the result (all must match); each matches as a
/// case-insensitive "contains". <see cref="Text"/> searches every column at once; the others target one column.
/// </summary>
public sealed record NodeSearchQuery
{
    /// <summary>Free text matched against every column.</summary>
    public string Text { get; init; } = "";

    public string NodeNumber { get; init; } = "";
    public string Callsign { get; init; } = "";
    public string Description { get; init; } = "";
    public string Location { get; init; } = "";
    public string Country { get; init; } = "";
    public string SiteName { get; init; } = "";

    public NodeSort Sort { get; init; } = NodeSort.NodeNumber;
    public bool Descending { get; init; }

    /// <summary>Zero-based index of the first row wanted.</summary>
    public int Start { get; init; }

    public int PageSize { get; init; } = 50;

    /// <summary>
    /// Typing only digits in a free-text box means a node number, so it is routed to the node column; anything
    /// else is searched everywhere. Returns the query as it should be sent.
    /// </summary>
    public NodeSearchQuery Interpreted()
    {
        string t = (Text ?? "").Trim();
        if (t.Length > 0 && t.All(char.IsAsciiDigit) && NodeNumber.Length == 0)
            return this with { Text = "", NodeNumber = t };
        return this with { Text = t };
    }

    /// <summary>True when nothing narrows the list.</summary>
    public bool IsUnfiltered =>
        Interpreted() is var q
        && q.Text.Length == 0 && q.NodeNumber.Length == 0 && q.Callsign.Length == 0 && q.Description.Length == 0
        && q.Location.Length == 0 && q.Country.Length == 0 && q.SiteName.Length == 0;
}

/// <summary>One page of results.</summary>
/// <param name="Matching">How many nodes match the query in total (not just this page).</param>
/// <param name="Listed">How many nodes the list holds before filtering.</param>
public sealed record NodeSearchPage(IReadOnlyList<DirectoryNode> Nodes, int Matching, int Listed, int Start)
{
    public bool HasMore => Start + Nodes.Count < Matching;
}

/// <summary>
/// Searches the public AllStarLink node list (the same list stats.allstarlink.org shows).
/// <para>
/// This uses the stats website's own list endpoint (<c>POST /api/stats/nodeList</c>, a server-side DataTables
/// feed). It is not a documented public API, so its shape can change and it is rate limited (HTTP 429). The
/// service therefore sends one request at a time, caches identical queries briefly, and reports failures as
/// <see cref="NodeDirectoryException"/> with a message fit for the screen.
/// </para>
/// </summary>
public sealed partial class NodeDirectory
{
    // Column order of the list; the server matches filters and sort by position.
    private const int ColumnCount = 10;
    private const int ColNode = 0, ColCallsign = 2, ColDescription = 3, ColLocation = 5, ColCountry = 6, ColSite = 7, ColConnections = 9;

    private readonly HttpClient _http;
    private readonly Func<DateTime> _utcNow;
    private readonly Dictionary<string, (NodeSearchPage Page, DateTime ExpiresAt)> _cache = [];
    private readonly Lock _cacheLock = new();
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public NodeDirectory(HttpClient? httpClient = null, Func<DateTime>? utcNow = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>The list endpoint; change only for tests.</summary>
    public string Endpoint { get; set; } = "https://stats.allstarlink.org/api/stats/nodeList";

    /// <summary>How long an identical query is answered from memory.</summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromMinutes(2);

    public async Task<NodeSearchPage> SearchAsync(NodeSearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var q = query.Interpreted();
        q = q with { PageSize = Math.Clamp(q.PageSize, 1, 200), Start = Math.Max(0, q.Start) };

        string key = CacheKey(q);
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var hit) && _utcNow() < hit.ExpiresAt) return hit.Page;
        }

        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new FormUrlEncodedContent(BuildForm(q)) };
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("NodeX", "1.0"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode == 429)
                throw new NodeDirectoryException("The AllStarLink node list is busy. Wait a moment and try again.");
            if (!response.IsSuccessStatusCode)
                throw new NodeDirectoryException($"The AllStarLink node list returned an error ({(int)response.StatusCode}).");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var page = Parse(stream, q.Start);

            lock (_cacheLock)
            {
                if (_cache.Count > 100) _cache.Clear();
                _cache[key] = (page, _utcNow().Add(CacheTtl));
            }
            return page;
        }
        catch (NodeDirectoryException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new NodeDirectoryException("The AllStarLink node list took too long to answer.");
        }
        catch (HttpRequestException ex)
        {
            throw new NodeDirectoryException($"Could not reach the AllStarLink node list: {ex.Message}");
        }
        catch (JsonException)
        {
            throw new NodeDirectoryException("The AllStarLink node list sent something unexpected (its format may have changed).");
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    // ----- request -----

    private static string CacheKey(NodeSearchQuery q) =>
        string.Join('\u001f', q.Text.ToLowerInvariant(), q.NodeNumber, q.Callsign.ToLowerInvariant(), q.Description.ToLowerInvariant(),
            q.Location.ToLowerInvariant(), q.Country.ToLowerInvariant(), q.SiteName.ToLowerInvariant(),
            (int)q.Sort, q.Descending, q.Start, q.PageSize);

    internal static List<KeyValuePair<string, string>> BuildForm(NodeSearchQuery q)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("draw", "1"),
            new("start", q.Start.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("length", q.PageSize.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("search[value]", q.Text),
            new("search[regex]", "false"),
            new("order[0][column]", SortColumn(q.Sort).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("order[0][dir]", q.Descending ? "desc" : "asc")
        };

        string[] perColumn = new string[ColumnCount];
        perColumn[ColNode] = q.NodeNumber;
        perColumn[ColCallsign] = q.Callsign;
        perColumn[ColDescription] = q.Description;
        perColumn[ColLocation] = q.Location;
        perColumn[ColCountry] = q.Country;
        perColumn[ColSite] = q.SiteName;

        for (int i = 0; i < ColumnCount; i++)
        {
            string c = $"columns[{i}]";
            form.Add(new($"{c}[data]", i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            form.Add(new($"{c}[searchable]", "true"));
            form.Add(new($"{c}[orderable]", "true"));
            form.Add(new($"{c}[search][value]", perColumn[i] ?? ""));
            form.Add(new($"{c}[search][regex]", "false"));
        }
        return form;
    }

    private static int SortColumn(NodeSort sort) => sort switch
    {
        NodeSort.Callsign => ColCallsign,
        NodeSort.Description => ColDescription,
        NodeSort.SiteName => ColSite,
        _ => ColNode
    };

    // ----- response -----

    [GeneratedRegex(@">\s*(\d{1,7})\s*<")]
    private static partial Regex NodeNumberInHtml();

    internal static NodeSearchPage Parse(Stream json, int start)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new JsonException("No data array.");

        var nodes = new List<DirectoryNode>();
        foreach (var row in data.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < ColumnCount) continue;

            string node = NodeNumberFrom(Text(row, ColNode));
            if (node.Length == 0) continue;   // a row we cannot connect to is no use

            int? connections = null;
            string conn = Text(row, ColConnections);
            if (int.TryParse(conn, out int n) && n > 0) connections = n;

            nodes.Add(new DirectoryNode(
                node,
                Text(row, ColCallsign),
                Text(row, ColDescription),
                Text(row, 4),
                Text(row, ColLocation),
                Text(row, ColCountry),
                Text(row, ColSite),
                Text(row, 8),
                connections));
        }

        return new NodeSearchPage(nodes, Int(root, "recordsFiltered"), Int(root, "recordsTotal"), start);
    }

    private static string NodeNumberFrom(string html)
    {
        var m = NodeNumberInHtml().Match(html);
        if (m.Success) return m.Groups[1].Value;
        string plain = html.Trim();
        return plain.Length is >= 1 and <= 7 && plain.All(char.IsAsciiDigit) ? plain : "";
    }

    private static string Text(JsonElement row, int index)
    {
        var el = row[index];
        return el.ValueKind switch
        {
            JsonValueKind.String => (el.GetString() ?? "").Trim(),
            JsonValueKind.Number => el.GetRawText(),
            _ => ""
        };
    }

    private static int Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int v) ? v : 0;
}

/// <summary>The node list could not be searched; the message is meant to be shown to the user.</summary>
public sealed class NodeDirectoryException(string message) : Exception(message);
