#if !PLAY_STORE
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using NodeX.Core.Updates;
using NodeX.UI.ViewModels;

[assembly: UsesPermission("android.permission.REQUEST_INSTALL_PACKAGES")]

namespace NodeX.UI.Android;

[ContentProvider(["com.kv4s.nodex.updates"], Exported = false, GrantUriPermissions = true)]
[MetaData("android.support.FILE_PROVIDER_PATHS", Resource = "@xml/update_paths")]
public class UpdateFileProvider : AndroidX.Core.Content.FileProvider;

/// <summary>Only compiled into the full edition. Android always owns the final installation confirmation.</summary>
internal static class AndroidAppUpdater
{
    private static readonly HttpClient Http = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    public static void Configure(Context context)
    {
        var info = context.PackageManager!.GetPackageInfo(context.PackageName!, 0)!;
        AppServices.Updates = new AppUpdateViewModel(new GitHubReleaseUpdater(Http), VersionCode(info),
            Path.Combine(context.CacheDir!.AbsolutePath, "updates"), InstallAsync,
            () => AppServices.Runtime.Connections.Count == 0 && !CarSession.Active);
    }

    private static long VersionCode(PackageInfo info) => OperatingSystem.IsAndroidVersionAtLeast(28)
        ? info.LongVersionCode
#pragma warning disable CA1422
        : info.VersionCode;
#pragma warning restore CA1422

    private static Task<string> InstallAsync(string path, AndroidRelease release)
    {
        var activity = MainActivity.Current ?? throw new InvalidOperationException("Open NodeX on your phone to install this update.");
        if (activity.IsFinishing || activity.IsDestroyed)
            throw new InvalidOperationException("Return to NodeX on your phone to install this update.");
        var manager = activity.PackageManager!;
#pragma warning disable CA1422 // The old signature API is required on Android 8 (minSdk 26).
        var flags = OperatingSystem.IsAndroidVersionAtLeast(28) ? PackageInfoFlags.SigningCertificates : PackageInfoFlags.Signatures;
        var installed = manager.GetPackageInfo(activity.PackageName!, flags)!;
        var archive = manager.GetPackageArchiveInfo(path, flags)
            ?? throw new InvalidDataException("The downloaded file is not an Android application.");
#pragma warning restore CA1422
        if (archive.PackageName != activity.PackageName || VersionCode(archive) != release.VersionCode ||
            VersionCode(archive) <= VersionCode(installed))
            throw new InvalidDataException("This update does not match this application or is not newer.");
        var expectedSigners = Signers(installed);
        var actualSigners = Signers(archive);
        if (expectedSigners.Length == 0 || !expectedSigners.SequenceEqual(actualSigners))
            throw new InvalidDataException("This update has a different signing key. Install only updates from the same NodeX edition.");
        using (var stream = File.OpenRead(path))
        {
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), Convert.FromHexString(release.Sha256)))
                throw new InvalidDataException("The downloaded update changed. Please download it again.");
        }
        if (!manager.CanRequestPackageInstalls())
        {
            activity.StartActivity(new Intent(global::Android.Provider.Settings.ActionManageUnknownAppSources,
                global::Android.Net.Uri.Parse("package:" + activity.PackageName)));
            return Task.FromResult("Allow NodeX to install updates, then return here and tap Download and install again.");
        }
        var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(activity, "com.kv4s.nodex.updates", new Java.IO.File(path));
        var intent = new Intent(Intent.ActionView);
        intent.SetDataAndType(uri, "application/vnd.android.package-archive");
        intent.AddFlags(ActivityFlags.GrantReadUriPermission);
        activity.StartActivity(intent);
        return Task.FromResult("Follow Android's prompt to install. Your settings and node password are preserved.");
    }

    private static string[] Signers(PackageInfo info)
    {
        var signatures = OperatingSystem.IsAndroidVersionAtLeast(28)
            ? info.SigningInfo?.GetApkContentsSigners()
#pragma warning disable CA1422
            : info.Signatures;
#pragma warning restore CA1422
        return signatures?.Select(s => Convert.ToHexString(SHA256.HashData(s.ToByteArray()!)))
            .OrderBy(s => s, StringComparer.Ordinal).ToArray() ?? [];
    }
}
#endif
