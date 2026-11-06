using System.Collections.Concurrent;
using System.Text.Json;

namespace NodeX.Core.Settings;

/// <summary>Persists <see cref="NodeSettings"/>.</summary>
public interface ISettingsStore
{
    /// <summary>Loads settings, returning defaults when nothing has been saved (or the file is unreadable).</summary>
    Task<NodeSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(NodeSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>
/// Stores secrets (the node password) in OS-protected storage. Implementations live in the platform
/// heads: DPAPI on Windows, Android Keystore, iOS Keychain.
/// </summary>
public interface ISecretStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

public static class SecretKeys
{
    public const string NodePassword = "asl.node.password";
}

/// <summary>Settings as a JSON file, written atomically (temp file + replace).</summary>
public sealed class JsonFileSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonFileSettingsStore(string? path = null) => _path = path ?? DefaultPath;

    /// <summary>Per-user app-data location (app-private storage on Android and iOS).</summary>
    public static string DefaultPath => Path.Combine(DefaultDirectory, "settings.json");

    public static string DefaultDirectory
    {
        get
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(root)) root = AppContext.BaseDirectory;
            return Path.Combine(root, "NodeX");
        }
    }

    public string FilePath => _path;

    public async Task<NodeSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_path)) return new NodeSettings();

            try
            {
                await using var stream = File.OpenRead(_path);
                return await JsonSerializer.DeserializeAsync<NodeSettings>(stream, Options, cancellationToken)
                       ?? new NodeSettings();
            }
            catch (JsonException)
            {
                // Keep the unreadable file for inspection rather than silently destroying it.
                try { File.Move(_path, _path + ".corrupt", overwrite: true); } catch (IOException) { }
                return new NodeSettings();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(NodeSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string temp = _path + ".tmp";
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, settings, Options, cancellationToken);
            }
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>Non-persistent secret store for tests and previews. NOT secure and NOT durable.</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _values = new();

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_values.TryGetValue(key, out var v) ? v : null);

    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        _values.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
