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

            // Keep the process alive while NodeX has work: a configured node stays registered (and follows IP
            // changes) even when nothing is linked; while linked the service also holds the CPU/Wi-Fi locks and
            // Bluetooth routing. It stops only after "Exit NodeX" (the service updates itself in between).
            AppServices.Runtime.BackgroundChanged += state =>
            {
                if (state.Needed) NodeForegroundService.EnsureStarted(this);
                else NodeForegroundService.Stop(this);
            };
        }
    }
}
