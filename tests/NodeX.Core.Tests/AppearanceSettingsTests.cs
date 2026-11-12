using System.Text.Json;
using NodeX.Core.Settings;

namespace NodeX.Core.Tests;

public class AppearanceSettingsTests
{
    private static NodeSettings Valid => new() { NodeNumber = "55553", Callsign = "W1AW" };

    [Theory]
    [InlineData("Dark", 1)]
    [InlineData("Light", 1.3)]
    [InlineData("HighContrast", 1.5)]
    public void Appearance_RoundTripsInSettingsAndTransfer(string theme, double scale)
    {
        var settings = Valid with { Theme = theme, TextScale = scale };
        Assert.Empty(NodeSettingsValidator.Validate(settings, "pw"));
        Assert.Equal(settings.Theme, JsonSerializer.Deserialize<NodeSettings>(JsonSerializer.Serialize(settings))!.Theme);
        var imported = SettingsTransfer.TryImport(SettingsTransfer.Export(settings, "pw", null), null);
        Assert.True(imported.Success);
        Assert.Equal(theme, imported.Settings!.Theme);
        Assert.Equal(scale, imported.Settings.TextScale);
    }

    [Theory]
    [InlineData("Unknown", 1)]
    [InlineData("Light", 0.9)]
    [InlineData("Dark", 1.6)]
    [InlineData("Dark", double.NaN)]
    public void InvalidAppearance_IsRejected(string theme, double scale) =>
        Assert.NotEmpty(NodeSettingsValidator.Validate(Valid with { Theme = theme, TextScale = scale }, "pw"));

    [Fact]
    public void ExistingSettings_KeepDarkAndNormalTextSize()
    {
        var settings = JsonSerializer.Deserialize<NodeSettings>("""{"NodeNumber":"55553"}""")!;
        Assert.Equal("Dark", settings.Theme);
        Assert.Equal(1, settings.TextScale);
    }
}
