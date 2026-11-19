using System.Net;

namespace NodeX.Core;

/// <summary>
/// Registers over IAX2 from the call socket (<see cref="Iax2Registrar"/>), so the registry records the address the
/// calls come from, and falls back to HTTPS (<see cref="AslRegistrationService"/>) when IAX2 registration has
/// never worked on this network: no reply after <see cref="NoReplyLimit"/> attempts (UDP to the registry blocked)
/// or a rejection (HTTPS then reports its own verdict).
/// <para>
/// Only one of the two ever runs at a time: an HTTPS registration would overwrite the registry entry with the TCP
/// path's address, which on carrier NAT is exactly the address the calls do not come from.
/// </para>
/// </summary>
public sealed class FallbackRegistration : INodeRegistration
{
    private readonly Iax2Registrar _iax;
    private readonly Func<INodeRegistration> _httpsFactory;
    private readonly Lock _gate = new();
    private INodeRegistration _active;
    private INodeRegistration? _https;
    private Task? _switchTask;
    private bool _iaxWorked;
    private bool _started;
    private bool _disposed;
    private CancellationToken _startToken;
    private readonly CancellationTokenSource _lifetime = new();   // cancelled on dispose (ends the IAX2 retry wait)

    /// <summary>IAX2 attempts in a row with no reply (each already re-sent) before switching to HTTPS.</summary>
    public int NoReplyLimit { get; init; } = 2;

    public FallbackRegistration(Iax2Registrar iax, Func<INodeRegistration> httpsFactory)
    {
        _iax = iax ?? throw new ArgumentNullException(nameof(iax));
        _httpsFactory = httpsFactory ?? throw new ArgumentNullException(nameof(httpsFactory));
        _active = iax;
        _iax.RegistrationStatusChanged += OnIaxStatus;
    }

    /// <summary>True once it has switched to HTTPS.</summary>
    public bool UsingFallback
    {
        get { lock (_gate) return _https != null; }
    }

    /// <summary>Why it switched to HTTPS (empty while on IAX2).</summary>
    public string FallbackReason { get; private set; } = "";

    /// <summary>Raised once, with the reason, when it switches to HTTPS.</summary>
    public event Action<string>? FellBack;

    public event Action<bool, string>? RegistrationStatusChanged;

    private INodeRegistration Active
    {
        get { lock (_gate) return _active; }
    }

    public bool IsRegistered => Active.IsRegistered;
    public DateTime? LastSuccessfulHeartbeat => Active.LastSuccessfulHeartbeat;
    public string LastStatusMessage => Active.LastStatusMessage;
    public IPAddress? PerceivedAddress => Active.PerceivedAddress;
    public int PerceivedPort => Active.PerceivedPort;
    public TimeSpan CurrentInterval => Active.CurrentInterval;
    public string Transport => Active.Transport;

    /// <summary>The IAX2 registrar (its socket is the one the calls use).</summary>
    public Iax2Registrar Iax => _iax;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_started) return Task.CompletedTask;
            _started = true;
            _startToken = cancellationToken;
        }
        return _iax.StartAsync(cancellationToken);
    }

    public async Task StopAsync()
    {
        Task? pending;
        lock (_gate) pending = _switchTask;
        if (pending != null) { try { await pending; } catch { /* reported already */ } }

        await _iax.StopAsync();
        INodeRegistration? https;
        lock (_gate) https = _https;
        if (https != null) await https.StopAsync();
    }

    public async Task<bool> SendHeartbeatAsync(CancellationToken cancellationToken = default)
    {
        bool ok = await Active.SendHeartbeatAsync(cancellationToken);
        if (ok) return true;

        Task? pending;
        lock (_gate) pending = _switchTask;
        if (pending == null) return false;

        await pending;                                       // switched to HTTPS just now: try that
        return await Active.SendHeartbeatAsync(cancellationToken);
    }

    private void OnIaxStatus(bool ok, string message)
    {
        if (ok)
        {
            lock (_gate) _iaxWorked = true;
            Forward(_iax, true, message);
            return;
        }

        bool fallBack;
        lock (_gate)
        {
            fallBack = !_iaxWorked && _https == null && _switchTask == null && !_disposed
                       && (_iax.LastFailureWasRejection || _iax.ConsecutiveNoReply >= NoReplyLimit);
            if (fallBack)
            {
                FallbackReason = message;
                // Not on this thread: it is the IAX2 loop, and stopping that loop waits for it to finish.
                _switchTask = Task.Run(SwitchToHttpsAsync);
            }
        }
        Forward(_iax, false, message);
    }

    private async Task SwitchToHttpsAsync()
    {
        await _iax.StopAsync();

        var https = _httpsFactory();
        lock (_gate)
        {
            if (_disposed) { _ = https.DisposeAsync(); return; }
            _https = https;
            _active = https;
        }
        https.RegistrationStatusChanged += (ok, msg) => Forward(https, ok, msg);
        try { FellBack?.Invoke(FallbackReason); } catch { /* a listener must not stop registration */ }
        await https.StartAsync(_startToken);
        var retryToken = CancellationTokenSource.CreateLinkedTokenSource(_startToken, _lifetime.Token).Token;
        _ = Task.Run(() => RetryIaxWhileOnHttpsAsync(retryToken));
    }

    /// <summary>How often IAX2 is tried again while registered over HTTPS.</summary>
    public TimeSpan IaxRetryInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Raised when IAX2 works again and HTTPS has been stopped.</summary>
    public event Action? Recovered;

    /// <summary>
    /// The fallback is not for good. A phone log (2026-11-19) showed IAX2 giving up 17 s after the app started,
    /// two attempts with no reply, most likely before the phone's network was really up; NodeX then stayed on
    /// HTTPS (and so open to "does not match link IP") for the rest of the session. Every
    /// <see cref="IaxRetryInterval"/> one IAX2 registration is tried; if it is accepted, HTTPS stops and IAX2 takes over.
    /// </summary>
    private async Task RetryIaxWhileOnHttpsAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                await Task.Delay(IaxRetryInterval, token);
                INodeRegistration? https;
                lock (_gate)
                {
                    if (_disposed) return;
                    https = _https;
                }
                if (https == null) return;

                if (!await _iax.RegisterOnceAsync(token)) continue;

                lock (_gate)
                {
                    if (_disposed) return;
                    _https = null;
                    _active = _iax;
                    _iaxWorked = true;
                    _switchTask = null;
                    FallbackReason = "";
                }
                await https.DisposeAsync();
                RegistrationStatusChanged?.Invoke(true, _iax.LastStatusMessage);
                try { Recovered?.Invoke(); } catch { }
                await _iax.StartAsync(_startToken);
                return;
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private void Forward(INodeRegistration source, bool ok, string message)
    {
        if (!ReferenceEquals(Active, source)) return;     // the one that is not running has nothing to say
        RegistrationStatusChanged?.Invoke(ok, message);
    }

    public async ValueTask DisposeAsync()
    {
        Task? pending;
        INodeRegistration? https;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            pending = _switchTask;
        }
        _lifetime.Cancel();
        if (pending != null) { try { await pending; } catch { } }
        _iax.RegistrationStatusChanged -= OnIaxStatus;
        await _iax.DisposeAsync();
        lock (_gate) https = _https;
        if (https != null) await https.DisposeAsync();
    }
}
