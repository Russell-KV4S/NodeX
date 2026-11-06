namespace NodeX.Core;

/// <summary>Timing for automatic re-dialling after a link is lost or the network changes.</summary>
public sealed record ReconnectPolicy
{
    /// <summary>Wait before the first retry after a lost link (a network change retries immediately).</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Upper bound for the back-off delay.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Each failed attempt multiplies the delay by this.</summary>
    public double Factor { get; init; } = 2.0;

    /// <summary>Quiet time after a burst of network-change events before acting on them.</summary>
    public TimeSpan NetworkChangeDebounce { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>How long a network change waits for the immediate re-registration before re-dialling.</summary>
    public TimeSpan RegistrationWait { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long one attempt may take to reach the connected state before it counts as failed.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(12);

    /// <summary>Give up after this many failed attempts; null keeps trying until the user disconnects.</summary>
    public int? MaxAttempts { get; init; }

    /// <summary>
    /// A remote that hangs up within this long of ACCEPT has most likely not recognised this node yet
    /// (verified live 2026-11-05: app_rpt logs "Reported node N cannot be found" and hangs up ~90 ms after
    /// ACCEPT until its local node list has refreshed, which takes minutes). Such a link is retried.
    /// </summary>
    public TimeSpan EarlyHangupWindow { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How many times an early hangup is retried (back-off from <see cref="InitialDelay"/> up to
    /// <see cref="MaxDelay"/>; 30 retries is about 28 minutes with the defaults) before telling the user.
    /// Raised from 10 after a busy hub (27339) took the full ~6 minutes and only just accepted on retry 10.
    /// </summary>
    public int EarlyHangupMaxRetries { get; init; } = 30;
}

/// <summary>What the auto-reconnect machinery is doing right now, for display.</summary>
/// <param name="Active">True while a reconnect cycle is running.</param>
/// <param name="Attempt">1-based number of the current or upcoming attempt.</param>
/// <param name="NextAttemptIn">Delay before the next attempt, or null when an attempt is running or waiting on the network.</param>
/// <param name="Message">Human-readable status.</param>
public readonly record struct ReconnectStatus(bool Active, int Attempt, TimeSpan? NextAttemptIn, string Message)
{
    public static ReconnectStatus Inactive => new(false, 0, null, "");
}
