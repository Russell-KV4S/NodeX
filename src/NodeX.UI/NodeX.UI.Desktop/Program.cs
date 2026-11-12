using System;
using Avalonia;
using NodeX.Core.Audio;
using NodeX.UI.Desktop.Audio;
using NodeX.UI.Desktop.Security;

namespace NodeX.UI.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        ConfigureServices();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static string? _portAudioProblem;
    private static bool _portAudioChecked;

    /// <summary>
    /// Windows uses NAudio. Linux/macOS use PortAudio; if its native library will not load (typically a missing
    /// libjack on Linux) the app falls back to silent audio, as before, so signalling and registration still work,
    /// and the reason goes to the diagnostic log once.
    /// </summary>
    private static IAudioDeviceService CreateAudioDevice()
    {
        // Devices chosen in Settings apply to the next link. A saved name that is no longer plugged in
        // falls back to the system default.
        var settings = AppServices.Runtime.Config.Settings;
        int inDev = DesktopAudioDevices.Resolve(settings.InputDeviceId, input: true);
        int outDev = DesktopAudioDevices.Resolve(settings.OutputDeviceId, input: false);
        if (!string.IsNullOrEmpty(settings.InputDeviceId) && inDev < 0)
            NodeX.Core.Diagnostics.DiagnosticLog.Default.Info("audio", $"Microphone '{settings.InputDeviceId}' not found; using the system default.");
        if (!string.IsNullOrEmpty(settings.OutputDeviceId) && outDev < 0)
            NodeX.Core.Diagnostics.DiagnosticLog.Default.Info("audio", $"Speaker '{settings.OutputDeviceId}' not found; using the system default.");

        if (OperatingSystem.IsWindows()) return new NAudioDeviceService(inDev, outDev);

        if (!_portAudioChecked)
        {
            _portAudioChecked = true;
            _portAudioProblem = PortAudioDeviceService.CheckAvailable();
            if (_portAudioProblem != null)
                NodeX.Core.Diagnostics.DiagnosticLog.Default.Info("audio", "No sound: " + _portAudioProblem);
        }
        return _portAudioProblem == null ? new PortAudioDeviceService(inDev, outDev) : new NullAudioDeviceService();
    }

    /// <summary>
    /// Supplies the desktop's secure storage and audio backend. Audio uses NAudio (WASAPI) on Windows
    /// and PortAudio (ALSA / CoreAudio) on Linux and macOS.
    /// </summary>
    private static void ConfigureServices()
    {
        AppServices.BleTransport = new NodeX.Core.DesktopBlePttTransport(new Bluetooth.SimpleBleCentral());
        AppServices.Initialize(
            new ProtectedFileSecretStore(),
            CreateAudioDevice,
            // Privacy: open the microphone only while keyed (+3 s hold-off), not for the whole connection.
            new NodeX.Core.Audio.NodeAudioEngineOptions { OpenMicOnlyWhileKeyed = true });

        AppServices.AudioDeviceLister = DesktopAudioDevices.List;

        // Desktop "send logs": show the log file in the file manager; the user attaches it to an e-mail.
        AppServices.LogSharer = (path, _) =>
        {
            var psi = OperatingSystem.IsWindows()
                ? new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                : OperatingSystem.IsMacOS()
                    ? new System.Diagnostics.ProcessStartInfo("open", $"-R \"{path}\"")
                    : new System.Diagnostics.ProcessStartInfo("xdg-open", $"\"{System.IO.Path.GetDirectoryName(path)}\"");
            psi.UseShellExecute = false;
            System.Diagnostics.Process.Start(psi);
            return System.Threading.Tasks.Task.CompletedTask;
        };
    }
}
