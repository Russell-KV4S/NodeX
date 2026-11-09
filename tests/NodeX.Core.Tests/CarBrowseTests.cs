using NodeX.Core.Settings;

namespace NodeX.Core.Tests;

public class CarBrowseTests
{
    private static NodeSettings Settings(params string[] favorites) => new()
    {
        FavoriteNodes = [.. favorites],
        FavoriteLabels = new() { ["27339"] = "East Coast Reflector", ["2000"] = " " }
    };

    [Fact]
    public void Items_use_label_or_node_number()
    {
        var items = CarBrowse.BuildItems(Settings("27339", "2000"));
        Assert.Equal(["East Coast Reflector", "2000"], items.Select(i => i.Title));
        Assert.All(items, i => Assert.True(i.Playable));
        Assert.Equal(["27339", "2000"], items.Select(i => i.MediaId));
    }

    [Fact]
    public void No_favorites_gives_one_non_playable_row()
    {
        var items = CarBrowse.BuildItems(Settings());
        var only = Assert.Single(items);
        Assert.False(only.Playable);
        Assert.Equal(CarBrowse.NoFavoritesId, only.MediaId);
    }

    [Fact]
    public void Neighbor_wraps_both_ways_and_handles_unknown_current()
    {
        string[] favs = ["1111", "2222", "3333"];
        Assert.Equal("2222", CarBrowse.Neighbor(favs, "1111", +1));
        Assert.Equal("1111", CarBrowse.Neighbor(favs, "3333", +1));
        Assert.Equal("3333", CarBrowse.Neighbor(favs, "1111", -1));
        Assert.Equal("1111", CarBrowse.Neighbor(favs, null, +1));
        Assert.Equal("3333", CarBrowse.Neighbor(favs, "9999", -1));
        Assert.Null(CarBrowse.Neighbor([], "1111", +1));
    }

    [Fact]
    public void Search_matches_number_label_and_rejects_ambiguity()
    {
        var s = Settings("27339", "2000", "5555");
        Assert.Equal("27339", CarBrowse.FindFavorite(s, "27339"));
        Assert.Equal("27339", CarBrowse.FindFavorite(s, "east coast reflector"));
        Assert.Equal("27339", CarBrowse.FindFavorite(s, "reflector"));
        Assert.Null(CarBrowse.FindFavorite(s, "nonsense"));
        Assert.Null(CarBrowse.FindFavorite(s, ""));
        Assert.Null(CarBrowse.FindFavorite(s, "99999"));
    }

    [Fact]
    public void Selecting_replaces_only_the_cars_previous_node()
    {
        Assert.Equal("1111", CarBrowse.NodeToReplace("1111", "2222"));
        Assert.Null(CarBrowse.NodeToReplace("1111", "1111"));
        Assert.Null(CarBrowse.NodeToReplace(null, "2222"));
        Assert.Null(CarBrowse.NodeToReplace("1111", "2222", selectedAlreadyConnected: true));
    }

    [Fact]
    public void Root_items_expose_active_links_and_favorites_tabs()
    {
        var root = CarBrowse.BuildRootItems(activeCount: 2, favoritesCount: 5);
        Assert.Equal(2, root.Count);
        Assert.Equal(CarBrowse.ActiveFolderId, root[0].MediaId);
        Assert.False(root[0].Playable);
        Assert.Equal("2 connected", root[0].Subtitle);

        Assert.Equal(CarBrowse.FavoritesFolderId, root[1].MediaId);
        Assert.False(root[1].Playable);
        Assert.Equal("5 saved", root[1].Subtitle);
    }

    [Fact]
    public void Favorites_items_show_connection_badges()
    {
        var s = Settings("27339", "2000", "5555");
        var items = CarBrowse.BuildFavoritesItems(s, connectedNodes: ["27339", "2000"], transmitNode: "27339");
        Assert.Equal(3, items.Count);

        Assert.Equal("Node 27339 • Connected (Transmit)", items[0].Subtitle);
        Assert.Equal("Node 2000 • Connected (Listen)", items[1].Subtitle);
        Assert.Equal("Node 5555", items[2].Subtitle);
    }

    [Fact]
    public void Active_items_show_nodes_disconnect_rows_and_disconnect_all()
    {
        var s = Settings("27339", "2000");
        var conn1 = new NodeConnection("27339");
        var conn2 = new NodeConnection("2000");

        var items = CarBrowse.BuildActiveItems(s, [conn1, conn2], transmitNode: "27339", talkingNodes: new HashSet<string> { "27339" });
        // Expected:
        // 1. Node 27339 item
        // 2. Disconnect 27339 item
        // 3. Node 2000 item
        // 4. Disconnect 2000 item
        // 5. Disconnect all nodes item
        Assert.Equal(5, items.Count);

        Assert.Equal("27339", items[0].MediaId);
        Assert.Equal("East Coast Reflector", items[0].Title);
        Assert.Equal("Transmit • Connecting…", items[0].Subtitle);

        Assert.Equal(CarBrowse.DisconnectPrefix + "27339", items[1].MediaId);
        Assert.Equal("Disconnect 27339", items[1].Title);

        Assert.Equal("2000", items[2].MediaId);
        Assert.Equal("2000", items[2].Title);
        Assert.Equal("Listen-only • Connecting…", items[2].Subtitle);

        Assert.Equal(CarBrowse.DisconnectPrefix + "2000", items[3].MediaId);
        Assert.Equal("Disconnect 2000", items[3].Title);

        Assert.Equal(CarBrowse.DisconnectAllId, items[4].MediaId);
        Assert.Equal("Disconnect all nodes", items[4].Title);
    }

    [Fact]
    public void Active_items_empty_gives_non_playable_notice()
    {
        var s = Settings("27339");
        var items = CarBrowse.BuildActiveItems(s, [], transmitNode: null);
        var only = Assert.Single(items);
        Assert.False(only.Playable);
        Assert.Equal(CarBrowse.NoActiveId, only.MediaId);
    }
}
