using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NodeX.Core.Updates;
using NodeX.UI.ViewModels;

namespace NodeX.Core.Tests;

public class GitHubReleaseUpdaterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nodex-update-test-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Apk = Encoding.UTF8.GetBytes("test apk payload");
    private static readonly string Hash = Convert.ToHexString(SHA256.HashData(Apk));
    private const string Prefix = "https://github.com/Russell-KV4S/NodeX/releases/download/v1.16/";

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public readonly List<string> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            return respond(request, ct);
        }
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };

    private static object Feed(bool prerelease = false, string apkUrl = Prefix + "NodeX-android.apk") => new
    {
        tag_name = "v1.16", draft = false, prerelease,
        assets = new[]
        {
            new { name = GitHubReleaseUpdater.ManifestName, browser_download_url = Prefix + GitHubReleaseUpdater.ManifestName, size = 200L },
            new { name = GitHubReleaseUpdater.ApkName, browser_download_url = apkUrl, size = (long)Apk.Length }
        }
    };

    private static object Manifest(long code = 18, string? hash = null, string package = "com.kv4s.nodex", string version = "1.16") => new
    {
        schemaVersion = 1, version,
        android = new { packageId = package, versionCode = code, assetName = GitHubReleaseUpdater.ApkName, size = Apk.Length, sha256 = hash ?? Hash }
    };

    private static Handler ValidHandler(object? feed = null, object? manifest = null, byte[]? download = null) => new((request, _) =>
        Task.FromResult(request.RequestUri!.Host == "api.github.com" ? Json(feed ?? Feed())
            : request.RequestUri.AbsolutePath.EndsWith(GitHubReleaseUpdater.ManifestName) ? Json(manifest ?? Manifest())
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(download ?? Apk) }));

    [Theory]
    [InlineData(17, true)]
    [InlineData(18, false)]
    [InlineData(19, false)]
    public async Task OffersOnlyIncreasedAndroidVersionCodes(long installed, bool expected)
    {
        using var client = new HttpClient(ValidHandler());
        var release = await new GitHubReleaseUpdater(client).CheckAsync(installed);
        Assert.Equal(expected, release != null);
    }

    [Fact]
    public async Task PrivateOrMissingFeed_IsExplicitlyUnavailable()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        await Assert.ThrowsAsync<UpdateFeedUnavailableException>(() => new GitHubReleaseUpdater(client).CheckAsync(17));
    }

    [Fact]
    public async Task Prerelease_IsIgnoredWithoutFetchingAssets()
    {
        var handler = ValidHandler(feed: Feed(prerelease: true));
        using var client = new HttpClient(handler);
        Assert.Null(await new GitHubReleaseUpdater(client).CheckAsync(17));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AssetOutsideRepository_IsRejectedBeforeDownload()
    {
        var handler = ValidHandler(feed: Feed(apkUrl: "https://example.com/app.apk"));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new GitHubReleaseUpdater(client).CheckAsync(17));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("bad hash", "com.kv4s.nodex", "1.16")]
    [InlineData(null, "com.other.app", "1.16")]
    [InlineData(null, "com.kv4s.nodex", "1.15")]
    public async Task InvalidManifest_IsRejected(string? hash, string package, string version)
    {
        using var client = new HttpClient(ValidHandler(manifest: Manifest(hash: hash, package: package, version: version)));
        await Assert.ThrowsAsync<InvalidDataException>(() => new GitHubReleaseUpdater(client).CheckAsync(17));
    }

    [Fact]
    public async Task VerifiedDownload_ReturnsExactBytesAndNoPartialFile()
    {
        using var client = new HttpClient(ValidHandler());
        var updater = new GitHubReleaseUpdater(client);
        var release = await updater.CheckAsync(17);
        string path = await updater.DownloadAsync(release!, _directory);
        Assert.Equal(Apk, await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task CorruptSameSizeDownload_IsRejectedAndRemoved()
    {
        using var client = new HttpClient(ValidHandler(download: new byte[Apk.Length]));
        var updater = new GitHubReleaseUpdater(client);
        var release = await updater.CheckAsync(17);
        await Assert.ThrowsAsync<InvalidDataException>(() => updater.DownloadAsync(release!, _directory));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task WrongSizeDownload_DoesNotReplacePreviousVerifiedFile()
    {
        Directory.CreateDirectory(_directory);
        string previous = Path.Combine(_directory, GitHubReleaseUpdater.ApkName);
        await File.WriteAllTextAsync(previous, "previous verified file");
        using var client = new HttpClient(ValidHandler(download: [1, 2, 3]));
        var updater = new GitHubReleaseUpdater(client);
        var release = await updater.CheckAsync(17);
        await Assert.ThrowsAsync<InvalidDataException>(() => updater.DownloadAsync(release!, _directory));
        Assert.Equal("previous verified file", await File.ReadAllTextAsync(previous));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task CancelledDownload_LeavesNoInstallableFile()
    {
        using var client = new HttpClient(ValidHandler());
        var updater = new GitHubReleaseUpdater(client);
        var release = await updater.CheckAsync(17);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => updater.DownloadAsync(release!, _directory, ct: cancelled.Token));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task AutomaticCheck_NeverDownloadsOrInstalls_AndIsThrottled()
    {
        var handler = ValidHandler();
        using var client = new HttpClient(handler);
        int installs = 0;
        var vm = new AppUpdateViewModel(new GitHubReleaseUpdater(client), 17, _directory,
            (_, _) => { installs++; return Task.FromResult("installer opened"); }, () => true);
        await vm.CheckAutomaticallyAsync();
        await vm.CheckAutomaticallyAsync();
        Assert.True(vm.HasUpdate);
        Assert.Equal(2, handler.Requests.Count); // release metadata + manifest, never APK
        Assert.Equal(0, installs);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task Install_WhileLinkedOrInCar_DoesNotDownloadOrCallInstaller()
    {
        var handler = ValidHandler();
        using var client = new HttpClient(handler);
        int installs = 0;
        var vm = new AppUpdateViewModel(new GitHubReleaseUpdater(client), 17, _directory,
            (_, _) => { installs++; return Task.FromResult("installer opened"); }, () => false);
        await vm.CheckCommand.ExecuteAsync(null);
        await vm.InstallCommand.ExecuteAsync(null);
        Assert.Contains("Disconnect", vm.Status);
        Assert.Equal(0, installs);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ExplicitInstall_VerifiesBeforePassingFileToPlatform()
    {
        using var client = new HttpClient(ValidHandler());
        int installs = 0;
        var vm = new AppUpdateViewModel(new GitHubReleaseUpdater(client), 17, _directory,
            async (path, release) =>
            {
                Assert.Equal(Apk, await File.ReadAllBytesAsync(path));
                Assert.Equal(18, release.VersionCode);
                installs++;
                return "installer opened";
            }, () => true);
        await vm.CheckCommand.ExecuteAsync(null);
        await vm.InstallCommand.ExecuteAsync(null);
        Assert.Equal(1, installs);
        Assert.Equal("installer opened", vm.Status);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
