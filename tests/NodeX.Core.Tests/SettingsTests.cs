using NodeX.Core.Settings;

namespace NodeX.Core.Tests;

public class NodeSettingsValidatorTests
{
    private static NodeSettings Good => new() { NodeNumber = "55553", Callsign = "w1aw" };

    [Fact]
    public void ValidConfiguration_HasNoIssues() =>
        Assert.Empty(NodeSettingsValidator.Validate(Good, "pw"));

    [Fact]
    public void Defaults_AreValidExceptNodeAndPassword()
    {
        var issues = NodeSettingsValidator.Validate(new NodeSettings(), null);
        Assert.Equal([NodeSettingsValidator.Fields.NodeNumber, NodeSettingsValidator.Fields.Password],
            issues.Select(i => i.Field).ToArray());
    }

    [Theory]
    [InlineData("12", false)]
    [InlineData("123", true)]
    [InlineData("1234567", true)]
    [InlineData("12345678", false)]
    [InlineData("12a45", false)]
    [InlineData("-5555", false)]
    [InlineData(" 55553 ", true)]   // surrounding spaces are trimmed
    public void NodeNumber_Rules(string value, bool valid)
    {
        var issues = NodeSettingsValidator.Validate(Good with { NodeNumber = value }, "pw");
        Assert.Equal(valid, issues.All(i => i.Field != NodeSettingsValidator.Fields.NodeNumber));
    }

    [Fact]
    public void Password_Required_AndLengthLimited()
    {
        Assert.Contains(NodeSettingsValidator.Validate(Good, ""), i => i.Field == NodeSettingsValidator.Fields.Password);
        Assert.Contains(NodeSettingsValidator.Validate(Good, null), i => i.Field == NodeSettingsValidator.Fields.Password);
        Assert.Empty(NodeSettingsValidator.Validate(Good, new string('x', NodeSettingsValidator.MaxPasswordLength)));
        Assert.Contains(NodeSettingsValidator.Validate(Good, new string('x', NodeSettingsValidator.MaxPasswordLength + 1)),
            i => i.Field == NodeSettingsValidator.Fields.Password);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("W1AW", true)]
    [InlineData("w1aw/p", true)]
    [InlineData("W1 AW", false)]
    [InlineData("W1AW/P/Q", false)]
    [InlineData("W1AW!", false)]
    public void Callsign_Rules(string value, bool valid)
    {
        var issues = NodeSettingsValidator.Validate(Good with { Callsign = value }, "pw");
        Assert.Equal(valid, issues.All(i => i.Field != NodeSettingsValidator.Fields.Callsign));
    }

    [Fact]
    public void Favourites_MustAllBeNodeNumbers()
    {
        var s = Good with { FavoriteNodes = ["1999", "abc", "27339"] };
        var issue = Assert.Single(NodeSettingsValidator.Validate(s, "pw"));
        Assert.Contains("abc", issue.Message);
    }

    [Theory]
    [InlineData("http://register.allstarlink.org/")]
    [InlineData("register.allstarlink.org")]
    [InlineData("")]
    public void RegistrationUrl_MustBeHttps(string url) =>
        Assert.Contains(NodeSettingsValidator.Validate(Good with { RegistrationUrl = url }, "pw"),
            i => i.Field == NodeSettingsValidator.Fields.RegistrationUrl);

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(65535, true)]
    [InlineData(65536, false)]
    public void IaxPort_Range(int port, bool valid) =>
        Assert.Equal(valid, NodeSettingsValidator.Validate(Good with { IaxPort = port }, "pw").Count == 0);

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(600, true)]
    [InlineData(601, false)]
    public void TimeOutTimer_Range(int seconds, bool valid) =>
        Assert.Equal(valid, NodeSettingsValidator.Validate(Good with { TimeOutTimerSeconds = seconds }, "pw").Count == 0);

    [Theory]
    [InlineData(0.05f, false)]
    [InlineData(1.0f, true)]
    [InlineData(9f, false)]
    [InlineData(float.NaN, false)]
    public void MicGain_Range(float gain, bool valid) =>
        Assert.Equal(valid, NodeSettingsValidator.Validate(Good with { MicGain = gain }, "pw").Count == 0);

    [Fact]
    public void Normalize_TrimsUppercasesAndDedupes()
    {
        var n = NodeSettingsValidator.Normalize(new NodeSettings
        {
            NodeNumber = " 55553 ",
            Callsign = " w1aw ",
            FavoriteNodes = ["1999", " 1999 ", "", "27339"]
        });
        Assert.Equal("55553", n.NodeNumber);
        Assert.Equal("W1AW", n.Callsign);
        Assert.Equal(["1999", "27339"], n.FavoriteNodes);
    }
}

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nodex-tests-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "nested", "settings.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task MissingFile_ReturnsDefaults()
    {
        var s = await new JsonFileSettingsStore(FilePath).LoadAsync();
        Assert.Equal(new NodeSettings().NodeNumber, s.NodeNumber);
        Assert.Equal(180, s.TimeOutTimerSeconds);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsEveryField_AndCreatesDirectories()
    {
        var store = new JsonFileSettingsStore(FilePath);
        var original = new NodeSettings
        {
            NodeNumber = "55553", Callsign = "W1AW", DefaultTargetNode = "27339",
            FavoriteNodes = ["1999", "27339"], IaxPort = 4570, RegisterOnStartup = false,
            InputDeviceId = "mic-1", OutputDeviceId = "spk-2", MicGain = 2.5f,
            TimeOutTimerSeconds = 90, TogglePtt = true
        };

        await store.SaveAsync(original);
        var loaded = await store.LoadAsync();

        Assert.Equivalent(original, loaded);
        Assert.False(File.Exists(FilePath + ".tmp"));   // atomic write leaves no temp file behind
    }

    [Fact]
    public async Task SecondSave_ReplacesFirst()
    {
        var store = new JsonFileSettingsStore(FilePath);
        await store.SaveAsync(new NodeSettings { NodeNumber = "111" });
        await store.SaveAsync(new NodeSettings { NodeNumber = "222" });
        Assert.Equal("222", (await store.LoadAsync()).NodeNumber);
    }

    [Fact]
    public async Task CorruptFile_ReturnsDefaults_AndKeepsTheBadFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await File.WriteAllTextAsync(FilePath, "{ this is not json");

        var s = await new JsonFileSettingsStore(FilePath).LoadAsync();

        Assert.Equal("", s.NodeNumber);
        Assert.True(File.Exists(FilePath + ".corrupt"));
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void DefaultPath_IsUnderPerUserAppData() =>
        Assert.EndsWith(Path.Combine("NodeX", "settings.json"), JsonFileSettingsStore.DefaultPath);

    [Fact]
    public async Task InMemorySecretStore_SetGetDelete()
    {
        var secrets = new InMemorySecretStore();
        Assert.Null(await secrets.GetAsync("k"));
        await secrets.SetAsync("k", "v");
        Assert.Equal("v", await secrets.GetAsync("k"));
        await secrets.SetAsync("k", "v2");
        Assert.Equal("v2", await secrets.GetAsync("k"));
        await secrets.DeleteAsync("k");
        Assert.Null(await secrets.GetAsync("k"));
        await secrets.DeleteAsync("k"); // deleting a missing key is fine
    }
}

