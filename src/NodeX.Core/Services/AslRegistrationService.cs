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
    TimeSpan Interval = default
)
{
    /// <summary>The official module re-registers every 60 s by default.</summary>
    public TimeSpan EffectiveInterval => Interval == default ? TimeSpan.FromSeconds(60) : Interval;
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

    /// <summary>Sends a single registration request immediately.</summary>
    public async Task<bool> SendHeartbeatAsync(CancellationToken cancellationToken = default)
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
                return Fail($"Registration rejected (HTTP {(int)response.StatusCode} {response.ReasonPhrase})");
            }

            return ApplyResponse(body);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail($"Connection failed: {ex.Message}");
        }
    }

    private bool ApplyResponse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Fail("Registration reply was not a JSON object");

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
                IsRegistered = true;
                LastSuccessfulHeartbeat = DateTime.UtcNow;
                LastStatusMessage = PerceivedAddress is null
                    ? "Registered"
                    : $"Registered as {PerceivedAddress}:{PerceivedPort}";
                RegistrationStatusChanged?.Invoke(true, LastStatusMessage);
                return true;
            }

            string shown = status.Length > 120 ? status[..120] + "..." : status;
            return Fail(string.IsNullOrWhiteSpace(shown) ? "Registration not accepted" : $"Registration not accepted: {shown}");
        }
        catch (JsonException)
        {
            return Fail("Registration reply was not valid JSON");
        }
    }

    private bool Fail(string message)
    {
        IsRegistered = false;
        LastStatusMessage = message;
        RegistrationStatusChanged?.Invoke(false, message);
        return false;
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await SendHeartbeatAsync(cancellationToken);
                await Task.Delay(CurrentInterval, cancellationToken);
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
