using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NodeX.Core;

/// <summary>
/// Configuration for registering a node with the AllStarLink directory over HTTPS
/// (the protocol used by ASL3's res_rpt_http_registrations module).
/// </summary>
public record AslRegistrationConfig(
    string NodeNumber,
    string NodePassword,
    int Port = Iax2Protocol.DefaultPort,
    string RegistrationUrl = AslRegistrationService.DefaultRegistrationUrl,
    TimeSpan Interval = default,
    TimeSpan RetryDelay = default
)
{
    /// <summary>The official module re-registers every 60 s by default.</summary>
    public TimeSpan EffectiveInterval => Interval == default ? TimeSpan.FromSeconds(60) : Interval;

    /// <summary>
    /// Wait before trying again after a request that never reached the registry: 5 s unless overridden, doubling
    /// per further failure and never longer than the normal interval. Waiting a whole interval leaves a node
    /// unregistered for a minute over one lost request (for example the first one after a network change).
    /// </summary>
    public TimeSpan EffectiveRetryDelay => RetryDelay == default ? TimeSpan.FromSeconds(5) : RetryDelay;
}

/// <summary>
/// Background HTTPS registration with the AllStarLink directory. Mirrors ASL3's
/// <c>res_rpt_http_registrations</c>: a JSON <c>POST</c> to the registration host's root path carrying the
/// node number, password and IAX2 port; the reply reports the address and port the server sees, the
/// refresh interval it wants, and a status string containing "successfully registered" on success.
/// </summary>
public sealed class AslRegistrationService : IAsyncDisposable, IDisposable
{
    public const string DefaultRegistrationUrl = "https://register.allstarlink.org/";
    public const string UserAgent = "NodeX/1.0 (+AllStarLink radio-less node)";

    private const string SuccessMarker = "successfully registered";

    private readonly HttpClient _httpClient;
    private readonly AslRegistrationConfig _config;
    private CancellationTokenSource? _cts;
    private Task? _heartbeatTask;
    private bool _isDisposed;

    public bool IsRegistered { get; private set; }
    public DateTime? LastSuccessfulHeartbeat { get; private set; }
    public string LastStatusMessage { get; private set; } = "Not started";

    /// <summary>Public address the registry saw our request come from (useful to display to the user).</summary>
    public IPAddress? PerceivedAddress { get; private set; }

    /// <summary>Public port the registry associates with this node.</summary>
    public int PerceivedPort { get; private set; }

    /// <summary>Refresh interval requested by the server in its last reply, if any.</summary>
    public TimeSpan? ServerRefresh { get; private set; }

    /// <summary>
    /// Delay before the next registration: the configured interval, but never longer than the
    /// refresh interval the server asked for.
    /// </summary>
    public TimeSpan CurrentInterval =>
        ServerRefresh is { } refresh && refresh > TimeSpan.Zero && refresh < _config.EffectiveInterval
            ? refresh
            : _config.EffectiveInterval;

    public event Action<bool, string>? RegistrationStatusChanged;

