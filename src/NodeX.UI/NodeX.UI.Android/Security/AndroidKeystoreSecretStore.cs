using System;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using NodeX.Core.Settings;

namespace NodeX.UI.Android.Security;

/// <summary>
/// Android <see cref="ISecretStore"/>: values are encrypted with an AES-256-GCM key that lives in the
/// hardware-backed Android Keystore (never exported), and the ciphertext is kept in app-private
/// SharedPreferences.
/// </summary>
public sealed class AndroidKeystoreSecretStore : ISecretStore
{
    private const string KeyAlias = "nodex_secret_key_v1";
    private const string Provider = "AndroidKeyStore";
    private const string PrefsName = "nodex_secrets";
    private const int GcmTagBits = 128;

    private readonly ISharedPreferences _prefs;
    private readonly object _gate = new();

    public AndroidKeystoreSecretStore(Context context)
    {
        _prefs = (context.ApplicationContext ?? context).GetSharedPreferences(PrefsName, FileCreationMode.Private)!;
    }

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            string? stored = _prefs.GetString(key, null);
            if (stored == null) return Task.FromResult<string?>(null);

            try
            {
                string[] parts = stored.Split(':');
                byte[] iv = Convert.FromBase64String(parts[0]);
                byte[] cipherText = Convert.FromBase64String(parts[1]);

                var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
                cipher.Init(CipherMode.DecryptMode, GetOrCreateKey(), new GCMParameterSpec(GcmTagBits, iv));
                byte[] plain = cipher.DoFinal(cipherText)!;
                return Task.FromResult<string?>(System.Text.Encoding.UTF8.GetString(plain));
            }
            catch (Exception ex) when (ex is Java.Lang.Exception or FormatException or IndexOutOfRangeException)
            {
                // Key invalidated (for example the lock screen was removed) or data damaged:
                // behave as "not stored" so the user is simply asked to re-enter the password.
                return Task.FromResult<string?>(null);
            }
        }
    }

    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
            cipher.Init(CipherMode.EncryptMode, GetOrCreateKey());
            byte[] iv = cipher.GetIV()!;
            byte[] cipherText = cipher.DoFinal(System.Text.Encoding.UTF8.GetBytes(value))!;

            using var editor = _prefs.Edit()!;
            editor.PutString(key, Convert.ToBase64String(iv) + ":" + Convert.ToBase64String(cipherText));
            editor.Commit();
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            using var editor = _prefs.Edit()!;
            editor.Remove(key);
            editor.Commit();
        }
        return Task.CompletedTask;
    }

    private static IKey GetOrCreateKey()
    {
        var store = KeyStore.GetInstance(Provider)!;
        store.Load(null);

        if (store.GetKey(KeyAlias, null) is IKey existing) return existing;

        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, Provider)!;
        generator.Init(new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)!
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!
            .SetKeySize(256)!
            .Build());
        return generator.GenerateKey()!;
    }
}
