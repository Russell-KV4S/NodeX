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
    public void Active_items_are_one_card_row_per_node_plus_disconnect_all()
    {
        var s = Settings("27339", "2000");
        var conn1 = new NodeConnection("27339");
        var conn2 = new NodeConnection("2000");

        var items = CarBrowse.BuildActiveItems(s, [conn1, conn2], transmitNode: "27339", talkingNodes: new HashSet<string> { "27339" });
        Assert.Equal(3, items.Count);

        Assert.Equal("27339", items[0].MediaId);
        Assert.Equal("27339 · East Coast Reflector", items[0].Title);
        Assert.Equal("TRANSMIT HERE · Connecting…", items[0].Subtitle);
        Assert.Equal(CarIcon.Transmit, items[0].Icon);

        Assert.Equal("2000", items[1].MediaId);
        Assert.Equal("2000", items[1].Title);
        Assert.Equal("Listen only · Connecting…", items[1].Subtitle);
        Assert.Equal(CarIcon.Listen, items[1].Icon);

        Assert.Equal(CarBrowse.DisconnectAllId, items[2].MediaId);
        Assert.Equal("Disconnect all nodes", items[2].Title);

        // No row hangs a single node up: that is a button on the now-playing screen.
        Assert.DoesNotContain(items, i => i.MediaId.StartsWith(CarBrowse.DisconnectPrefix) && i.MediaId != CarBrowse.DisconnectAllId);
    }

    [Fact]
    public void A_single_active_link_has_no_disconnect_all_row()
    {
        var items = CarBrowse.BuildActiveItems(Settings("2000"), [new NodeConnection("2000")], transmitNode: null);
        var only = Assert.Single(items);
        Assert.Equal("2000", only.MediaId);
        Assert.Equal(CarIcon.Listen, only.Icon);
    }

    [Theory]
    [InlineData(true, true, false, false, false, "TRANSMIT HERE · Connected")]
    [InlineData(true, true, false, true, false, "TRANSMIT HERE · Connected · Hearing now")]
    [InlineData(false, true, false, false, true, "Listen only · Connected · Muted")]
    [InlineData(false, false, true, true, false, "Listen only · Reconnecting…")]   // not "hearing" while down
    [InlineData(false, false, false, false, false, "Listen only · Connecting…")]
    public void Status_line_reads_like_the_phone_card(bool tx, bool connected, bool reconnecting, bool hearing, bool muted, string expected)
    {
        Assert.Equal(expected, CarBrowse.StatusLine(tx, connected, reconnecting, hearing, muted));
    }

    [Fact]
    public void Card_title_is_number_and_label_or_just_the_number()
    {
        var s = Settings("27339", "2000");
        Assert.Equal("27339 · East Coast Reflector", CarBrowse.CardTitle(s, "27339"));
        Assert.Equal("2000", CarBrowse.CardTitle(s, "2000"));
    }

    [Fact]
    public void Favorites_icons_show_connected_role()
    {
        var items = CarBrowse.BuildFavoritesItems(Settings("27339", "2000", "5555"), connectedNodes: ["27339", "2000"], transmitNode: "27339");
        Assert.Equal([CarIcon.Transmit, CarIcon.Listen, CarIcon.Favorite], items.Select(i => i.Icon));
    }

    [Fact]
    public void Wheel_next_and_back_step_through_connected_nodes_only()
    {
        // The service passes the connected nodes (not the favourites), so nothing is ever dialled from the wheel.
        string[] connected = ["523801", "53431", "43136"];
        Assert.Equal("53431", CarBrowse.Neighbor(connected, "523801", +1));
        Assert.Equal("43136", CarBrowse.Neighbor(connected, "523801", -1));
        Assert.Equal("2000", CarBrowse.Neighbor(["2000"], "2000", +1));   // one link: stays where it is
        Assert.Null(CarBrowse.Neighbor([], null, +1));                     // nothing linked: nothing happens
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
