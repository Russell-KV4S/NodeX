using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NodeX.Core.Updates;

public sealed record AndroidRelease(string Version, long VersionCode, Uri DownloadUrl, long Size, string Sha256);

public sealed class UpdateFeedUnavailableException() : Exception(
    "GitHub updates are not available yet. The release repository may be private, or no release has been published.");

/// <summary>Unauthenticated stable-release feed. No GitHub credentials are shipped in the application.</summary>
public sealed class GitHubReleaseUpdater(HttpClient http)
{
    public const string Repository = "Russell-KV4S/NodeX";
    public const string ApkName = "NodeX-android.apk";
    public const string ManifestName = "nodex-update.json";
    public const long MaxApkSize = 512L * 1024 * 1024;
    private const string DownloadPrefix = "https://github.com/" + Repository + "/releases/download/";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<AndroidRelease?> CheckAsync(long installedVersionCode, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("NodeX-Updater/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new UpdateFeedUnavailableException();
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("GitHub is limiting update checks. Please try again later.");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await ReadSmallAsync(response.Content, ct));
        var release = json.RootElement;
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, @"^v\d+\.\d+(?:\.\d+)?$"))
            throw new InvalidDataException("The release has an unsupported version tag.");

        JsonElement? manifestAsset = null, apkAsset = null;
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() == ManifestName) manifestAsset = asset;
            if (asset.GetProperty("name").GetString() == ApkName) apkAsset = asset;
        }
        if (manifestAsset == null || apkAsset == null)
            throw new InvalidDataException("This release does not contain a complete Android update.");
        var manifestUrl = AssetUrl(manifestAsset.Value, tag, ManifestName);
        var apkUrl = AssetUrl(apkAsset.Value, tag, ApkName);
        using var manifestResponse = await http.GetAsync(manifestUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        manifestResponse.EnsureSuccessStatusCode();
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(await ReadSmallAsync(manifestResponse.Content, ct), JsonOptions)
            ?? throw new InvalidDataException("The update manifest is empty.");
        var android = manifest.Android;
        if (manifest.SchemaVersion != 1 || manifest.Version != tag[1..] || android == null ||
            android.PackageId != "com.kv4s.nodex" || android.AssetName != ApkName || android.VersionCode <= 0 ||
            android.Size <= 0 || android.Size > MaxApkSize || android.Size != apkAsset.Value.GetProperty("size").GetInt64() ||
            !Regex.IsMatch(android.Sha256 ?? "", @"\A[0-9a-fA-F]{64}\z"))
            throw new InvalidDataException("The Android update manifest is invalid.");
        return android.VersionCode > installedVersionCode
            ? new(manifest.Version, android.VersionCode, apkUrl, android.Size, android.Sha256!) : null;
    }

    private static Uri AssetUrl(JsonElement asset, string tag, string name)
    {
        string expected = DownloadPrefix + tag + "/" + name;
        if (asset.GetProperty("browser_download_url").GetString() != expected)
            throw new InvalidDataException("An update asset points outside the expected GitHub release.");
        return new Uri(expected);
    }

    private static async Task<byte[]> ReadSmallAsync(HttpContent content, CancellationToken ct)
    {
        const int limit = 1024 * 1024;
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(bytes, ct)) > 0)
        {
            if (buffer.Length + read > limit) throw new InvalidDataException("The update feed is too large.");
            buffer.Write(bytes, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>Only a complete, hash-verified file is returned. Partial/cancelled downloads are removed.</summary>
    public async Task<string> DownloadAsync(AndroidRelease release, string directory,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!Regex.IsMatch(release.Version, @"^\d+\.\d+(?:\.\d+)?$") ||
            release.DownloadUrl.AbsoluteUri != DownloadPrefix + "v" + release.Version + "/" + ApkName ||
            release.Size <= 0 || release.Size > MaxApkSize || !Regex.IsMatch(release.Sha256, @"\A[0-9a-fA-F]{64}\z"))
            throw new InvalidDataException("The Android update download is invalid.");
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, ApkName);
        string partial = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".part");
        try
        {
            using var response = await http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != release.Size)
                throw new InvalidDataException("The update download has an unexpected size.");
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > release.Size) throw new InvalidDataException("The update download is larger than expected.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    progress?.Report((double)total / release.Size);
                }
                if (total != release.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(release.Sha256)))
                    throw new InvalidDataException("Update verification failed. Please download the update again.");
            }
            ct.ThrowIfCancellationRequested();
            File.Move(partial, destination, overwrite: true);
            return destination;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    private sealed record UpdateManifest(int SchemaVersion, string Version, AndroidManifest? Android);
    private sealed record AndroidManifest(string PackageId, long VersionCode, string AssetName, long Size, string? Sha256);
}
