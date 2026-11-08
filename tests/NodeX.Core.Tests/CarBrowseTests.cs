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
    }
}
