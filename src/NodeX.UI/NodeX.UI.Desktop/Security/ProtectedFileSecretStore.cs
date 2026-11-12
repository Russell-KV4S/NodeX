using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NodeX.Core.Settings;

namespace NodeX.UI.Desktop.Security;

/// <summary>
/// Desktop <see cref="ISecretStore"/> persisting secrets in a per-user encrypted file.
/// <list type="bullet">
/// <item><b>Windows:</b> each value is encrypted with DPAPI (<c>CurrentUser</c> scope), so it can only be
/// decrypted by the same Windows account on the same machine.</item>
/// <item><b>macOS:</b> values are encrypted with AES-GCM using a 256-bit key stored in the user's login
/// Keychain (<c>/usr/bin/security</c>).</item>
/// <item><b>Linux:</b> values are encrypted with AES-GCM using a 256-bit key stored in the Freedesktop Secret
/// Service (<c>secret-tool</c> / <c>libsecret</c>) when available, falling back to a <c>0600</c> key file on
/// headless or minimal systems without a keyring daemon.</item>
/// </list>
/// </summary>
public sealed class ProtectedFileSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NodeX.secrets.v1");
    private const string KeychainService = "com.kv4s.nodex";
    private const string KeychainAccount = "master-key";

    private readonly string _path;
    private readonly string _keyPath;
    private readonly bool _useOsKeyring;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ProtectedFileSecretStore(string? directory = null)
    {
        _useOsKeyring = directory == null;
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
        if (_useOsKeyring && TryReadOsKeyring(out byte[]? keyringKey) && keyringKey is { Length: 32 })
        {
            return keyringKey;
        }

        if (File.Exists(_keyPath))
        {
            byte[] existing = File.ReadAllBytes(_keyPath);
            if (existing.Length == 32)
            {
                // Migrate legacy on-disk key into the OS keyring when available and remove the file.
                if (_useOsKeyring && TryWriteOsKeyring(existing))
                {
                    try { File.Delete(_keyPath); } catch (Exception) { }
                }
                return existing;
            }
        }

        byte[] key = RandomNumberGenerator.GetBytes(32);
        if (_useOsKeyring && TryWriteOsKeyring(key))
        {
            return key;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_keyPath)!);
        File.WriteAllBytes(_keyPath, key);
        RestrictToCurrentUser(_keyPath);
        return key;
    }

    private static bool TryReadOsKeyring(out byte[]? key)
    {
        key = null;
        try
        {
            string? b64 = null;
            if (OperatingSystem.IsMacOS())
            {
                b64 = RunTool("/usr/bin/security", ["find-generic-password", "-s", KeychainService, "-a", KeychainAccount, "-w"]);
            }
            else if (OperatingSystem.IsLinux())
            {
                b64 = RunTool("secret-tool", ["lookup", "service", KeychainService, "account", KeychainAccount]);
            }

            if (string.IsNullOrWhiteSpace(b64)) return false;
            byte[] decoded = Convert.FromBase64String(b64.Trim());
            if (decoded.Length != 32) return false;
            key = decoded;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryWriteOsKeyring(byte[] key)
    {
        try
        {
            string b64 = Convert.ToBase64String(key);
            if (OperatingSystem.IsMacOS())
            {
                return RunTool("/usr/bin/security", ["add-generic-password", "-U", "-s", KeychainService, "-a", KeychainAccount, "-w", b64]) != null;
            }
            if (OperatingSystem.IsLinux())
            {
                return RunTool("secret-tool", ["store", "--label=NodeX Master Key", "service", KeychainService, "account", KeychainAccount], stdin: b64) != null;
            }
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? RunTool(string fileName, string[] args, string? stdin = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi);
        if (proc == null) return null;
        if (stdin != null)
        {
            proc.StandardInput.Write(stdin);
            proc.StandardInput.Close();
        }
        string output = proc.StandardOutput.ReadToEnd();
        if (!proc.WaitForExit(3000) || proc.ExitCode != 0) return null;
        return output;
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
