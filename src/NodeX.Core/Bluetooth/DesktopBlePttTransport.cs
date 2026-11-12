namespace NodeX.Core;

/// <summary>Native desktop BLE boundary. No PTT decisions belong in a platform driver.</summary>
public interface IDesktopBleCentral : IAsyncDisposable
{
    bool IsSupported { get; }
    event Action<BleDeviceInfo>? DeviceFound;
    Task StartScanAsync(CancellationToken ct);
    Task StopScanAsync();
    Task<IDesktopBlePeer> ConnectAsync(string address, CancellationToken ct);
}

public interface IDesktopBlePeer : IAsyncDisposable
{
    string Name { get; }
    bool IsConnected { get; }
    event Action? Disconnected;
    event Action<string, byte[]>? Notification;
    Task SubscribeAsync(CancellationToken ct);
}

/// <summary>Serializes native operations and rejects notifications from a previous connection.</summary>
public sealed class DesktopBlePttTransport(IDesktopBleCentral central) : IBlePttTransport
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile IDesktopBlePeer? _peer;
    private volatile bool _disposed;
    public bool IsSupported => !_disposed && central.IsSupported;
    public bool IsConnected => _peer?.IsConnected == true && _ready;
    private volatile bool _ready;
    public string? ConnectedAddress { get; private set; }
    public event Action<BleDeviceInfo>? DeviceFound
    {
        add => central.DeviceFound += value;
        remove => central.DeviceFound -= value;
    }
    public event Action<string, byte[]>? Notification;
    public event Action<bool, string>? ConnectionChanged;

    public async Task StartScanAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await central.StartScanAsync(cancellationToken);
    }
    public Task StopScanAsync() => central.StopScanAsync();

    public async Task ConnectAsync(string address, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsConnected && ConnectedAddress == address) return;
            await DisconnectCoreAsync();
            ConnectedAddress = address;
            var peer = await central.ConnectAsync(address, cancellationToken);
            _peer = peer;
            peer.Disconnected += () =>
            {
                if (!ReferenceEquals(_peer, peer)) return;
                _ready = false;
                ConnectionChanged?.Invoke(false, "Bluetooth button disconnected");
            };
            peer.Notification += (uuid, data) =>
            {
                if (ReferenceEquals(_peer, peer) && IsConnected) Notification?.Invoke(uuid, data);
            };
            await peer.SubscribeAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!peer.IsConnected) throw new IOException("The Bluetooth button disconnected during setup.");
            _ready = true;
            ConnectionChanged?.Invoke(true, peer.Name);
        }
        catch
        {
            await DisconnectCoreAsync();
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try { await DisconnectCoreAsync(); }
        finally { _gate.Release(); }
    }

    private async Task DisconnectCoreAsync()
    {
        var peer = _peer;
        _ready = false;
        _peer = null;
        ConnectedAddress = null;
        if (peer == null) return;
        // Notify before the native teardown, which can take seconds: release a held PTT immediately.
        ConnectionChanged?.Invoke(false, "Bluetooth button disconnected");
        await peer.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            await DisconnectCoreAsync();
            await central.DisposeAsync();
        }
        finally { _gate.Release(); }
    }
}