public class NodeConfigServiceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "nodex-cfg-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly InMemorySecretStore _secrets = new();

    public void Dispose()
    {
        foreach (string f in new[] { _path, _path + ".tmp", _path + ".corrupt" })
            try { File.Delete(f); } catch (IOException) { }
    }

    private NodeConfigService Create() => new(new JsonFileSettingsStore(_path), _secrets);

    [Fact]
    public async Task FreshInstall_LoadsIncompleteConfig()
    {
        var cfg = await Create().LoadAsync();
        Assert.False(cfg.IsComplete);
        Assert.Null(cfg.Password);
    }

    [Fact]
    public async Task Save_PersistsSettingsAndPassword_SeparatelyAndSecurely()
    {
        var svc = Create();
        var result = await svc.SaveAsync(new NodeSettings { NodeNumber = " 55553 ", Callsign = "w1aw" }, "Sup3rSecret!");

        Assert.True(result.Success);
        string json = await File.ReadAllTextAsync(_path);
        Assert.DoesNotContain("Sup3rSecret!", json);                       // never in the settings file
        Assert.Equal("Sup3rSecret!", await _secrets.GetAsync(SecretKeys.NodePassword));

        var loaded = await Create().LoadAsync();
        Assert.True(loaded.IsComplete);
        Assert.Equal("55553", loaded.Settings.NodeNumber);                 // normalised
        Assert.Equal("W1AW", loaded.Settings.Callsign);
        Assert.Equal("Sup3rSecret!", loaded.Password);
    }

    [Fact]
    public async Task InvalidSave_WritesNothing_AndReportsIssues()
    {
        var svc = Create();
        var result = await svc.SaveAsync(new NodeSettings { NodeNumber = "x" }, "");

        Assert.False(result.Success);
        Assert.Equal(2, result.Issues.Count);
        Assert.False(File.Exists(_path));
        Assert.Null(await _secrets.GetAsync(SecretKeys.NodePassword));
    }

    [Fact]
    public async Task Save_RaisesChanged_WithPasswordForRuntimeUse()
    {
        var svc = Create();
        NodeConfig? seen = null;
        svc.Changed += c => seen = c;

        await svc.SaveAsync(new NodeSettings { NodeNumber = "55553" }, "pw");

        Assert.NotNull(seen);
        Assert.Equal("pw", seen!.Password);
        Assert.Equal("55553", seen.ToRegistrationConfig().NodeNumber);
        Assert.Equal(4569, seen.ToRegistrationConfig().Port);
    }

    [Fact]
    public async Task ClearPassword_RemovesOnlyTheSecret()
    {
        var svc = Create();
        await svc.SaveAsync(new NodeSettings { NodeNumber = "55553" }, "pw");

        await svc.ClearPasswordAsync();

        var cfg = await svc.LoadAsync();
        Assert.Equal("55553", cfg.Settings.NodeNumber);
        Assert.Null(cfg.Password);
        Assert.False(cfg.IsComplete);
    }

    private sealed class FailingSecretStore : ISecretStore
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SetAsync(string key, string value, CancellationToken ct = default) => throw new IOException("keystore locked");
        public Task DeleteAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task SecretStoreFailure_DoesNotWriteSettings()
    {
        var svc = new NodeConfigService(new JsonFileSettingsStore(_path), new FailingSecretStore());
        await Assert.ThrowsAsync<IOException>(() => svc.SaveAsync(new NodeSettings { NodeNumber = "55553" }, "pw"));
        Assert.False(File.Exists(_path));
    }
}