    public AslRegistrationService(AslRegistrationConfig config, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>Builds the request body exactly as the official ASL3 module does.</summary>
    internal static string BuildRequestJson(AslRegistrationConfig config)
    {
        var node = new JsonObject
        {
            ["node"] = config.NodeNumber,
            ["passwd"] = config.NodePassword,
            ["remote"] = 0
        };

        var body = new JsonObject();
        if (config.Port > 0) body["port"] = config.Port;
        body["data"] = new JsonObject
        {
            ["nodes"] = new JsonObject { [config.NodeNumber] = node }
        };
        return body.ToJsonString();
    }

    /// <summary>Starts the background registration loop (first registration happens immediately).</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_heartbeatTask != null) return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    /// <summary>Stops the registration loop.</summary>
    public async Task StopAsync()
    {
        if (_cts != null)
        {
            await _cts.CancelAsync();
        }

        if (_heartbeatTask != null)
        {
            try
            {
                await _heartbeatTask;
            }
            catch (OperationCanceledException) { }
            _heartbeatTask = null;
        }

        IsRegistered = false;
        LastStatusMessage = "Stopped";
        RegistrationStatusChanged?.Invoke(false, LastStatusMessage);
    }

    /// <summary>
    /// Sends a single registration request immediately (outside the background schedule, for example right after
    /// the network changed). The background loop then re-plans its next beat: a full interval after a success,
    /// a short retry after a request that never reached the registry.
    /// </summary>
    public async Task<bool> SendHeartbeatAsync(CancellationToken cancellationToken = default)
    {
        bool ok = await SendOnceAsync(cancellationToken);
        Interlocked.Exchange(ref _reschedule, NewSignal()).TrySetResult();
        return ok;
    }

    private async Task<bool> SendOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _config.RegistrationUrl)
            {
                Content = new StringContent(BuildRequestJson(_config), Encoding.UTF8, "application/json")
            };
            request.Headers.UserAgent.ParseAdd(UserAgent);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // 5xx, 408 and 429 say the registry (or something in front of it) is struggling, not that it
                // refused this node.
                int code = (int)response.StatusCode;
                bool verdict = code is < 500 and not 408 and not 429;
                return Fail($"Registration rejected (HTTP {code} {response.ReasonPhrase})", registryAnswered: verdict);
            }

            return ApplyResponse(body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;      // the caller (or StopAsync) cancelled: not a failure
        }
        catch (OperationCanceledException)
        {
            // HttpClient reports its own timeout as a cancellation. It is a failed request like any other; letting
            // it escape used to end the background loop for good, so the node silently stopped re-registering.
            return Fail("Connection failed: the registration server did not answer in time", registryAnswered: false);
        }
        catch (Exception ex)
        {
            return Fail($"Connection failed: {ex.Message}", registryAnswered: false);
        }
    }

    private bool ApplyResponse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Fail("Registration reply was not a JSON object", registryAnswered: false);

            if (root.TryGetProperty("ipaddr", out var ip) && ip.ValueKind == JsonValueKind.String
                && IPAddress.TryParse(ip.GetString(), out var address))
            {
                PerceivedAddress = address;
            }
            if (root.TryGetProperty("port", out var port) && port.TryGetInt32(out int p)) PerceivedPort = p;
            if (root.TryGetProperty("refresh", out var refresh) && refresh.TryGetInt32(out int r) && r > 0)
            {
                ServerRefresh = TimeSpan.FromSeconds(r);
            }

            // "data" is a status string (or object) from the registry; success is signalled in its text.
            string status = root.TryGetProperty("data", out var data)
                ? (data.ValueKind == JsonValueKind.String ? data.GetString() ?? "" : data.GetRawText())
                : "";

            if (status.Contains(SuccessMarker, StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref _transportFailures, 0);
                IsRegistered = true;
                LastSuccessfulHeartbeat = DateTime.UtcNow;
                LastStatusMessage = PerceivedAddress is null
                    ? "Registered"
                    : $"Registered as {PerceivedAddress}:{PerceivedPort}";
                RegistrationStatusChanged?.Invoke(true, LastStatusMessage);
                return true;
            }

            string shown = status.Length > 120 ? status[..120] + "..." : status;
            return Fail(
                string.IsNullOrWhiteSpace(shown) ? "Registration not accepted" : $"Registration not accepted: {shown}",
                registryAnswered: true);
        }
        catch (JsonException)
        {
            // Not the registry talking (a captive portal or proxy page, or a truncated reply).
            return Fail("Registration reply was not valid JSON", registryAnswered: false);
        }
    }

    /// <summary>
    /// Consecutive requests that got no verdict from the registry (no connection, timeout, server error, a reply
    /// that is not the registry's JSON). Reset by any real answer, accepted or not.
    /// </summary>
    private int _transportFailures;

    /// <summary>
    /// While registered, this many requests in a row may fail to reach the registry before the node is reported
    /// as not registered. One lost request does not unregister a node (the registry keeps its entry well past one
    /// interval) and the retry follows within seconds, so reporting it would only reset "registered since" and
    /// make the next connect wait for nothing.
    /// </summary>
    public const int ToleratedTransportFailures = 1;

    /// <param name="registryAnswered">
    /// True when the registry itself refused (wrong password, unknown node): reported at once and retried at the
    /// normal interval. False when the request never got a verdict: tolerated briefly and retried quickly.
    /// </param>
    private bool Fail(string message, bool registryAnswered)
    {
        if (registryAnswered)
        {
            Interlocked.Exchange(ref _transportFailures, 0);
        }
        else
        {
            int failures = Interlocked.Increment(ref _transportFailures);
            if (IsRegistered && failures <= ToleratedTransportFailures) return false;
        }

        IsRegistered = false;
        LastStatusMessage = message;
        RegistrationStatusChanged?.Invoke(false, message);
        return false;
    }

    /// <summary>
    /// Time until the next scheduled request: the normal interval, or after requests that never reached the
    /// registry a short retry (<see cref="AslRegistrationConfig.EffectiveRetryDelay"/>, doubling, never longer
    /// than the interval).
    /// </summary>
    internal TimeSpan NextDelay()
    {
        var interval = CurrentInterval;
        int failures = Volatile.Read(ref _transportFailures);
        if (failures <= 0) return interval;

        double retryTicks = _config.EffectiveRetryDelay.Ticks * Math.Pow(2, Math.Min(failures - 1, 16));
        return retryTicks < interval.Ticks ? TimeSpan.FromTicks((long)retryTicks) : interval;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completed (and replaced) whenever an out-of-band request changes when the next beat is due.</summary>
    private TaskCompletionSource _reschedule = NewSignal();

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await SendOnceAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    // Only a listener of RegistrationStatusChanged can get here. Whatever it was, this loop is
                    // what keeps the node registered, so it carries on.
                }

                // Wait for the next beat. An out-of-band SendHeartbeatAsync restarts the wait, so a failed
                // immediate registration is retried soon and a successful one is not repeated moments later.
                while (true)
                {
                    var rescheduled = Volatile.Read(ref _reschedule).Task;
                    using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var due = Task.Delay(NextDelay(), waitCts.Token);
                    var first = await Task.WhenAny(due, rescheduled);
                    waitCts.Cancel();
                    cancellationToken.ThrowIfCancellationRequested();
                    if (first == due) break;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        await StopAsync();
        _cts?.Dispose();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
