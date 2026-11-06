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

        private void ConfigureServices()
        {
            // Keystore-backed secret storage and the AudioRecord/AudioTrack backend.
            AppServices.Initialize(
                new AndroidKeystoreSecretStore(this),
                () => new AndroidAudioDeviceService(),
                // Battery: open the microphone only while keyed.
                new NodeX.Core.Audio.NodeAudioEngineOptions { OpenMicOnlyWhileKeyed = true },
                networkMonitor: new AndroidNetworkMonitor(this));

            // Keep the process, CPU and Wi-Fi awake (foreground service, wake lock, Bluetooth SCO)
            // exactly while a link is up.
            AppServices.Runtime.LinkActiveChanged += (active, node) =>
            {
                if (!active)
                {
                    NodeForegroundService.Stop(this);
                    return;
                }

                // A microphone foreground service may only start once RECORD_AUDIO is granted.
                if (CheckSelfPermission(global::Android.Manifest.Permission.RecordAudio) == Permission.Granted)
                {
                    NodeForegroundService.Start(this, node);
                }
            };
        }
    }
}
