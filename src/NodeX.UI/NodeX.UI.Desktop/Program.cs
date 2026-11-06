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

    /// <summary>
    /// Supplies the desktop's secure storage and audio backend. Audio uses NAudio (WASAPI) on Windows;
    /// Linux and macOS currently run silently (signalling and registration work, no sound) until a
    /// cross-platform backend is added.
    /// </summary>
    private static void ConfigureServices() =>
        AppServices.Initialize(
            new ProtectedFileSecretStore(),
            () => OperatingSystem.IsWindows()
                ? new NAudioDeviceService()
                : new NullAudioDeviceService(),
            // Privacy: open the microphone only while keyed (+3 s hold-off), not for the whole connection.
            new NodeX.Core.Audio.NodeAudioEngineOptions { OpenMicOnlyWhileKeyed = true });
}
