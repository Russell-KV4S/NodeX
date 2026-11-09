namespace NodeX.Core;

/// <summary>
/// Owns the BLE PTT button: keeps it connected, turns its notifications into pressed / released using the learned
/// <see cref="BlePttProfile"/>, runs the Learn wizard, and makes sure a lost connection never leaves a key held.
/// Safety: only the exact learned press / release packets on the learned characteristic count as PTT, a button with
/// no release packet is a tap (pressed then released at once), and a dropped connection always reports "released".
/// </summary>
public sealed class BlePttManager : IBluetoothPttService
{
    /// <summary>Standard Battery Level characteristic. Reported as battery, never as PTT.</summary>
    public const string BatteryCharacteristic = "00002a19-0000-1000-8000-00805f9b34fb";

    private static readonly TimeSpan LearnStepTimeout = TimeSpan.FromSeconds(25);

    private readonly IBlePttTransport? _transport;
    private readonly object _gate = new();
    private readonly BlePttLearner _learner = new();

    private BlePttProfile? _profile;
    private CancellationTokenSource? _loopCts;
    private CancellationTokenSource? _learnCts;
    private bool _pressed;
    private bool _learning;
    private bool _connecting;
    private bool _disposed;

    public BlePttManager(IBlePttTransport? transport)
    {
        _transport = transport;
        if (transport != null)
        {
            transport.Notification += OnNotification;
            transport.ConnectionChanged += OnConnectionChanged;
            transport.DeviceFound += d => DeviceFound?.Invoke(d);
        }
        _learner.Changed += () => LearnChanged?.Invoke();
    }

    // ----- IBluetoothPttService -----

    public bool IsConnected => _transport?.IsConnected == true;
    public string? DeviceName { get; private set; }
    public event Action<bool>? PttStateChanged;
    public event Action<bool, string>? ConnectionStateChanged;
    public event Action<int>? BatteryLevelReceived;

    public Task StartScanningAsync(CancellationToken cancellationToken = default) =>
        _transport?.StartScanAsync(cancellationToken) ?? Task.CompletedTask;

    public Task StopScanAsync() => _transport?.StopScanAsync() ?? Task.CompletedTask;

    public Task ConnectAsync(string deviceId, CancellationToken cancellationToken = default) =>
        _transport?.ConnectAsync(deviceId, cancellationToken) ?? Task.CompletedTask;

    public Task DisconnectAsync() => _transport?.DisconnectAsync() ?? Task.CompletedTask;

    // ----- status for the UI -----

    public bool IsSupported => _transport?.IsSupported == true;

    public BlePttProfile? Profile => _profile;

    public BlePttLearner Learner => _learner;

    public bool IsLearning => _learning;

    /// <summary>True while the button is held (as far as we know).</summary>
    public bool IsPressed => _pressed;

    public event Action<BleDeviceInfo>? DeviceFound;

    /// <summary>The wizard moved on (or finished).</summary>
    public event Action? LearnChanged;

    /// <summary>Status text or connection state changed.</summary>
    public event Action? StatusChanged;

    /// <summary>Something worth showing in the activity log (a failed connect, a permission problem).</summary>
    public event Action<string>? Notice;

    public string StatusText
    {
        get
        {
            if (!IsSupported) return "BLE PTT: not available";
            var p = _profile;
            if (_learning) return "BLE PTT: learning…";
            if (p == null || !p.IsComplete) return "BLE PTT: not set up";
            string name = string.IsNullOrWhiteSpace(p.DeviceName) ? p.DeviceAddress : p.DeviceName;
            if (!IsConnected) return _connecting ? $"BLE PTT: connecting to {name}…" : $"BLE PTT: {name} (not connected)";
            return _pressed ? $"BLE PTT: {name} — pressed" : $"BLE PTT: {name} ready";
        }
    }

    // ----- profile / connection loop -----

    /// <summary>Sets the saved button (null = none) and keeps it connected in the background.</summary>
    public void SetProfile(BlePttProfile? profile)
    {
        if (profile is { IsComplete: false }) profile = null;

        bool changed;
        lock (_gate)
        {
            changed = _profile != profile;
            _profile = profile;
            if (!changed && _loopCts != null) return;

            _loopCts?.Cancel();
            _loopCts = null;
            if (profile != null && IsSupported && !_disposed)
            {
                _loopCts = new CancellationTokenSource();
                var token = _loopCts.Token;
                _ = Task.Run(() => ConnectLoopAsync(profile, token));
            }
        }

        if (changed && profile == null) _ = DisconnectAsync();
        else if (changed && IsConnected && !string.Equals(_transport!.ConnectedAddress, profile!.DeviceAddress, StringComparison.OrdinalIgnoreCase))
            _ = DisconnectAsync();   // a different button now: drop the old one, the loop connects the new one
        StatusChanged?.Invoke();
    }

