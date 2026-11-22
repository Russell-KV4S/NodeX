using NodeX.Core;
using NodeX.Core.Settings;
using NodeX.UI.ViewModels;

namespace NodeX.Core.Tests;

public class SettingsPreservationTests
{
    [Fact]
    public async Task SavingTheSettingsScreen_KeepsSettingsItDoesNotShow()
    {
        // 2026-11-21 review: the form built a fresh NodeSettings, so an HTTPS-only setup (IaxRegistration = false,
        // not on the form) silently switched back to IAX2 registration on any Save.
        string path = Path.Combine(Path.GetTempPath(), "nodex-keep-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var config = new NodeConfigService(new JsonFileSettingsStore(path), new InMemorySecretStore());
            await config.SaveAsync(new NodeSettings { NodeNumber = "55553", IaxRegistration = false }, "pw");

            var vm = new SettingsViewModel(config);
            await vm.LoadAsync();
            await vm.SaveCommand.ExecuteAsync(null);
            Assert.False(vm.HasError);

            var reloaded = await config.LoadAsync();
            Assert.False(reloaded.Settings.IaxRegistration);
        }
        finally
        {
            foreach (string f in new[] { path, path + ".tmp" })
                try { File.Delete(f); } catch (IOException) { }
        }
    }

    private sealed class FailingStore(ISettingsStore inner) : ISettingsStore
    {
        public bool Fail;
        public Task<NodeSettings> LoadAsync(CancellationToken ct = default) => inner.LoadAsync(ct);
        public Task SaveAsync(NodeSettings settings, CancellationToken ct = default) =>
            Fail ? throw new IOException("disk full") : inner.SaveAsync(settings, ct);
    }

    [Fact]
    public async Task AFailedSettingsWrite_PutsTheOldPasswordBack()
    {
        // 2026-11-21 review: the password was written first; if the settings file then failed, a reload paired the
        // old node number with the new account's password.
        string path = Path.Combine(Path.GetTempPath(), "nodex-pair-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new FailingStore(new JsonFileSettingsStore(path));
            var config = new NodeConfigService(store, new InMemorySecretStore());
            await config.SaveAsync(new NodeSettings { NodeNumber = "55553" }, "old-pw");

            store.Fail = true;
            await Assert.ThrowsAsync<IOException>(() => config.SaveAsync(new NodeSettings { NodeNumber = "66664" }, "new-pw"));

            var reloaded = await config.LoadAsync();
            Assert.Equal("55553", reloaded.Settings.NodeNumber);
            Assert.Equal("old-pw", reloaded.Password);
        }
        finally
        {
            foreach (string f in new[] { path, path + ".tmp" })
                try { File.Delete(f); } catch (IOException) { }
        }
    }
}
