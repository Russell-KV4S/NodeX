using System.Net;
using System.Text;
using NodeX.Core.Settings;
using NodeX.UI.ViewModels;

namespace NodeX.Core.Tests;

public class SettingsViewModelTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "nodex-vm-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly InMemorySecretStore _secrets = new();
    private readonly NodeConfigService _config;

    public SettingsViewModelTests() => _config = new NodeConfigService(new JsonFileSettingsStore(_path), _secrets);

    public void Dispose()
    {
        foreach (string f in new[] { _path, _path + ".tmp" })
            try { File.Delete(f); } catch (IOException) { }
    }

    private sealed class StubHandler(HttpResponseMessage reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(reply);
    }

    private const string OkReply = """{"ipaddr":"203.0.113.7","port":4569,"refresh":60,"data":"successfully registered"}""";

    private SettingsViewModel Create(string? registryReply = null) =>
        new(_config, cfg => new AslRegistrationService(cfg, new HttpClient(new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(registryReply ?? OkReply, Encoding.UTF8, "application/json")
        }))));

    private static void Fill(SettingsViewModel vm)
    {
        vm.NodeNumber = " 55553 ";
        vm.Password = "Sup3rSecret";
        vm.Callsign = "w1aw";
    }

    [Fact]
    public async Task Load_FillsTheFormFromSavedConfiguration()
    {
        await _config.SaveAsync(new NodeSettings
        {
            NodeNumber = "55553", Callsign = "W1AW", DefaultTargetNode = "27339",
            FavoriteNodes = ["1999", "2000"], TogglePtt = true, TimeOutTimerSeconds = 90, MicGain = 2f, IaxPort = 4570
        }, "pw123");

        var vm = Create();
        await vm.LoadAsync();

        Assert.Equal("55553", vm.NodeNumber);
        Assert.Equal("pw123", vm.Password);
        Assert.Equal("W1AW", vm.Callsign);
        Assert.Equal("27339", vm.DefaultTargetNode);
        Assert.Equal("1999, 2000", vm.FavoritesText);
        Assert.True(vm.TogglePtt);
        Assert.Equal("90", vm.TimeOutSecondsText);
        Assert.Equal(2.0, vm.MicGain);
        Assert.Equal("4570", vm.IaxPortText);
        Assert.False(vm.ShowPassword);
    }

    [Fact]
    public async Task Save_WithEmptyForm_ShowsBothProblems_AndSavesNothing()
    {
        var vm = Create();
        bool saved = false;
        vm.Saved += () => saved = true;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.Contains("node number", vm.ErrorText);
        Assert.Contains("password", vm.ErrorText);
        Assert.False(saved);
        Assert.False(File.Exists(_path));
        Assert.Null(await _secrets.GetAsync(SecretKeys.NodePassword));
    }

    [Fact]
    public async Task Save_WithValidForm_PersistsSettingsAndPassword_AndRaisesSaved()
    {
        var vm = Create();
        Fill(vm);
        vm.FavoritesText = "1999, 27339  2000;2000";
        bool saved = false;
        vm.Saved += () => saved = true;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(saved);
        Assert.False(vm.HasError);
        Assert.Equal("Saved.", vm.StatusText);

        var cfg = await _config.LoadAsync();
        Assert.True(cfg.IsComplete);
        Assert.Equal("55553", cfg.Settings.NodeNumber);          // trimmed
        Assert.Equal("W1AW", cfg.Settings.Callsign);              // upper-cased
        Assert.Equal(["1999", "27339", "2000"], cfg.Settings.FavoriteNodes);
        Assert.Equal("Sup3rSecret", cfg.Password);
        Assert.DoesNotContain("Sup3rSecret", await File.ReadAllTextAsync(_path));
    }

    [Theory]
    [InlineData("abc", "Time-out timer must be a whole number")]
    [InlineData("5", "between 10 and 600")]
    public async Task Save_RejectsBadTimeOut_WithOneClearMessage(string value, string expected)
    {
        var vm = Create();
        Fill(vm);
        vm.TimeOutSecondsText = value;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Contains(expected, vm.ErrorText);
        Assert.Single(vm.ErrorText.Split(Environment.NewLine));   // no duplicate "out of range" note
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public async Task Save_RejectsBadPort()
    {
        var vm = Create();
        Fill(vm);
        vm.IaxPortText = "70000";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains("between 1 and 65535", vm.ErrorText);

        vm.IaxPortText = "forty";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains("whole number", vm.ErrorText);
    }

    [Fact]
    public async Task Cancel_RaisesCancelled_AndSavesNothing()
    {
        var vm = Create();
        Fill(vm);
        bool cancelled = false;
        vm.Cancelled += () => cancelled = true;

        vm.CancelCommand.Execute(null);

        Assert.True(cancelled);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public async Task TestRegistration_Success_ShowsPublicAddress_WithoutSaving()
    {
        var vm = Create();
        Fill(vm);

        await vm.TestRegistrationCommand.ExecuteAsync(null);

        Assert.Contains("203.0.113.7", vm.StatusText);
        Assert.False(vm.HasError);
        Assert.False(vm.IsBusy);
        Assert.False(File.Exists(_path));                       // testing never persists anything
        Assert.Null(await _secrets.GetAsync(SecretKeys.NodePassword));
    }

    [Fact]
    public async Task TestRegistration_Rejected_ShowsServerMessage_NotThePassword()
    {
        var vm = Create("""{"data":"Invalid password for node 55553"}""");
        Fill(vm);

        await vm.TestRegistrationCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.Contains("Invalid password", vm.ErrorText);
        Assert.DoesNotContain("Sup3rSecret", vm.ErrorText);
        Assert.False(vm.HasStatus);
    }

    [Fact]
    public async Task TestRegistration_WithIncompleteForm_ExplainsWhatIsMissing()
    {
        var vm = Create();
        await vm.TestRegistrationCommand.ExecuteAsync(null);
        Assert.Contains("node number", vm.ErrorText);
    }
}
