namespace NodeX.Core;

/// <summary>
/// The platform's raw BLE access for PTT buttons (Android GATT, iOS CoreBluetooth and desktop native bindings). It only moves bytes: scanning, connecting,
/// subscribing to every notifying characteristic and reporting what arrives. What those bytes mean, learning, and
/// keying safety live in <see cref="BlePttManager"/>, so they are shared and testable.
/// </summary>
public interface IBlePttTransport : IAsyncDisposable
{
    /// <summary>False when the device has no BLE or the platform head has no implementation.</summary>
    bool IsSupported { get; }

    /// <summary>True while a GATT connection is up and notifications are subscribed.</summary>
    bool IsConnected { get; }

    /// <summary>Address of the connected (or connecting) device.</summary>
    string? ConnectedAddress { get; }

    /// <summary>A device turned up while scanning.</summary>
    event Action<BleDeviceInfo>? DeviceFound;

    /// <summary>A characteristic notified: (characteristic UUID, bytes). Raised on a platform thread.</summary>
    event Action<string, byte[]>? Notification;

    /// <summary>The connection came up (true) or dropped (false); the text is the device name or a reason.</summary>
    event Action<bool, string>? ConnectionChanged;

    /// <summary>Starts scanning; also reports devices already paired with the operating system.</summary>
    Task StartScanAsync(CancellationToken cancellationToken = default);

    Task StopScanAsync();

    /// <summary>Connects and subscribes. Completes when ready, throws if it could not (including a missing permission).</summary>
    Task ConnectAsync(string address, CancellationToken cancellationToken = default);

    Task DisconnectAsync();
}
