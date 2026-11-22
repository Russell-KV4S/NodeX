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
        string? link = Array.Find(args, a => a.StartsWith("nodex://", StringComparison.OrdinalIgnoreCase));

        // A clicked nodex:// link starts a new process. If NodeX is already running, hand the link to it and quit,
        // so only one copy registers the node (§4.9). No running copy: carry on and show the link here.
        if (link != null && DeepLinkForwarder.TrySend(DeepLinkForwarder.DefaultPipeName, link, TimeSpan.FromMilliseconds(500)))
            return;

        ConfigureServices();
        if (link != null) AppServices.DeliverDeepLink(link);
        if (OperatingSystem.IsWindows()) WindowsLinkRegistration.EnsureRegistered();
        _ = System.Threading.Tasks.Task.Run(() => DeepLinkForwarder.RunServerAsync(
            DeepLinkForwarder.DefaultPipeName, OnForwardedLink, System.Threading.CancellationToken.None));

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>A link forwarded by a second launch: open the confirmation form and bring the window forward.</summary>
    private static void OnForwardedLink(string link)
    {
        AppServices.DeliverDeepLink(link);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } w })
            {
                if (w.WindowState == Avalonia.Controls.WindowState.Minimized) w.WindowState = Avalonia.Controls.WindowState.Normal;
                w.Activate();
            }
        });
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
        AppServices.LogSharer = (path, _) => RevealFileInManager(path);
        AppServices.AudioFileSharer = (path, _) => RevealFileInManager(path);
    }

    private static System.Threading.Tasks.Task RevealFileInManager(string path)
    {
        var psi = OperatingSystem.IsWindows()
            ? new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            : OperatingSystem.IsMacOS()
                ? new System.Diagnostics.ProcessStartInfo("open", $"-R \"{path}\"")
                : new System.Diagnostics.ProcessStartInfo("xdg-open", $"\"{System.IO.Path.GetDirectoryName(path)}\"");
        psi.UseShellExecute = false;
        System.Diagnostics.Process.Start(psi);
        return System.Threading.Tasks.Task.CompletedTask;
    }
}
