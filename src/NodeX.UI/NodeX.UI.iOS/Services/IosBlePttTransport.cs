using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreBluetooth;
using CoreFoundation;
using Foundation;
using NodeX.Core;

namespace NodeX.UI.iOS.Services;

/// <summary>
/// iOS CoreBluetooth BLE (GATT) access for push-to-talk buttons: scan, connect, and subscribe to every
/// characteristic that can notify or indicate, reporting the raw bytes to <see cref="BlePttManager"/>.
/// </summary>
public sealed class IosBlePttTransport : IBlePttTransport
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(25);

    private readonly object _gate = new();
    private readonly CentralDelegate _centralDelegate;
    private readonly PeripheralDelegate _peripheralDelegate;
    private CBCentralManager? _central;

    private CBPeripheral? _peripheral;
    private TaskCompletionSource? _ready;
    private int _pendingServices;
    private int _pendingSubscriptions;
    private int _subscribed;
    private volatile bool _isReady;
    private bool _scanning;
    private string? _address;
    private string _name = "";

    public IosBlePttTransport()
    {
        _centralDelegate = new CentralDelegate(this);
        _peripheralDelegate = new PeripheralDelegate(this);
    }

    private CBCentralManager EnsureCentralLocked()
    {
        return _central ??= new CBCentralManager(_centralDelegate, DispatchQueue.MainQueue);
    }

    public bool IsSupported => true;

    public bool IsConnected => _isReady;

    public string? ConnectedAddress => _address;

    public event Action<BleDeviceInfo>? DeviceFound;
    public event Action<string, byte[]>? Notification;
    public event Action<bool, string>? ConnectionChanged;

    private static void RequirePoweredOn(CBCentralManager central)
    {
        switch (central.State)
        {
            case CBManagerState.PoweredOn:
                return;
            case CBManagerState.Unauthorized:
                throw new InvalidOperationException("NodeX doesn't have Bluetooth permission. Allow it in iOS Settings -> NodeX.");
            case CBManagerState.PoweredOff:
                throw new InvalidOperationException("Bluetooth is off. Turn it on and try again.");
            case CBManagerState.Unsupported:
                throw new InvalidOperationException("This device does not support Bluetooth LE.");
            default:
                throw new InvalidOperationException("Bluetooth is starting up. Try again in a moment.");
        }
    }

    public async Task StartScanAsync(CancellationToken cancellationToken = default)
    {
        CBCentralManager central;
        lock (_gate)
        {
            central = EnsureCentralLocked();
        }

        // CoreBluetooth initializes its state asynchronously on the first instantiation.
        if (central.State is CBManagerState.Unknown or CBManagerState.Resetting)
        {
            for (int i = 0; i < 15 && central.State is CBManagerState.Unknown or CBManagerState.Resetting; i++)
                await Task.Delay(100, cancellationToken);
        }

        RequirePoweredOn(central);

        lock (_gate)
        {
            if (_scanning) return;
            _scanning = true;
        }

        central.ScanForPeripherals((CBUUID[]?)null, new PeripheralScanningOptions { AllowDuplicatesKey = false });
    }

    public Task StopScanAsync()
    {
        CBCentralManager? central;
        lock (_gate)
        {
            if (!_scanning) return Task.CompletedTask;
            _scanning = false;
            central = _central;
        }

        try
        {
            if (central?.State == CBManagerState.PoweredOn)
                central.StopScan();
        }
        catch (Exception) { }
        return Task.CompletedTask;
    }

    public async Task ConnectAsync(string address, CancellationToken cancellationToken = default)
    {
        CBCentralManager central;
        lock (_gate)
        {
            central = EnsureCentralLocked();
        }

        if (central.State is CBManagerState.Unknown or CBManagerState.Resetting)
        {
            for (int i = 0; i < 15 && central.State is CBManagerState.Unknown or CBManagerState.Resetting; i++)
                await Task.Delay(100, cancellationToken);
        }

        RequirePoweredOn(central);

        TaskCompletionSource ready;
        lock (_gate)
        {
            if (_isReady && string.Equals(_address, address, StringComparison.OrdinalIgnoreCase)) return;
            ClosePeripheralLocked(raise: false);

            var uuid = new NSUuid(address);
            var peripherals = central.RetrievePeripheralsWithIdentifiers(uuid);
            if (peripherals == null || peripherals.Length == 0)
                throw new InvalidOperationException("That Bluetooth device is not known to this iPhone/iPad. Scan for it again.");

            var peripheral = peripherals[0];
            ready = _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _address = address;
            _name = peripheral.Name ?? address;
            _pendingServices = 0;
            _pendingSubscriptions = 0;
            _subscribed = 0;
            _peripheral = peripheral;
            peripheral.Delegate = _peripheralDelegate;
            central.ConnectPeripheral(peripheral);
        }

        using var cancel = cancellationToken.Register(() => ready.TrySetCanceled(cancellationToken));
        var finished = await Task.WhenAny(ready.Task, Task.Delay(ConnectTimeout, cancellationToken));
        if (finished != ready.Task)
        {
            lock (_gate) ClosePeripheralLocked(raise: false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("The button didn't answer. Is it on, in range, and not connected to something else?");
        }

        try { await ready.Task; }
        catch
        {
            lock (_gate) ClosePeripheralLocked(raise: false);
            throw;
        }
    }

    public Task DisconnectAsync()
    {
        lock (_gate) ClosePeripheralLocked(raise: true);
        return Task.CompletedTask;
    }

    private void ClosePeripheralLocked(bool raise)
    {
        bool wasReady = _isReady;
        string name = _name;
        var peripheral = _peripheral;
        _peripheral = null;
        _isReady = false;
        _address = null;
        _pendingServices = 0;
        _pendingSubscriptions = 0;
        _ready?.TrySetCanceled();
        _ready = null;

        if (peripheral != null)
        {
            try
            {
                if (_central?.State == CBManagerState.PoweredOn)
                    _central.CancelPeripheralConnection(peripheral);
            }
            catch (Exception) { }
        }

        if (raise && wasReady) ConnectionChanged?.Invoke(false, name);
    }

    private void CheckReadyLocked()
    {
        if (_isReady || _ready == null) return;
        if (_pendingServices > 0 || _pendingSubscriptions > 0) return;

        if (_subscribed == 0)
        {
            _ready.TrySetException(new InvalidOperationException("This device has nothing that reports button presses."));
            return;
        }

        _isReady = true;
        _ready.TrySetResult();
        ConnectionChanged?.Invoke(true, _name);
    }

    private sealed class CentralDelegate(IosBlePttTransport owner) : CBCentralManagerDelegate
    {
        public override void UpdatedState(CBCentralManager central)
        {
            if (central.State != CBManagerState.PoweredOn)
            {
                lock (owner._gate)
                {
                    owner._scanning = false;
                    owner.ClosePeripheralLocked(raise: true);
                }
            }
        }

        public override void DiscoveredPeripheral(CBCentralManager central, CBPeripheral peripheral, NSDictionary advertisementData, NSNumber RSSI)
        {
            string id = peripheral.Identifier.AsString();
            string advName = (advertisementData.ObjectForKey(CBAdvertisement.DataLocalNameKey) as NSString)?.ToString() ?? "";
            string name = !string.IsNullOrWhiteSpace(advName) ? advName : (peripheral.Name ?? "");
            int rssi = RSSI?.Int32Value ?? 0;
            owner.DeviceFound?.Invoke(new BleDeviceInfo(id, name, rssi, Bonded: false));
        }

        public override void ConnectedPeripheral(CBCentralManager central, CBPeripheral peripheral)
        {
            lock (owner._gate)
            {
                if (!ReferenceEquals(peripheral, owner._peripheral)) return;
                peripheral.DiscoverServices();
            }
        }

        public override void FailedToConnectPeripheral(CBCentralManager central, CBPeripheral peripheral, NSError? error)
        {
            lock (owner._gate)
            {
                if (!ReferenceEquals(peripheral, owner._peripheral)) return;
                var ready = owner._ready;
                owner.ClosePeripheralLocked(raise: false);
                ready?.TrySetException(new InvalidOperationException(
                    error?.LocalizedDescription ?? "Could not connect to the Bluetooth button."));
            }
        }

        public override void DisconnectedPeripheral(CBCentralManager central, CBPeripheral peripheral, NSError? error)
        {
            lock (owner._gate)
            {
                if (!ReferenceEquals(peripheral, owner._peripheral)) return;
                var ready = owner._ready;
                bool wasReady = owner._isReady;
                string name = owner._name;
                owner.ClosePeripheralLocked(raise: false);
                ready?.TrySetException(new InvalidOperationException(
                    error?.LocalizedDescription ?? "The button disconnected."));
                if (wasReady) owner.ConnectionChanged?.Invoke(false, name);
            }
        }
    }

    private sealed class PeripheralDelegate(IosBlePttTransport owner) : CBPeripheralDelegate
    {
        public override void DiscoveredService(CBPeripheral peripheral, NSError? error)
        {
            lock (owner._gate)
            {
                if (!ReferenceEquals(peripheral, owner._peripheral)) return;
                if (error != null)
                {
                    owner._ready?.TrySetException(new InvalidOperationException(
                        $"Could not read the button's services: {error.LocalizedDescription}"));
                    return;
                }

                var services = peripheral.Services;
                if (services == null || services.Length == 0)
                {
                    owner.CheckReadyLocked();
                    return;
                }

                owner._pendingServices = services.Length;
                foreach (var service in services)
                    peripheral.DiscoverCharacteristics(service);
            }
        }

        public override void DiscoveredCharacteristics(CBPeripheral peripheral, CBService service, NSError? error)
        {
            lock (owner._gate)
            {
                if (!ReferenceEquals(peripheral, owner._peripheral)) return;
                owner._pendingServices = Math.Max(0, owner._pendingServices - 1);

                if (error == null && service.Characteristics is { } chars)
                {
                    foreach (var ch in chars)
                    {
                        if ((ch.Properties & (CBCharacteristicProperties.Notify | CBCharacteristicProperties.Indicate)) != 0)
                        {
                            owner._pendingSubscriptions++;
                            peripheral.SetNotifyValue(true, ch);
                        }
                    }
                }

                owner.CheckReadyLocked();
            }
        }

        public override void UpdatedNotificationState(CBPeripheral peripheral, CBCharacteristic characteristic, NSError? error)
        {
            lock (owner._gate)
            {
                if (!ReferenceEquals(peripheral, owner._peripheral)) return;
                owner._pendingSubscriptions = Math.Max(0, owner._pendingSubscriptions - 1);
                if (error == null && characteristic.IsNotifying)
                    owner._subscribed++;

                owner.CheckReadyLocked();
            }
        }

        public override void UpdatedCharacterteristicValue(CBPeripheral peripheral, CBCharacteristic characteristic, NSError? error)
        {
            if (error != null) return;
            var data = characteristic.Value;
            if (data == null || data.Length == 0) return;
            string uuid = characteristic.UUID.ToString();
            owner.Notification?.Invoke(uuid, data.ToArray());
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopScanAsync();
        lock (_gate)
        {
            ClosePeripheralLocked(raise: false);
            _central?.Dispose();
            _central = null;
        }
    }
}
