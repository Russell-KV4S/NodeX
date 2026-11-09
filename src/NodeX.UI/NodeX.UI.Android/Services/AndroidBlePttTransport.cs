using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using NodeX.Core;

namespace NodeX.UI.Android;

/// <summary>
/// Android BLE (GATT) access for push-to-talk buttons: scan (plus already-paired devices), connect, and subscribe to every
/// characteristic that can notify, reporting the raw bytes. Meaning, learning and keying safety are in
/// <see cref="BlePttManager"/>.
/// </summary>
public sealed class AndroidBlePttTransport : IBlePttTransport
{
    private static readonly Java.Util.UUID ClientConfig =
        Java.Util.UUID.FromString("00002902-0000-1000-8000-00805f9b34fb")!;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(25);

    private readonly Context _context;
    private readonly BluetoothAdapter? _adapter;
    private readonly object _gate = new();
    private readonly ScanCb _scanCallback;
    private readonly GattCb _gattCallback;

    private BluetoothGatt? _gatt;
    private TaskCompletionSource? _ready;
    private Queue<BluetoothGattCharacteristic> _pending = new();
    private int _subscribed;
    private volatile bool _isReady;
    private bool _scanning;
    private string? _address;
    private string _name = "";

    public AndroidBlePttTransport(Context context)
    {
        _context = context.ApplicationContext ?? context;
        var manager = _context.GetSystemService(Context.BluetoothService) as BluetoothManager;
        _adapter = manager?.Adapter;
        _scanCallback = new ScanCb(this);
        _gattCallback = new GattCb(this);
    }

    public bool IsSupported =>
        _adapter != null && _context.PackageManager!.HasSystemFeature(PackageManager.FeatureBluetoothLe);

    public bool IsConnected => _isReady;

    public string? ConnectedAddress => _address;

    public event Action<BleDeviceInfo>? DeviceFound;
    public event Action<string, byte[]>? Notification;
    public event Action<bool, string>? ConnectionChanged;

    // ----- permissions -----

    private bool Has(string permission) => _context.CheckSelfPermission(permission) == Permission.Granted;

