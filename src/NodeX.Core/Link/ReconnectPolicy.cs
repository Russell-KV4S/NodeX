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
    public TimeSpan NetworkChangeDebounce { get; init; } = TimeSpan.FromSeconds(0.5);

    /// <summary>How long a network change waits for the immediate re-registration before re-dialling.</summary>
    public TimeSpan RegistrationWait { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Registered over IAX2 longer ago than this, a dial first re-registers (waiting up to
    /// <see cref="RegistrationWait"/>). The carrier may have let the call socket's NAT mapping lapse since, and the new
    /// mapping can have a different public address; the far node only accepts the call from the registered one.
    /// A lapsed registration (any transport) is always renewed before dialling.
    /// </summary>
    public TimeSpan PreDialRegistrationAge { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How long one attempt may take to reach the connected state before it counts as failed.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(12);

    /// <summary>Give up after this many failed attempts; null keeps trying until the user disconnects.</summary>
    public int? MaxAttempts { get; init; }

    /// <summary>
    /// A remote that hangs up within this long of ACCEPT has most likely not recognised this node yet
    /// (verified live 2026-11-05: app_rpt logs "Reported node N cannot be found" and hangs up ~90 ms after
    /// ACCEPT until its local node list has refreshed, which takes minutes). Such a link is retried.
    /// </summary>
    /// <remarks>
    /// Was 3 s. A phone log (2026-11-18) showed four nodes each accepting and then hanging up 2.4 to 3.6 s later,
    /// over and over, after the phone's address changed: the 3.3 s ones fell outside the window, were treated as
    /// ordinary drops and gave up after 5 tries. 10 s covers that with room to spare.
    /// </remarks>
    public TimeSpan EarlyHangupWindow { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How many times an early hangup is retried (back-off from <see cref="InitialDelay"/> up to
    /// <see cref="MaxDelay"/>) before telling the user; null (the default) keeps retrying until the user
    /// disconnects. Owner decision 2026-11-19: NodeX must not stop retrying on its own.
    /// </summary>
    public int? EarlyHangupMaxRetries { get; init; }

    /// <summary>
    /// How many times in a row a link is redialled after the far node hangs up an established call
    /// <b>without</b> sending app_rpt's <c>!!DISCONNECT!!</c> first (its Asterisk restarted, or it gave up on
    /// packets we could not answer during a short network gap). Null (the default, owner decision 2026-11-19; was
    /// 5, app_rpt's own <c>MAX_RETRIES</c>) keeps redialling until the user disconnects. A deliberate disconnect
    /// (<c>!!DISCONNECT!!</c>) is still never redialled. A link that stays up for <see cref="StableLinkTime"/>
    /// starts a fresh count. Zero never redials.
    /// </summary>
    public int? RemoteHangupMaxRetries { get; init; }

    /// <summary>True when retry <paramref name="n"/> (1-based) is allowed under <paramref name="max"/> (null = no limit).</summary>
    public static bool WithinLimit(int n, int? max) => max is null || n <= max;

    /// <summary>"retry 3" or "retry 3 of 5", for notices.</summary>
    public static string Count(string word, int n, int? max) => max is null ? $"{word} {n}" : $"{word} {n} of {max}";

    /// <summary>A link that stayed up at least this long was healthy, so its next drop starts a fresh retry count.</summary>
    public TimeSpan StableLinkTime { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>What the auto-reconnect machinery is doing right now, for display.</summary>
/// <param name="Active">True while a reconnect cycle is running.</param>
/// <param name="Attempt">1-based number of the current or upcoming attempt.</param>
/// <param name="NextAttemptIn">Delay before the next attempt, or null when an attempt is running or waiting on the network.</param>
/// <param name="Message">Human-readable status.</param>
/// <param name="RetryAt">When the next attempt starts (UTC), so a display can count down; null when not waiting.</param>
public readonly record struct ReconnectStatus(bool Active, int Attempt, TimeSpan? NextAttemptIn, string Message, DateTimeOffset? RetryAt = null)
{
    public static ReconnectStatus Inactive => new(false, 0, null, "");
}
