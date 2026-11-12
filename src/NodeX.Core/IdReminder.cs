namespace NodeX.Core;

/// <summary>
/// Optional station-identification reminder. It NEVER transmits anything: it only tells the UI that the user
/// should identify by voice. The clock starts when the user starts transmitting and no clock is running; once
/// <c>interval</c> has passed the reminder becomes "due" and stays due until <see cref="Acknowledge"/> (the
/// user tapped "I've identified") or <see cref="Reset"/> (link ended / feature switched off). The next
/// transmission after that starts a fresh clock.
/// </summary>
public sealed class IdReminder : IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private ITimer? _timer;
    private bool _enabled;
    private TimeSpan _interval = DefaultInterval;
    private bool _clockRunning;
    private DateTimeOffset _clockStart;
    private bool _due;
    private bool _disposed;

    public IdReminder(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>Raised (on a timer thread, or the caller's thread for Acknowledge/Reset) when <see cref="IsDue"/> changes.</summary>
    public event Action<bool>? DueChanged;

    public bool IsDue { get { lock (_gate) return _due; } }

    public bool IsEnabled { get { lock (_gate) return _enabled; } }

    /// <summary>Applies the user's settings. Switching off clears any pending reminder.</summary>
    public void Configure(bool enabled, TimeSpan interval)
    {
        bool? change = null;
        lock (_gate)
        {
            if (_disposed) return;
            if (interval <= TimeSpan.Zero) interval = DefaultInterval;
            _enabled = enabled;
            _interval = interval;

            if (!enabled)
            {
                change = ClearLocked();
            }
            else if (_clockRunning)
            {
                change = ScheduleOrFireLocked();
            }
        }
        Raise(change);
    }

    /// <summary>Call when the user keys the transmitter.</summary>
    public void TransmissionStarted()
    {
        bool? change = null;
        lock (_gate)
        {
            if (_disposed || !_enabled || _clockRunning || _due) return;
            _clockRunning = true;
            _clockStart = _time.GetUtcNow();
            change = ScheduleOrFireLocked();
        }
        Raise(change);
    }

    /// <summary>The user says they have identified: clear the reminder and stop the clock.</summary>
    public void Acknowledge() => Acknowledge(transmitting: false);

    /// <summary>
    /// The operator has identified. Clears the reminder; when they are still transmitting (they identified on the
    /// air and tapped the button before unkeying), a fresh clock starts now, because waiting for the next key-up
    /// would leave the rest of this transmission and the gap before the next one uncounted.
    /// </summary>
    public void Acknowledge(bool transmitting)
    {
        Reset();
        if (transmitting) TransmissionStarted();
    }

    /// <summary>Clears everything (link ended, feature switched off).</summary>
    public void Reset()
    {
        bool? change;
        lock (_gate) change = ClearLocked();
        Raise(change);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    // ----- internals (call with _gate held) -----

    private bool? ClearLocked()
    {
        _timer?.Dispose();
        _timer = null;
        _clockRunning = false;
        bool wasDue = _due;
        _due = false;
        return wasDue ? false : null;
    }

    /// <summary>Marks due if the interval already elapsed, otherwise (re)arms a one-shot timer for the remainder.</summary>
    private bool? ScheduleOrFireLocked()
    {
        _timer?.Dispose();
        _timer = null;

        var remaining = _clockStart + _interval - _time.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            _clockRunning = false;
            _due = true;
            return true;
        }

        _timer = _time.CreateTimer(_ => OnTimer(), null, remaining, Timeout.InfiniteTimeSpan);
        return null;
    }

    private void OnTimer()
    {
        bool? change = null;
        lock (_gate)
        {
            if (_disposed || !_clockRunning) return;
            change = ScheduleOrFireLocked();
        }
        Raise(change);
    }

    private void Raise(bool? change)
    {
        if (change is bool b) DueChanged?.Invoke(b);
    }
}
