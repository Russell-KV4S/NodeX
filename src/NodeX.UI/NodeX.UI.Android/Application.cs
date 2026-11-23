using Android.App;
using Android.Content.PM;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using NodeX.UI.Android.Audio;
using NodeX.UI.Android.Security;

namespace NodeX.UI.Android
{
    [Application]
    public class Application : AvaloniaAndroidApplication<App>
    {
        protected Application(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
        {
        }

        protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        {
            ConfigureServices();
            return base.CustomizeAppBuilder(builder)
            .WithInterFont();
        }

        /// <summary>"1.8 (10)": the manifest versionName and versionCode, so a sent log identifies the release.</summary>
        private string? AndroidVersionName()
        {
            try
            {
                var info = PackageManager?.GetPackageInfo(PackageName!, 0);
                if (info == null) return null;
                // LongVersionCode is API 28+; minSdk is 26, where only the (deprecated) int VersionCode exists.
                long code = System.OperatingSystem.IsAndroidVersionAtLeast(28)
                    ? info.LongVersionCode
#pragma warning disable CA1422
                    : info.VersionCode;
#pragma warning restore CA1422
                string edition = NodeX.Core.BuildFlavor.CarTalkAllowed ? "Full · GitHub" : "Google Play";
                return $"{info.VersionName} ({code}) · {edition}";
            }
            catch (System.Exception) { return null; }
        }

        // The media service (Android Auto) can start the process with no Activity, so services are also set up from
        // OnCreate. Whichever runs first wins.
        private static bool _servicesConfigured;

        public override void OnCreate()
        {
            base.OnCreate();
            ConfigureServices();
        }

        private void ConfigureServices()
        {
            if (_servicesConfigured) return;
            _servicesConfigured = true;

            // Keystore-backed secret storage and the AudioRecord/AudioTrack backend.
            AppServices.Initialize(
                new AndroidKeystoreSecretStore(this),
                () => new AndroidAudioDeviceService(),
                // Battery: open the microphone only while keyed.
                new NodeX.Core.Audio.NodeAudioEngineOptions { OpenMicOnlyWhileKeyed = true },
                networkMonitor: new AndroidNetworkMonitor(this),
                appVersion: AndroidVersionName());

            AppServices.RxLoudnessBoostSupported = true;
            AppServices.MulticastLockAcquirer = () => AcquireMulticastLock(this);
            AppServices.Runtime.RxAudioChanged += (gain, _, boost) =>
                AndroidAudioDeviceService.SetLoudnessBoost(gain, boost);
            AndroidAudioDeviceService.SetLoudnessBoost(
                AppServices.Runtime.LiveRxGain,
                AppServices.Runtime.LiveRxLoudnessBoost);

#if !PLAY_STORE
            AndroidAppUpdater.Configure(this);
#endif

            // Keep the process alive while NodeX has work: a configured node stays registered (and follows IP
            // changes) even when nothing is linked; while linked the service also holds the CPU/Wi-Fi locks and
            // Bluetooth routing. It stops only after "Exit NodeX" (the service updates itself in between).
            AppServices.Runtime.BackgroundChanged += state =>
            {
                if (state.Needed) NodeForegroundService.EnsureStarted(this);
                else NodeForegroundService.Stop(this);
            };

            // Know when the phone is in a car, so a link started on the phone switches to car audio and the wheel
            // buttons work without first picking a node on the car screen.
            CarConnectionWatcher.Start(this);
        }

        private static System.IDisposable? AcquireMulticastLock(global::Android.Content.Context context)
        {
            try
            {
                if (context.GetSystemService(WifiService) is not global::Android.Net.Wifi.WifiManager wifi)
                    return null;
                var mcast = wifi.CreateMulticastLock("NodeXCast");
                if (mcast == null) return null;
                mcast.SetReferenceCounted(false);
                mcast.Acquire();
                return new MulticastLockGuard(mcast);
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        private sealed class MulticastLockGuard(global::Android.Net.Wifi.WifiManager.MulticastLock mcastLock) : System.IDisposable
        {
            public void Dispose()
            {
                try
                {
                    if (mcastLock.IsHeld) mcastLock.Release();
                }
                catch (System.Exception) { }
                mcastLock.Dispose();
            }
        }
    }
}
