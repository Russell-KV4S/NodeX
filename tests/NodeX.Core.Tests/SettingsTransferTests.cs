using NodeX.Core.Settings;
using Xunit;

namespace NodeX.Core.Tests;

public class SettingsTransferTests
{
    private static NodeSettings Sample() => new()
    {
        NodeNumber = "523802",
        Callsign = "KV4S",
        FavoriteNodes = ["27339", "2000"],
        FavoriteLabels = new() { ["27339"] = "East Coast Reflector" },
        MicGain = 2.5f,
        TogglePtt = true,
        InputDeviceId = "mic-on-this-phone",
        OutputDeviceId = "speaker-on-this-phone"
    };

    [Fact]
    public void RoundTrip_KeepsSettings_AndDropsDeviceIds()
    {
        string text = SettingsTransfer.Export(Sample());
        var result = SettingsTransfer.TryImport(text);

        Assert.True(result.Success);
        Assert.Null(result.Password);
        var s = result.Settings!;
        Assert.Equal("523802", s.NodeNumber);
        Assert.Equal("KV4S", s.Callsign);
        Assert.Equal(["27339", "2000"], s.FavoriteNodes);
        Assert.Equal("East Coast Reflector", s.FavoriteLabels["27339"]);
        Assert.Equal(2.5f, s.MicGain);
        Assert.True(s.TogglePtt);
        Assert.Null(s.InputDeviceId);
        Assert.Null(s.OutputDeviceId);
    }

    [Fact]
    public void Export_WithoutPassphrase_NeverContainsPassword()
    {
        string text = SettingsTransfer.Export(Sample(), password: "hunter2", passphrase: null);
        Assert.DoesNotContain("hunter2", text);
        Assert.Null(SettingsTransfer.TryImport(text).Password);
    }

    [Fact]
    public void Password_RoundTripsWithPassphrase_AndIsNotPlainInFile()
    {
        string text = SettingsTransfer.Export(Sample(), "hunter2", "correct horse");
        Assert.DoesNotContain("hunter2", text);

        var result = SettingsTransfer.TryImport(text, "correct horse");
        Assert.True(result.Success);
        Assert.Equal("hunter2", result.Password);
    }

    [Fact]
    public void EncryptedFile_WithoutOrWrongPassphrase_AsksForIt()
    {
        string text = SettingsTransfer.Export(Sample(), "hunter2", "correct horse");

        var none = SettingsTransfer.TryImport(text);
        Assert.False(none.Success);
        Assert.True(none.NeedsPassphrase);

        var wrong = SettingsTransfer.TryImport(text, "battery staple");
        Assert.False(wrong.Success);
        Assert.True(wrong.NeedsPassphrase);
    }

    [Fact]
    public void SamePassword_EncryptsDifferentlyEachTime()
    {
        string a = SettingsTransfer.Export(Sample(), "hunter2", "pp");
        string b = SettingsTransfer.Export(Sample(), "hunter2", "pp");
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("{\"format\":\"something-else\",\"version\":1,\"settings\":{}}")]
    [InlineData("[1,2,3]")]
    public void Garbage_FailsCleanly_WithoutThrowing(string? text)
    {
        var result = SettingsTransfer.TryImport(text);
        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }

    [Fact]
    public void NewerVersion_IsRejectedWithUpdateHint()
    {
        string text = SettingsTransfer.Export(Sample()).Replace("\"version\": 1", "\"version\": 99");
        var result = SettingsTransfer.TryImport(text);
        Assert.False(result.Success);
        Assert.Contains("newer version", result.Error);
    }

    [Fact]
    public void HostileKdfIterations_AreRejected_NotExecuted()
    {
        string text = SettingsTransfer.Export(Sample(), "hunter2", "pp").Replace("\"iterations\": 200000", "\"iterations\": 2000000000");
        var result = SettingsTransfer.TryImport(text, "pp");
        Assert.False(result.Success);
    }

    [Fact]
    public void OversizedInput_IsRejected()
    {
        var result = SettingsTransfer.TryImport(new string('x', 300_000));
        Assert.False(result.Success);
    }

    [Fact]
    public void Import_Normalises_AndLeavesRangeChecksToSave()
    {
        string text = SettingsTransfer.Export(Sample() with { Callsign = "  kv4s ", TimeOutTimerSeconds = 99999 });
        var s = SettingsTransfer.TryImport(text).Settings!;
        Assert.Equal("KV4S", s.Callsign);
        // Import only fills the form; Save re-validates, so a bad value in a hand-edited file can't be stored.
        Assert.NotEmpty(NodeSettingsValidator.Validate(s, "pw"));
    }
}
