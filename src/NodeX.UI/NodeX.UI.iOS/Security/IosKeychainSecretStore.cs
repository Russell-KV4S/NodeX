using System;
using System.Threading;
using System.Threading.Tasks;
using Foundation;
using NodeX.Core.Settings;
using Security;

namespace NodeX.UI.iOS.Security;

/// <summary>
/// iOS <see cref="ISecretStore"/> backed by the Keychain. Items use <c>AfterFirstUnlockThisDeviceOnly</c>
/// so the app can still read the node password while running in the background with the screen locked
/// (required for the always-on link), without the item migrating to other devices or backups.
/// </summary>
public sealed class IosKeychainSecretStore : ISecretStore
{
    private const string Service = "com.nodex.secrets";

    private static SecRecord Query(string key) => new(SecKind.GenericPassword)
    {
        Service = Service,
        Account = key
    };

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var record = SecKeyChain.QueryAsRecord(Query(key), out SecStatusCode status);
        if (status != SecStatusCode.Success || record?.ValueData == null) return Task.FromResult<string?>(null);
        return Task.FromResult<string?>(NSString.FromData(record.ValueData, NSStringEncoding.UTF8)?.ToString());
    }

    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        var data = NSData.FromString(value, NSStringEncoding.UTF8);

        var existing = SecKeyChain.QueryAsRecord(Query(key), out SecStatusCode lookup);
        SecStatusCode status;
        if (lookup == SecStatusCode.Success && existing != null)
        {
            status = SecKeyChain.Update(Query(key), new SecRecord { ValueData = data });
        }
        else
        {
            var record = Query(key);
            record.ValueData = data;
            record.Accessible = SecAccessible.AfterFirstUnlockThisDeviceOnly;
            status = SecKeyChain.Add(record);
        }

        if (status != SecStatusCode.Success)
            throw new InvalidOperationException($"Keychain write failed ({status}).");
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        SecKeyChain.Remove(Query(key));
        return Task.CompletedTask;
    }
}