    private async Task ConnectLoopAsync(BlePttProfile profile, CancellationToken ct)
    {
        int delaySeconds = 2;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_learning || (_transport!.IsConnected
                    && string.Equals(_transport.ConnectedAddress, profile.DeviceAddress, StringComparison.OrdinalIgnoreCase)))
                {
                    delaySeconds = 2;
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    continue;
                }

                _connecting = true;
                StatusChanged?.Invoke();
                try
                {
                    await _transport.ConnectAsync(profile.DeviceAddress, ct);
                    delaySeconds = 2;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    Notice?.Invoke($"BLE PTT: could not connect to {profile.DeviceName}: {ex.Message}");
                    delaySeconds = Math.Min(delaySeconds * 2, 30);
                }
                finally
                {
                    _connecting = false;
                    StatusChanged?.Invoke();
                }

                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
            }
        }
        catch (OperationCanceledException) { /* profile changed or app closing */ }
    }

    // ----- Learn wizard -----

    /// <summary>Connects to <paramref name="device"/> and starts the press / release wizard.</summary>
    public async Task BeginLearnAsync(BleDeviceInfo device)
    {
        if (_transport is not { IsSupported: true })
        {
            _learner.Fail("Bluetooth LE isn't available on this device.");
            return;
        }

        CancelLearn();
        await _transport.StopScanAsync();

        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _learnCts = cts;
            _learning = true;
        }
        StatusChanged?.Invoke();

        try
        {
            _learner.Connecting();
            if (!string.Equals(_transport.ConnectedAddress, device.Address, StringComparison.OrdinalIgnoreCase)
                || !_transport.IsConnected)
            {
                await _transport.DisconnectAsync();
                await _transport.ConnectAsync(device.Address, cts.Token);
            }
            DeviceName = device.Name;
            _learner.Begin(device.Address, device.Name);
            _ = TimeOutStepsAsync(cts.Token);
        }
        catch (OperationCanceledException) { /* cancelled */ }
        catch (Exception ex)
        {
            _learner.Fail($"Could not connect: {ex.Message}");
            EndLearn();
        }
    }

    private async Task TimeOutStepsAsync(CancellationToken ct)
    {
        try
        {
            while (_learner.IsRunning)
            {
                var step = _learner.Step;
                await Task.Delay(LearnStepTimeout, ct);
                if (_learner.Step == step) _learner.TimeOutStep();   // still stuck on the same step
            }
        }
        catch (OperationCanceledException) { return; }
        EndLearn();
    }

    public void CancelLearn()
    {
        _learner.Cancel();
        EndLearn();
    }

    private void EndLearn()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _learnCts;
            _learnCts = null;
            _learning = false;
        }
        cts?.Cancel();
        cts?.Dispose();

        // Don't stay connected to a device that isn't the saved button.
        var p = _profile;
        if (_transport is { IsConnected: true } t
            && (p == null || !string.Equals(t.ConnectedAddress, p.DeviceAddress, StringComparison.OrdinalIgnoreCase)))
            _ = t.DisconnectAsync();
        StatusChanged?.Invoke();
    }

    // ----- notifications -----

    private void OnNotification(string characteristicUuid, byte[] data)
    {
        if (data.Length == 0) return;

        if (string.Equals(characteristicUuid, BatteryCharacteristic, StringComparison.OrdinalIgnoreCase))
        {
            BatteryLevelReceived?.Invoke(data[0]);
            return;
        }

        if (_learning)
        {
            _learner.OnNotification(characteristicUuid, data);
            if (_learner.Step is BleLearnStep.Done or BleLearnStep.Failed) EndLearn();
            return;
        }

        var p = _profile;
        if (p == null) return;

        if (p.IsPress(characteristicUuid, data))
        {
            SetPressed(true);
            // A button with no release packet is a tap: release straight away so nothing can be left keyed.
            if (!p.HasRelease) SetPressed(false);
        }
        else if (p.IsRelease(characteristicUuid, data))
        {
            SetPressed(false);
        }
    }

    private void SetPressed(bool pressed)
    {
        lock (_gate)
        {
            if (_pressed == pressed) return;
            _pressed = pressed;
        }
        PttStateChanged?.Invoke(pressed);
        StatusChanged?.Invoke();
    }

    private void OnConnectionChanged(bool connected, string text)
    {
        if (connected) DeviceName = _profile?.DeviceName ?? text;

        // A dropped button must never leave the transmitter keyed.
        if (!connected) SetPressed(false);

        ConnectionStateChanged?.Invoke(connected, text);
        StatusChanged?.Invoke();
    }

    // ----- disposal -----

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _loopCts?.Cancel();
        _learnCts?.Cancel();
        SetPressed(false);
        if (_transport != null)
        {
            _transport.Notification -= OnNotification;
            _transport.ConnectionChanged -= OnConnectionChanged;
            await _transport.DisposeAsync();
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
