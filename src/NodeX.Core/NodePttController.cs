using System.Diagnostics;

namespace NodeX.Core;

/// <summary>
/// Source that initiated a PTT (Push-To-Talk) keying event.
/// </summary>
public enum PttSource
{
    None,
    UiButton,
    BluetoothBle,
    MediaKey,
    Vox,
    External
}

/// <summary>
/// Operation mode of the PTT control.
/// </summary>
public enum PttMode
{
    /// <summary>Transmit while holding; release to unkey.</summary>
    Momentary,
    /// <summary>Tap once to start transmitting; tap again to stop.</summary>
    Toggle
}

/// <summary>
/// Central PTT keying state machine coordinating UI triggers, Bluetooth hardware buttons,
/// media keys, Time-Out Timer (TOT) enforcement, and IAX2 AST_CONTROL_KEY / AST_CONTROL_UNKEY signaling.
/// </summary>
public sealed class NodePttController : IAsyncDisposable, IDisposable
{
    private readonly Iax2Session _session;
    private readonly IBluetoothPttService? _bluetoothPttService;
    private readonly Stopwatch _txDurationStopwatch = new();
    private readonly Lock _stateLock = new();

    private CancellationTokenSource? _totCts;
    private Task? _totMonitorTask;
    private bool _isDisposed;

    /// <summary>Whether transmitter is currently keyed active.</summary>
    public bool IsPttActive { get; private set; }

    /// <summary>Active source that keyed the transmitter.</summary>
    public PttSource CurrentSource { get; private set; } = PttSource.None;

    /// <summary>PTT operating mode (Momentary or Toggle).</summary>
    public PttMode Mode { get; set; } = PttMode.Momentary;

    /// <summary>Amateur radio Time-Out Timer (TOT) limit to prevent stuck transmitters.</summary>
    public TimeSpan TimeOutTimerLimit { get; set; } = TimeSpan.FromSeconds(180);

    /// <summary>Elapsed transmission time for current PTT cycle.</summary>
    public TimeSpan CurrentTxDuration => IsPttActive ? _txDurationStopwatch.Elapsed : TimeSpan.Zero;

    // Events
    public event Action<bool, PttSource>? PttStateChanged;
    public event Action? TimeOutTimerExceeded;
    public event Action<string>? WarningLogged;

    /// <summary>
    /// When set, keying is refused (and an active key is released) while <see cref="TransmitPolicy.CanTransmit"/>
    /// is false, for example in listen-only mode or without microphone permission. Unkeying is never blocked.
    /// </summary>
    public TransmitPolicy? Policy
    {
        get => _policy;
        init
        {
            _policy = value;
            if (value != null) value.Changed += OnPolicyChanged;
        }
    }

    private readonly TransmitPolicy? _policy;

    private void OnPolicyChanged()
    {
        if (_policy is { CanTransmit: false } && IsPttActive)
            _ = Task.Run(() => SetPttStateAsync(false, PttSource.External));
    }

    public NodePttController(Iax2Session session, IBluetoothPttService? bluetoothPttService = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _bluetoothPttService = bluetoothPttService;

        if (_bluetoothPttService != null)
        {
            _bluetoothPttService.PttStateChanged += OnBluetoothPttEvent;
        }
    }

    /// <summary>
    /// Handles PTT key action from the UI button (down=true, up=false).
    /// Respects Momentary vs Toggle mode configuration.
    /// </summary>
    public async Task HandleUiPttAsync(bool pressed)
    {
        if (Mode == PttMode.Toggle)
        {
            if (pressed)
            {
                // Toggle active state on press
                await SetPttStateAsync(!IsPttActive, PttSource.UiButton);
            }
        }
        else
        {
            await SetPttStateAsync(pressed, PttSource.UiButton);
        }
    }

    /// <summary>
    /// Handles a physical button (a mapped phone key, a headset or Bluetooth button): follows the PTT mode, so in
    /// Momentary it keys while held and in Toggle each press flips the state (the release is ignored).
    /// </summary>
    public async Task HandleButtonAsync(bool pressed, PttSource source)
    {
        if (Mode == PttMode.Toggle)
        {
            if (pressed) await SetPttStateAsync(!IsPttActive, source);
        }
        else
        {
            await SetPttStateAsync(pressed, source);
        }
    }

    /// <summary>
    /// Handles media key press (e.g. headset Play/Pause button) typically toggling PTT.
    /// </summary>
    public async Task HandleMediaKeyPttAsync()
    {
        await SetPttStateAsync(!IsPttActive, PttSource.MediaKey);
    }

    /// <summary>
    /// Handles external VOX activation.
    /// </summary>
    public async Task HandleVoxStateAsync(bool voiceDetected)
    {
        if (Mode == PttMode.Momentary)
        {
            await SetPttStateAsync(voiceDetected, PttSource.Vox);
        }
    }

    /// <summary>
    /// Sets the PTT state directly and dispatches IAX2 AST_CONTROL_KEY / AST_CONTROL_UNKEY.
    /// </summary>
    public async Task SetPttStateAsync(bool key, PttSource source)
    {
        bool stateChanged = false;

        if (key && _policy is { CanTransmit: false })
        {
            WarningLogged?.Invoke($"Not transmitting. {_policy.Reason}");
            return;
        }

        lock (_stateLock)
        {
            if (IsPttActive == key)
            {
                return;
            }

            IsPttActive = key;
            CurrentSource = key ? source : PttSource.None;
            stateChanged = true;

            if (key)
            {
                _txDurationStopwatch.Restart();
                StartTotMonitor();
            }
            else
            {
                _txDurationStopwatch.Stop();
                StopTotMonitor();
            }
        }

        if (stateChanged)
        {
            try
            {
                // Signaling via IAX2 session:
                // Emits AST_CONTROL_KEY (12) when true and AST_CONTROL_UNKEY (13) when false
                await _session.SetPttAsync(key);
            }
            catch (Exception ex)
            {
                WarningLogged?.Invoke($"Failed to send IAX2 PTT control frame: {ex.Message}");
            }

            PttStateChanged?.Invoke(key, source);
        }
    }

    private void OnBluetoothPttEvent(bool pressed)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await HandleButtonAsync(pressed, PttSource.BluetoothBle);
            }
            catch (Exception ex)
            {
                WarningLogged?.Invoke($"Bluetooth PTT handling error: {ex.Message}");
            }
        });
    }

    private void StartTotMonitor()
    {
        StopTotMonitor();
        _totCts = new CancellationTokenSource();
        _totMonitorTask = Task.Run(() => TotMonitorLoopAsync(_totCts.Token));
    }

    private void StopTotMonitor()
    {
        if (_totCts != null)
        {
            _totCts.Cancel();
            _totCts.Dispose();
            _totCts = null;
        }
    }

    private async Task TotMonitorLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeOutTimerLimit, cancellationToken);

            // If timer fires without cancellation, force unkey
            WarningLogged?.Invoke($"Time-Out Timer ({TimeOutTimerLimit.TotalSeconds}s) exceeded! Forcing unkey.");
            TimeOutTimerExceeded?.Invoke();

            await SetPttStateAsync(false, PttSource.External);
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_policy != null) _policy.Changed -= OnPolicyChanged;

        if (_bluetoothPttService != null)
        {
            _bluetoothPttService.PttStateChanged -= OnBluetoothPttEvent;
        }

        StopTotMonitor();

        if (IsPttActive)
        {
            await SetPttStateAsync(false, PttSource.None);
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
