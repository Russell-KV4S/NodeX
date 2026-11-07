using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NodeX.Core;
using NodeX.Core.Audio;
using NodeX.Core.Diagnostics;
using NodeX.Core.Settings;

namespace NodeX.UI;

/// <summary>
/// Composition root shared by all platform heads. Each head calls <see cref="Initialize"/> once, early
/// (before Avalonia starts), supplying its platform pieces: secure storage and an audio backend.
/// </summary>
public static class AppServices
{
    private static NodeRuntime? _runtime;
    private static readonly object Gate = new();

    /// <summary>The application-wide runtime. Falls back to an in-memory, silent one (designer/previews).</summary>
    public static NodeRuntime Runtime
    {
        get
        {
            lock (Gate)
            {
                return _runtime ??= Create(
                    new MemorySettingsStore(),
                    new InMemorySecretStore(),
                    () => new NullAudioDeviceService(),
                    audioOptions: null);
            }
        }
    }

    /// <summary>
    /// Set by a platform head that can ask the OS for microphone access (Android): shows the system prompt, or
    /// opens the app's permission settings if the prompt was permanently declined. Null where not applicable.
    /// </summary>
    public static Action? MicrophonePermissionRequester { get; set; }

    /// <summary>
    /// How a head ends the process after <see cref="NodeRuntime.ExitAsync"/> (Android: finish the task and kill the
    /// process). Null on desktop, where the app lifetime is shut down instead.
    /// </summary>
    public static Action? ExitHandler { get; set; }

    /// <summary>False where an app may not quit itself (iOS): the "Exit" menu item is hidden there.</summary>
    public static bool CanExit { get; set; } = true;

    /// <summary>
    /// Asks the UI to handle the Back button: returns true if it closed something (a full-screen panel). When it
    /// returns false the platform head moves the app to the background instead of quitting.
    /// </summary>
    public static Func<bool>? BackHandler { get; set; }

    /// <summary>Set by the main view: Back on a phone tab other than Radio returns to the previous tab (true if it did).</summary>
    public static Func<bool>? TabBackHandler { get; set; }

    /// <summary>
    /// Set by a head that can switch the audio output (Android): true = loudspeaker, false = phone earpiece (or a
    /// connected headset). Returns whether the switch worked. Null where the platform has one output.
    /// </summary>
    public static Func<bool, bool>? SpeakerphoneSetter { get; set; }

    /// <summary>
    /// Set by a head that can hand the troubleshooting log to the user (Android: the share sheet, desktop: show the
    /// file). Receives the path of a shareable copy and the log text (capped for message bodies). Null = not supported.
    /// </summary>
    public static Func<string, string, Task>? LogSharer { get; set; }

    /// <summary>Largest log text handed to <see cref="LogSharer"/>; message bodies and Android intents have size limits.</summary>
    public const int ShareTextLimit = 120_000;

    /// <summary>
    /// Packages the troubleshooting log and passes it to the head's <see cref="LogSharer"/>. Returns a sentence for the
    /// user: what happened, or why nothing did.
    /// </summary>
    public static async Task<string> ShareLogsAsync()
    {
        var log = DiagnosticLog.Default;
        if (LogSharer is not { } sharer) return "Sending logs isn't available on this device.";

        string? path = log.CreateShareFile();
        if (path == null) return "There is no log yet. Use the app for a while, then try again.";

        try
        {
            await sharer(path, log.ReadAll(ShareTextLimit));
            return "Log ready to send. It includes your node number, callsign and IP addresses but never your password.";
        }
        catch (Exception ex)
        {
            return $"Could not share the log: {ex.Message}";
        }
    }

    /// <summary>The phone button mapped to push-to-talk; the platform head feeds key events to it.</summary>
    public static HardwarePttMapper HardwarePtt { get; } = new();

    /// <summary>True where a head can deliver hardware key events (Android), so Settings offers the mapping.</summary>
    public static bool HardwareKeysSupported { get; set; }

    /// <summary>Turns a key code into a readable name; set by the head. Falls back to "Key n".</summary>
    public static Func<int, string>? HardwareKeyNamer { get; set; }

    public static string NameKey(int code) =>
        code == 0 ? "Not set" : HardwareKeyNamer?.Invoke(code) ?? $"Key {code}";

    /// <summary>Searches the public AllStarLink node list (one shared instance: it caches and paces requests).</summary>
    public static NodeDirectory Directory { get; } = new();

    /// <summary>True once a platform head has supplied real services.</summary>
    public static bool IsInitialized { get; private set; }

    /// <param name="secrets">OS-protected storage for the node password.</param>
    /// <param name="audioFactory">Creates a fresh audio device for each link.</param>
    /// <param name="audioOptions">Engine tuning, e.g. the zero-PCM keeper on iOS.</param>
    /// <param name="networkMonitor">Platform network monitor; defaults to the system one.</param>
    /// <param name="settings">Settings store; defaults to a JSON file in the per-user app-data folder.</param>
    public static void Initialize(
        ISecretStore secrets,
        Func<IAudioDeviceService> audioFactory,
        NodeAudioEngineOptions? audioOptions = null,
        ISettingsStore? settings = null,
        INetworkMonitor? networkMonitor = null)
    {
        lock (Gate)
        {
            if (IsInitialized) return;

            DiagnosticLog.Default.Configure(Path.Combine(JsonFileSettingsStore.DefaultDirectory, "logs"));
            DiagnosticLog.Default.WriteHeader(typeof(AppServices).Assembly.GetName().Version?.ToString() ?? "?");

            _runtime = Create(settings ?? new JsonFileSettingsStore(), secrets, audioFactory, audioOptions, networkMonitor);
            IsInitialized = true;
        }
    }

    private static NodeRuntime Create(
        ISettingsStore settings,
        ISecretStore secrets,
        Func<IAudioDeviceService> audioFactory,
        NodeAudioEngineOptions? audioOptions,
        INetworkMonitor? networkMonitor = null) =>
        new(new NodeConfigService(settings, secrets), audioFactory, audioOptions: audioOptions, networkMonitor: networkMonitor);

    /// <summary>Best-effort orderly shutdown (unkey, hang up, stop registering) with a time limit.</summary>
    public static void Shutdown(TimeSpan timeout)
    {
        NodeRuntime? runtime;
        lock (Gate) runtime = _runtime;
        if (runtime == null) return;

        try { Task.Run(() => runtime.DisposeAsync().AsTask()).Wait(timeout); }
        catch (Exception) { /* exiting anyway */ }
    }

    private sealed class MemorySettingsStore : ISettingsStore
    {
        private NodeSettings _settings = new();
        public Task<NodeSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_settings);

        public Task SaveAsync(NodeSettings settings, CancellationToken cancellationToken = default)
        {
            _settings = settings;
            return Task.CompletedTask;
        }
    }
}