    private void RequirePermissions(bool scan)
    {
        if (_adapter is not { IsEnabled: true })
            throw new InvalidOperationException("Bluetooth is off. Turn it on and try again.");

        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            if (!Has(global::Android.Manifest.Permission.BluetoothConnect) ||
                (scan && !Has(global::Android.Manifest.Permission.BluetoothScan)))
                throw new InvalidOperationException("NodeX doesn't have the Bluetooth (nearby devices) permission. Allow it in the phone's app settings.");
        }
        else if (scan && !Has(global::Android.Manifest.Permission.AccessFineLocation))
        {
            throw new InvalidOperationException("Android needs the Location permission to scan for Bluetooth devices. Allow it in the phone's app settings.");
        }
    }

    // ----- scanning -----

    public Task StartScanAsync(CancellationToken cancellationToken = default)
    {
        RequirePermissions(scan: true);

        // Devices already paired with the phone don't always advertise, so list them straight away.
        foreach (var device in _adapter!.BondedDevices ?? [])
        {
            if (device.Type is BluetoothDeviceType.Le or BluetoothDeviceType.Dual)
                DeviceFound?.Invoke(new BleDeviceInfo(device.Address ?? "", device.Name ?? "", 0, Bonded: true));
        }

        var scanner = _adapter.BluetoothLeScanner
            ?? throw new InvalidOperationException("This phone can't scan for Bluetooth LE devices right now.");
        lock (_gate)
        {
            if (_scanning) return Task.CompletedTask;
            _scanning = true;
        }
        var settings = new ScanSettings.Builder()!.SetScanMode(global::Android.Bluetooth.LE.ScanMode.LowLatency)!.Build()!;
        scanner.StartScan(null, settings, _scanCallback);
        return Task.CompletedTask;
    }

    public Task StopScanAsync()
    {
        lock (_gate)
        {
            if (!_scanning) return Task.CompletedTask;
            _scanning = false;
        }
        try
        {
            if (Has(OperatingSystem.IsAndroidVersionAtLeast(31)
                    ? global::Android.Manifest.Permission.BluetoothScan
                    : global::Android.Manifest.Permission.AccessFineLocation))
                _adapter?.BluetoothLeScanner?.StopScan(_scanCallback);
        }
        catch (Exception) { /* adapter turned off meanwhile */ }
        return Task.CompletedTask;
    }

    private sealed class ScanCb(AndroidBlePttTransport owner) : ScanCallback
    {
        public override void OnScanResult(ScanCallbackType callbackType, ScanResult? result)
        {
            var device = result?.Device;
            if (device?.Address == null) return;
            string name = result!.ScanRecord?.DeviceName ?? device.Name ?? "";
            owner.DeviceFound?.Invoke(new BleDeviceInfo(device.Address, name, result.Rssi,
                device.BondState == Bond.Bonded));
        }
    }

    // ----- connecting -----

    public async Task ConnectAsync(string address, CancellationToken cancellationToken = default)
    {
        RequirePermissions(scan: false);

        TaskCompletionSource ready;
        lock (_gate)
        {
            if (_isReady && string.Equals(_address, address, StringComparison.OrdinalIgnoreCase)) return;
            CloseGattLocked(raise: false);

            ready = _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _address = address;
            _pending = new Queue<BluetoothGattCharacteristic>();
            _subscribed = 0;
            var device = _adapter!.GetRemoteDevice(address)
                ?? throw new InvalidOperationException("That Bluetooth device is not known to this phone.");
            _name = device.Name ?? address;
            _gatt = device.ConnectGatt(_context, false, _gattCallback, BluetoothTransports.Le);
        }

        using var cancel = cancellationToken.Register(() => ready.TrySetCanceled(cancellationToken));
        var finished = await Task.WhenAny(ready.Task, Task.Delay(ConnectTimeout, cancellationToken));
        if (finished != ready.Task)
        {
            lock (_gate) CloseGattLocked(raise: false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("The button didn't answer. Is it on, in range, and not connected to something else?");
        }

        try { await ready.Task; }
        catch
        {
            lock (_gate) CloseGattLocked(raise: false);
            throw;
        }
    }

    public Task DisconnectAsync()
    {
        lock (_gate) CloseGattLocked(raise: true);
        return Task.CompletedTask;
    }

    private void CloseGattLocked(bool raise)
    {
        bool wasReady = _isReady;
        string name = _name;
        var gatt = _gatt;
        _gatt = null;
        _isReady = false;
        _address = null;
        _pending.Clear();
        _ready?.TrySetCanceled();
        _ready = null;

        if (gatt != null)
        {
            try { gatt.Disconnect(); } catch (Exception) { /* already gone */ }
            try { gatt.Close(); } catch (Exception) { /* already gone */ }
        }
        if (raise && wasReady) ConnectionChanged?.Invoke(false, name);
    }

    private sealed class GattCb(AndroidBlePttTransport owner) : BluetoothGattCallback
    {
        public override void OnConnectionStateChange(BluetoothGatt? gatt, GattStatus status, ProfileState newState)
        {
            if (gatt == null) return;
            lock (owner._gate)
            {
                if (!ReferenceEquals(gatt, owner._gatt)) { try { gatt.Close(); } catch (Exception) { } return; }

                if (newState == ProfileState.Connected)
                {
                    gatt.DiscoverServices();
                }
                else if (newState == ProfileState.Disconnected)
                {
                    var ready = owner._ready;
                    bool wasReady = owner._isReady;
                    string name = owner._name;
                    owner.CloseGattLocked(raise: false);
                    ready?.TrySetException(new InvalidOperationException($"The button disconnected (status {(int)status})."));
                    if (wasReady) owner.ConnectionChanged?.Invoke(false, name);
                }
            }
        }

        public override void OnServicesDiscovered(BluetoothGatt? gatt, GattStatus status)
        {
            if (gatt == null) return;
            lock (owner._gate)
            {
                if (!ReferenceEquals(gatt, owner._gatt)) return;
                if (status != GattStatus.Success)
                {
                    owner._ready?.TrySetException(new InvalidOperationException($"Could not read the button's services (status {(int)status})."));
                    return;
                }

                foreach (var service in gatt.Services ?? [])
                    foreach (var ch in service.Characteristics ?? [])
                        if ((ch.Properties & (GattProperty.Notify | GattProperty.Indicate)) != 0)
                            owner._pending.Enqueue(ch);

                owner.SubscribeNextLocked(gatt);
            }
        }

        public override void OnDescriptorWrite(BluetoothGatt? gatt, BluetoothGattDescriptor? descriptor, GattStatus status)
        {
            if (gatt == null) return;
            lock (owner._gate)
            {
                if (!ReferenceEquals(gatt, owner._gatt)) return;
                // Some characteristics (encrypted HID reports) refuse until paired; carry on with the rest.
                if (status == GattStatus.Success) owner._subscribed++;
                owner.SubscribeNextLocked(gatt);
            }
        }

        // Android 13 and later
        public override void OnCharacteristicChanged(BluetoothGatt gatt, BluetoothGattCharacteristic characteristic, byte[] value) =>
            owner.RaiseNotification(characteristic, value);

        // Android 12 and earlier
#pragma warning disable CA1422 // the old overload is the only one called below API 33
        public override void OnCharacteristicChanged(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic)
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(33) && characteristic != null)
                owner.RaiseNotification(characteristic, characteristic.GetValue() ?? []);
        }
#pragma warning restore CA1422
    }

    private void RaiseNotification(BluetoothGattCharacteristic characteristic, byte[] value)
    {
        string? uuid = characteristic.Uuid?.ToString();
        if (uuid == null || value.Length == 0) return;
        Notification?.Invoke(uuid, value);
    }

    /// <summary>Enables notifications on the next characteristic; when none are left the connection is ready.</summary>
    private void SubscribeNextLocked(BluetoothGatt gatt)
    {
        while (_pending.Count > 0)
        {
            var ch = _pending.Dequeue();
            if (!gatt.SetCharacteristicNotification(ch, true)) continue;

            var descriptor = ch.GetDescriptor(ClientConfig);
            if (descriptor == null) continue;

            byte[] enable = ((ch.Properties & GattProperty.Notify) != 0
                ? BluetoothGattDescriptor.EnableNotificationValue
                : BluetoothGattDescriptor.EnableIndicationValue)!.ToArray();

            bool started;
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                started = gatt.WriteDescriptor(descriptor, enable) == (int)CurrentStatusSuccess;
            }
            else
            {
#pragma warning disable CA1422
                descriptor.SetValue(enable);
                started = gatt.WriteDescriptor(descriptor);
#pragma warning restore CA1422
            }

            if (started) return;   // OnDescriptorWrite continues the queue
        }

        if (_subscribed == 0)
        {
            _ready?.TrySetException(new InvalidOperationException("This device has nothing that reports button presses."));
            return;
        }

        _isReady = true;
        _ready?.TrySetResult();
        ConnectionChanged?.Invoke(true, _name);
    }

    // BluetoothStatusCodes.Success
    private const int CurrentStatusSuccess = 0;

    public async ValueTask DisposeAsync()
    {
        await StopScanAsync();
        lock (_gate) CloseGattLocked(raise: false);
    }
}
