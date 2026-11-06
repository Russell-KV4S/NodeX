using System;
using System.Threading;
using System.Threading.Tasks;
using NodeX.Core;
using NodeX.Core.Audio;
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
