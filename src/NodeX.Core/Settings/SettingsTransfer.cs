using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NodeX.Core.Settings;

/// <summary>Result of <see cref="SettingsTransfer.TryImport"/>.</summary>
/// <param name="Settings">The imported settings (normalised); null on failure.</param>
/// <param name="Password">The decrypted node password; null when the file carried none.</param>
/// <param name="Error">Why the import failed; null on success.</param>
/// <param name="NeedsPassphrase">The file holds an encrypted password and no (or the wrong) passphrase was given.</param>
public sealed record SettingsImportResult(NodeSettings? Settings, string? Password, string? Error, bool NeedsPassphrase = false)
{
    public bool Success => Settings != null;
}

/// <summary>
/// Moves a configuration between devices as one small JSON document (file or clipboard text). The settings
/// are plain JSON. The node password is included only when the user supplies a passphrase, and is then
/// encrypted with AES-256-GCM using a PBKDF2-SHA256 key, so the file is safe to send over chat or e-mail.
/// </summary>
public static class SettingsTransfer
{
    public const string Marker = "nodex-settings";
    public const int CurrentVersion = 1;
    private const int Iterations = 200_000;
    private const int MaxDocumentChars = 256 * 1024;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class Document
    {
        public string? Format { get; set; }
        public int Version { get; set; }
        public NodeSettings? Settings { get; set; }
        public SecretBlock? Secret { get; set; }
    }

    private sealed class SecretBlock
    {
        public string Kdf { get; set; } = "pbkdf2-sha256";
        public int Iterations { get; set; }
        public string Salt { get; set; } = "";
        public string Nonce { get; set; } = "";
        public string Tag { get; set; } = "";
        public string Data { get; set; } = "";
    }

    /// <summary>
    /// Serialises <paramref name="settings"/>. The password is included (encrypted) only when both it and a
    /// passphrase are non-empty. Device-specific values (audio devices) are never exported.
    /// </summary>
    public static string Export(NodeSettings settings, string? password = null, string? passphrase = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var portable = settings with { InputDeviceId = null, OutputDeviceId = null };
        var doc = new Document { Format = Marker, Version = CurrentVersion, Settings = portable };

        if (!string.IsNullOrEmpty(password) && !string.IsNullOrEmpty(passphrase))
            doc.Secret = Encrypt(password, passphrase);

        return JsonSerializer.Serialize(doc, Options);
    }

    /// <summary>Parses an exported document. Never throws for bad input; failures come back in the result.</summary>
    public static SettingsImportResult TryImport(string? text, string? passphrase = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new(null, null, "There is nothing to import.");
        if (text.Length > MaxDocumentChars)
            return new(null, null, "That file is too large to be a NodeX settings file.");

        Document? doc;
        try
        {
            doc = JsonSerializer.Deserialize<Document>(text.Trim(), Options);
        }
        catch (JsonException)
        {
            return new(null, null, "That doesn't look like a NodeX settings file.");
        }

        if (doc?.Format != Marker || doc.Settings == null)
            return new(null, null, "That doesn't look like a NodeX settings file.");
        if (doc.Version > CurrentVersion)
            return new(null, null, "This settings file is from a newer version of NodeX. Update the app and try again.");

        var settings = NodeSettingsValidator.Normalize(doc.Settings with { InputDeviceId = null, OutputDeviceId = null });

        string? password = null;
        if (doc.Secret != null)
        {
            if (string.IsNullOrEmpty(passphrase))
                return new(null, null, "This file contains an encrypted node password. Enter its passphrase.", NeedsPassphrase: true);

            password = Decrypt(doc.Secret, passphrase);
            if (password == null)
                return new(null, null, "Wrong passphrase, or the file is damaged.", NeedsPassphrase: true);
        }

        return new(settings, password, null);
    }

    private static SecretBlock Encrypt(string password, string passphrase)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Iterations, HashAlgorithmName.SHA256, 32);

        byte[] plain = Encoding.UTF8.GetBytes(password);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[AesGcm.TagByteSizes.MaxSize];
        using (var aes = new AesGcm(key, tag.Length))
            aes.Encrypt(nonce, plain, cipher, tag);
        CryptographicOperations.ZeroMemory(plain);
        CryptographicOperations.ZeroMemory(key);

        return new SecretBlock
        {
            Iterations = Iterations,
            Salt = Convert.ToBase64String(salt),
            Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag),
            Data = Convert.ToBase64String(cipher)
        };
    }

    private static string? Decrypt(SecretBlock block, string passphrase)
    {
        try
        {
            // Bound the work a hostile file can demand.
            if (block.Kdf != "pbkdf2-sha256" || block.Iterations is < 10_000 or > 2_000_000) return null;

            byte[] salt = Convert.FromBase64String(block.Salt);
            byte[] nonce = Convert.FromBase64String(block.Nonce);
            byte[] tag = Convert.FromBase64String(block.Tag);
            byte[] cipher = Convert.FromBase64String(block.Data);
            if (nonce.Length != AesGcm.NonceByteSizes.MaxSize || tag.Length != AesGcm.TagByteSizes.MaxSize) return null;

            byte[] key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, block.Iterations, HashAlgorithmName.SHA256, 32);
            byte[] plain = new byte[cipher.Length];
            try
            {
                using var aes = new AesGcm(key, tag.Length);
                aes.Decrypt(nonce, cipher, tag, plain);
                return Encoding.UTF8.GetString(plain);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }
}
