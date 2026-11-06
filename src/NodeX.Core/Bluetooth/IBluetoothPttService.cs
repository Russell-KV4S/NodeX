namespace NodeX.Core;

/// <summary>
/// Abstraction for Bluetooth Low Energy (BLE) PTT (Push-To-Talk) hardware devices
/// such as PRYMEBLU, B01, Dellking, and TexMicro buttons.
/// </summary>
public interface IBluetoothPttService : IAsyncDisposable, IDisposable
{
    /// <summary>Whether a Bluetooth PTT button is currently connected.</summary>
    bool IsConnected { get; }

    /// <summary>Name of the connected Bluetooth device.</summary>
    string? DeviceName { get; }

    /// <summary>Fired when PTT button state changes (true = pressed/transmitting, false = released).</summary>
    event Action<bool>? PttStateChanged;

    /// <summary>Fired when connection status changes with device name.</summary>
    event Action<bool, string>? ConnectionStateChanged;

    /// <summary>Fired when device battery level percentage is reported.</summary>
    event Action<int>? BatteryLevelReceived;

    /// <summary>Starts scanning for BLE PTT devices.</summary>
    Task StartScanningAsync(CancellationToken cancellationToken = default);

    /// <summary>Connects to a specific BLE device by identifier or address.</summary>
    Task ConnectAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>Disconnects from the active device.</summary>
    Task DisconnectAsync();
}
