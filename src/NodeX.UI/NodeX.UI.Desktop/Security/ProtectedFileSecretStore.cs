using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NodeX.Core.Settings;

namespace NodeX.UI.Desktop.Security;

/// <summary>
/// Desktop <see cref="ISecretStore"/> persisting secrets in a per-user file.
/// <list type="bullet">
/// <item><b>Windows:</b> each value is encrypted with DPAPI (<c>CurrentUser</c> scope), so it can only be
/// decrypted by the same Windows account on the same machine.</item>
/// <item><b>Linux / macOS:</b> there is no portable OS keyring API in .NET, so values are encrypted with
/// AES-GCM using a random key kept in a separate file restricted to the current user (mode 0600). This
/// stops casual reading and accidental sync/backup leaks but is weaker than a real keychain, because
/// anyone who can read both files as that user can decrypt.</item>
/// </list>
/// </summary>
public sealed class ProtectedFileSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NodeX.secrets.v1");

    private readonly string _path;
    private readonly string _keyPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ProtectedFileSecretStore(string? directory = null)
    {
        string dir = directory ?? JsonFileSettingsStore.DefaultDirectory;
        _path = Path.Combine(dir, "secrets.dat");
        _keyPath = Path.Combine(dir, "secrets.key");
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var all = await ReadAllAsync(cancellationToken);
            if (!all.TryGetValue(key, out string? blob)) return null;

            try
            {
                return Encoding.UTF8.GetString(Unprotect(Convert.FromBase64String(blob)));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                return null; // wrong user/machine or damaged file: treat as "not stored" so the user can re-enter it
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var all = await ReadAllAsync(cancellationToken);
            all[key] = Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(value)));
            await WriteAllAsync(all, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var all = await ReadAllAsync(cancellationToken);
            if (all.Remove(key)) await WriteAllAsync(all, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ----- file handling -----

    private async Task<Dictionary<string, string>> ReadAllAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return [];
        try
        {
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, cancellationToken: ct) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task WriteAllAsync(Dictionary<string, string> all, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temp = _path + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, all, cancellationToken: ct);
        }
        RestrictToCurrentUser(temp);
        File.Move(temp, _path, overwrite: true);
    }

    // ----- encryption -----

    private byte[] Protect(byte[] plain)
    {
        if (OperatingSystem.IsWindows())
        {
            return ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        }

        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] tag = new byte[16];
        byte[] cipher = new byte[plain.Length];
        using var aes = new AesGcm(LoadOrCreateKey(), tag.Length);
        aes.Encrypt(nonce, plain, cipher, tag);
        return [.. nonce, .. tag, .. cipher];
    }

    private byte[] Unprotect(byte[] blob)
    {
        if (OperatingSystem.IsWindows())
        {
            return ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
        }

        if (blob.Length < 28) throw new CryptographicException("Secret blob is too short.");
        byte[] nonce = blob[..12];
        byte[] tag = blob[12..28];
        byte[] cipher = blob[28..];
        byte[] plain = new byte[cipher.Length];
        using var aes = new AesGcm(LoadOrCreateKey(), tag.Length);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }

    private byte[] LoadOrCreateKey()
    {
        if (File.Exists(_keyPath))
        {
            byte[] existing = File.ReadAllBytes(_keyPath);
            if (existing.Length == 32) return existing;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_keyPath)!);
        byte[] key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(_keyPath, key);
        RestrictToCurrentUser(_keyPath);
        return key;
    }

    private static void RestrictToCurrentUser(string path)
    {
        // Unix: owner read/write only. On Windows the profile directory is already per-user and DPAPI
        // protects the content, so there is nothing further to set.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
