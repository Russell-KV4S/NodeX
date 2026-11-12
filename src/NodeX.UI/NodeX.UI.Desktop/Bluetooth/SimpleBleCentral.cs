using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NodeX.Core;
using SimpleSharpBLE;

namespace NodeX.UI.Desktop.Bluetooth;

/// <summary>Native WinRT, BlueZ or CoreBluetooth access; initialized only when the user scans/connects.</summary>
public sealed class SimpleBleCentral : IDesktopBleCentral
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, Peripheral> _devices = new(StringComparer.OrdinalIgnoreCase);
    private Adapter? _adapter;
    private volatile bool _disposed;
    public bool IsSupported => !_disposed &&
        (RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64) &&
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() ||
         (OperatingSystem.IsMacOSVersionAtLeast(15) && RuntimeInformation.ProcessArchitecture == Architecture.Arm64));
    public event Action<BleDeviceInfo>? DeviceFound;

    private Adapter Adapter()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsSupported) throw new PlatformNotSupportedException("Desktop BLE needs Windows/Linux x64 or ARM64, or macOS 15+ on Apple Silicon.");
        if (_adapter != null) return _adapter;
        var adapters = SimpleSharpBLE.Adapter.GetAdapters();
        _adapter = adapters.FirstOrDefault(a => a.IsPowered);
        foreach (var adapter in adapters) if (!ReferenceEquals(adapter, _adapter)) adapter.Dispose();
        if (_adapter == null) throw new IOException("No enabled Bluetooth adapter found. Turn Bluetooth on in system settings.");
        _adapter.ScanFound += OnFound;
        _adapter.ScanUpdated += OnFound;
        return _adapter;
    }

    private void OnFound(object? sender, PeripheralEventArgs e) => Remember(e.Peripheral);

    private void Remember(Peripheral peripheral)
    {
        BleDeviceInfo info;
        lock (_devices)
        {
            if (_disposed) { peripheral.Dispose(); return; }
            info = new BleDeviceInfo(peripheral.Address,
                string.IsNullOrWhiteSpace(peripheral.Identifier) ? peripheral.Address : peripheral.Identifier,
                peripheral.Rssi, peripheral.IsPaired);
            if (!_devices.TryAdd(info.Address, peripheral)) peripheral.Dispose();
        }
        DeviceFound?.Invoke(info);
    }

    public async Task StartScanAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await Task.Run(() =>
            {
                var adapter = Adapter();
                foreach (var peer in adapter.GetPairedPeripherals()) Remember(peer);
                if (!adapter.ScanIsActive) adapter.ScanStart();
            }, ct);
        }
        finally { _gate.Release(); }
    }

    public async Task StopScanAsync()
    {
        await _gate.WaitAsync();
        try { await Task.Run(() => { if (_adapter?.ScanIsActive == true) _adapter.ScanStop(); }); }
        finally { _gate.Release(); }
    }

    public async Task<IDesktopBlePeer> ConnectAsync(string address, CancellationToken ct)
    {
        await StartScanAsync(ct);
        try
        {
            // Re-discover after a restart: a saved address alone is insufficient on CoreBluetooth.
            for (int i = 0; i < 32 && !_devices.ContainsKey(address); i++) await Task.Delay(250, ct);
        }
        finally { await StopScanAsync(); }
        if (!_devices.TryRemove(address, out var device))
            throw new IOException("Bluetooth button not found. Turn it on and pair it in system settings if required.");
        var peer = new Peer(device);
        try
        {
            await device.ConnectAsync(ct);
            ct.ThrowIfCancellationRequested();
            return peer;
        }
        catch { await peer.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        await StopScanAsync();
        await _gate.WaitAsync();
        try
        {
            lock (_devices)
            {
                _disposed = true;
                foreach (var entry in _devices) if (_devices.TryRemove(entry.Key, out var device)) device.Dispose();
            }
            if (_adapter != null)
            {
                _adapter.ScanFound -= OnFound;
                _adapter.ScanUpdated -= OnFound;
                _adapter.Dispose();
                _adapter = null;
            }
        }
        finally { _gate.Release(); }
    }

    private sealed class Peer : IDesktopBlePeer
    {
        private readonly Peripheral _device;
        private volatile bool _disposed;
        private readonly List<(string Service, string Characteristic)> _subscriptions = [];
        public Peer(Peripheral device) { _device = device; device.Disconnected += OnDisconnected; }
        public string Name => _device.Identifier;
        public bool IsConnected
        {
            get
            {
                if (_disposed) return false;
                try { return _device.IsConnected; }
                catch (ObjectDisposedException) { return false; }
                catch (BleException) { return false; }
            }
        }
        public event Action? Disconnected;
        public event Action<string, byte[]>? Notification;
        private void OnDisconnected(object? sender, EventArgs e) => Disconnected?.Invoke();

        public async Task SubscribeAsync(CancellationToken ct)
        {
            foreach (var service in _device.Services)
            foreach (var characteristic in service.Characteristics)
            {
                if (!characteristic.CanNotify && !characteristic.CanIndicate) continue;
                string uuid = characteristic.Uuid;
                try
                {
                    if (characteristic.CanNotify)
                        await _device.NotifyAsync(service.Uuid, uuid, data => Notification?.Invoke(uuid, data), ct);
                    else
                        await _device.IndicateAsync(service.Uuid, uuid, data => Notification?.Invoke(uuid, data), ct);
                    _subscriptions.Add((service.Uuid, uuid));
                }
                catch (BleException) { /* Some protected/HID characteristics are owned by the OS. Try the others. */ }
            }
            if (!_subscriptions.Any(s => !string.Equals(s.Characteristic, BlePttManager.BatteryCharacteristic, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("No accessible button notifications. Pair the button in system settings and try again.");
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            _device.Disconnected -= OnDisconnected;
            foreach (var s in _subscriptions)
                try { await _device.UnsubscribeAsync(s.Service, s.Characteristic); } catch (BleException) { }
            try { if (_device.IsConnected) await _device.DisconnectAsync(); }
            catch (BleException) { }
            finally { _device.Dispose(); }
        }
    }
}
