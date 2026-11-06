using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NodeX.Core;
using NodeX.Core.Audio;
using NodeX.Core.Settings;

namespace NodeX.UI.ViewModels;

/// <summary>
/// Main console. All real work is delegated to <see cref="NodeRuntime"/> (registration, the active
/// <see cref="NodeLink"/>); this class only translates runtime events into bindable state on the UI thread.
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    private const string NotSet = "not set";

    private readonly NodeRuntime _runtime;
    private NodeLink? _link;
    private DispatcherTimer? _timer;
    private volatile float _micMeter;   // written on audio threads, applied by the UI timer
    private volatile float _rxMeter;

    // --- Station & Header Properties ---
    [ObservableProperty]
    public partial string LocalNodeNumber { get; set; } = NotSet;

    [ObservableProperty]
    public partial string LocalCallsign { get; set; } = "NO CALLSIGN";

    [ObservableProperty]
    public partial bool IsAslRegistered { get; set; }

    [ObservableProperty]
    public partial string AslStatusText { get; set; } = "Not registered";

    [ObservableProperty]
    public partial string BleStatusText { get; set; } = "BLE PTT: not set up";

    [ObservableProperty]
    public partial bool IsBleConnected { get; set; }

    // --- Find-a-node overlay ---
    public NodeSearchViewModel Search { get; }

    [ObservableProperty]
    public partial bool IsSearchOpen { get; set; }

    [RelayCommand]
    public void OpenSearch()
    {
        IsSearchOpen = true;
        Search.Opened();
    }

    /// <summary>Adds or removes a favourite and saves it; false when it could not be saved.</summary>
    private async Task<bool> SetFavoriteAsync(string node, bool favorite)
    {
        var cfg = _runtime.Config;
        if (!cfg.IsComplete || cfg.Password == null)
        {
            LogActivity("[ERROR] Finish your Settings before saving favorites.");
            return false;
        }

        var list = cfg.Settings.FavoriteNodes.Where(n => n != node).ToList();
        if (favorite) list.Add(node);

        try
        {
            var result = await _runtime.ConfigService.SaveAsync(cfg.Settings with { FavoriteNodes = list }, cfg.Password);
            return result.Success;
        }
        catch (Exception ex)
        {
            LogActivity($"[ERROR] Could not save favorites: {ex.Message}");
            return false;
        }
    }

    // --- Settings overlay ---
    public SettingsViewModel Settings { get; }

    [ObservableProperty]
    public partial bool IsSettingsOpen { get; set; }

    // --- Connection & Target Node Properties ---
    [ObservableProperty]
    public partial string TargetNodeInput { get; set; } = "";

    [ObservableProperty]
    public partial string ConnectedNodeDisplay { get; set; } = "Not Connected";

    [ObservableProperty]
    public partial string ConnectedNodeDetails { get; set; } = "Enter a node number to connect";

    [ObservableProperty]
    public partial Iax2SessionState SessionState { get; set; } = Iax2SessionState.Disconnected;

    [ObservableProperty]
    public partial string ConnectionStateBadgeText { get; set; } = "DISCONNECTED";

    [ObservableProperty]
    public partial string LatencyText { get; set; } = "-- ms";

    public bool IsConnected => SessionState == Iax2SessionState.Connected;
    public bool IsConnecting => SessionState is Iax2SessionState.Connecting or Iax2SessionState.CallTokenHandshake
                                or Iax2SessionState.Authenticating;
    /// <summary>True while the runtime is automatically re-dialling a lost link.</summary>
    [ObservableProperty]
    public partial bool IsReconnecting { get; set; }

    partial void OnIsReconnectingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanDisconnect));
    }

    public bool CanConnect => SessionState == Iax2SessionState.Disconnected && !IsReconnecting;
    public bool CanDisconnect => SessionState != Iax2SessionState.Disconnected || IsReconnecting;

    // --- Transmit permission: listen-only or microphone denied. Listening always works. ---
    public bool CanTransmit => _runtime.Transmit.CanTransmit;
    public bool IsTransmitBlocked => !CanTransmit;
    public string TransmitBlockedText => _runtime.Transmit.Reason;
    public string PttHeadline => CanTransmit ? "PUSH TO TALK" : "LISTEN ONLY";
    public string PttCaption => _runtime.Transmit.Block == TransmitBlock.MicrophoneDenied ? "MIC OFF" : CanTransmit ? "MIC" : "NO TX";

    /// <summary>True when the OS denied the microphone and the platform can show its permission prompt.</summary>
    public bool CanFixMicrophone =>
        _runtime.Transmit.Block == TransmitBlock.MicrophoneDenied && AppServices.MicrophonePermissionRequester != null;

    /// <summary>True when transmitting is off only because no callsign has been entered.</summary>
    public bool CanAddCallsign => _runtime.Transmit.Block == TransmitBlock.ListenOnly;

    private void RefreshTransmit()
    {
        OnPropertyChanged(nameof(CanTransmit));
        OnPropertyChanged(nameof(IsTransmitBlocked));
        OnPropertyChanged(nameof(TransmitBlockedText));
        OnPropertyChanged(nameof(PttHeadline));
        OnPropertyChanged(nameof(PttCaption));
        OnPropertyChanged(nameof(CanFixMicrophone));
        OnPropertyChanged(nameof(CanAddCallsign));
    }

    [RelayCommand]
    public void FixMicrophone() => AppServices.MicrophonePermissionRequester?.Invoke();

    [RelayCommand]
    public async Task AddCallsignAsync() => await OpenSettingsAsync();

    // --- Radio Activity & PTT Properties ---
    [ObservableProperty]
    public partial bool IsTransmitting { get; set; }

    [ObservableProperty]
    public partial bool IsRemoteKeyed { get; set; }

    [ObservableProperty]
    public partial bool IsToggleMode { get; set; }

    [ObservableProperty]
    public partial string PttModeLabel { get; set; } = "Momentary (Hold)";

    [ObservableProperty]
    public partial string TxDurationDisplay { get; set; } = "00:00";

    [ObservableProperty]
    public partial double TotProgressPercent { get; set; }

    [ObservableProperty]
    public partial double MicAudioLevel { get; set; }

    [ObservableProperty]
    public partial double RxAudioLevel { get; set; }

    // --- Telemetry & Recent Lists ---
    public ObservableCollection<string> ActivityLog { get; } = [];
    public ObservableCollection<string> FavoriteNodes { get; } = [];
    public bool HasFavorites => FavoriteNodes.Count > 0;

    /// <summary>Keys of the DTMF pad in display order (3 columns).</summary>
    public string[] DtmfKeys { get; } = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "*", "0", "#"];

    /// <summary>"elapsed / limit" for the transmit time-out timer, using the configured limit.</summary>
    [ObservableProperty]
    public partial string TotCounterText { get; set; } = "00:00 / 03:00";

    private TimeSpan TotLimit =>
        _link?.Ptt.TimeOutTimerLimit ?? TimeSpan.FromSeconds(_runtime.Config.Settings.TimeOutTimerSeconds);

    private static string FormatTot(TimeSpan elapsed, TimeSpan limit) =>
        $"{(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2} / {(int)limit.TotalMinutes:D2}:{limit.Seconds:D2}";

    // --- Optional station-ID reminder (shows a prompt only; transmits nothing) ---
    private readonly IdReminder _idReminder = new();

    [ObservableProperty]
    public partial bool IsIdReminderDue { get; set; }

    /// <summary>Raised on the UI thread when the reminder becomes due (heads may post a notification).</summary>
    public event Action? IdReminderDue;

    [RelayCommand]
    public void AcknowledgeId()
    {
        _idReminder.Acknowledge();
        LogActivity("Station ID acknowledged.");
    }

    private void OnIdReminderDueChanged(bool due) => OnUi(() =>
    {
        IsIdReminderDue = due;
        if (due)
        {
            LogActivity("[ID] Time to identify (say your callsign).");
            IdReminderDue?.Invoke();
        }
    });

    /// <summary>Uses the application-wide runtime (see <see cref="AppServices"/>).</summary>
    public MainViewModel() : this(AppServices.Runtime) { }

    public MainViewModel(NodeRuntime runtime, NodeDirectory? directory = null)
    {
        _runtime = runtime;
        _idReminder.DueChanged += OnIdReminderDueChanged;

        Search = new NodeSearchViewModel(
            directory ?? AppServices.Directory,
            () => FavoriteNodes,
            SetFavoriteAsync);
        Search.Closed += () => IsSearchOpen = false;
        Search.NodeChosen += node =>
        {
            TargetNodeInput = node;
            IsSearchOpen = false;
        };

        Settings = new SettingsViewModel(runtime.ConfigService);
        Settings.Saved += OnSettingsSaved;
        Settings.Cancelled += () => IsSettingsOpen = false;

        runtime.RegistrationChanged += () => OnUi(ApplyRegistration);
        runtime.LinkChanged += link => OnUi(() => AttachLink(link));
        runtime.ErrorOccurred += msg => LogActivity($"[ERROR] {msg}");
        runtime.Notice += msg => LogActivity(msg);
        runtime.ReconnectChanged += status => OnUi(() => ApplyReconnect(status));
        runtime.ConfigService.Changed += cfg => OnUi(() => ApplyConfig(cfg));
        runtime.Transmit.Changed += () => OnUi(RefreshTransmit);

        ApplyConfig(runtime.Config);
        ApplyRegistration();
    }

    /// <summary>
    /// Battery: the 100 ms meter/timer refresh is only useful while the screen is showing. Mobile heads call
    /// this from the app's activated/deactivated events; the link, audio and reconnect logic are unaffected.
    /// </summary>
    public void SetForeground(bool foreground)
    {
        if (_timer == null) return;
        if (foreground)
        {
            _timer.Start();
            OnUiTimerTick();
        }
        else
        {
            _timer.Stop();
        }
    }

    /// <summary>Starts the runtime (loads settings, registers) and opens Settings on first run.</summary>
    public async Task InitializeAsync()
    {
        _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => OnUiTimerTick());
        _timer.Start();

        try
        {
            await _runtime.StartAsync();
            OnUi(() =>
            {
                ApplyConfig(_runtime.Config);
                ApplyRegistration();
            });

            if (!_runtime.Config.IsComplete)
            {
                LogActivity("Welcome to NodeX. Enter your node number, node password and callsign in Settings to get started (no callsign? choose Listen only).");
                await OpenSettingsAsync();
            }
            else
            {
                LogActivity($"NodeX ready. Station {LocalCallsign}, node {LocalNodeNumber}.");
            }
        }
        catch (Exception ex)
        {
            LogActivity($"[ERROR] Startup failed: {ex.Message}");
        }
    }

    // --- Commands ---

    [RelayCommand]
    public async Task OpenSettingsAsync()
    {
        await Settings.LoadAsync();
        IsSettingsOpen = true;
    }

    [RelayCommand]
    public async Task ConnectNodeAsync()
    {
        if (!_runtime.Config.IsComplete)
        {
            LogActivity("[ERROR] Finish your Settings (node number, password, and callsign or Listen only) before connecting.");
            await OpenSettingsAsync();
            return;
        }

        string target = TargetNodeInput?.Trim() ?? "";
        if (!NodeSettingsValidator.IsValidNodeNumber(target))
        {
            LogActivity("[ERROR] Please enter a valid target node number (3 to 7 digits).");
            return;
        }

        try
        {
            LogActivity($"Resolving node {target}...");
            var info = await _runtime.ConnectAsync(target);
            if (info == null)
            {
                LogActivity($"[ERROR] Unable to resolve node {target} via the ASL directory or DNS.");
                return;
            }

            ConnectedNodeDisplay = $"Node {info.NodeNumber}";
            ConnectedNodeDetails = string.IsNullOrWhiteSpace(info.Callsign)
                ? $"{info.Description} ({info.EndPoint.Address})"
                : $"{info.Callsign} • {info.Description} ({info.EndPoint.Address})";
            LogActivity($"Connecting to {info.NodeNumber} at {info.EndPoint}...");
        }
        catch (Exception ex)
        {
            LogActivity($"[ERROR] Connection failed: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task DisconnectNodeAsync()
    {
        try
        {
            LogActivity("Disconnecting from remote node...");
            await _runtime.DisconnectAsync();
        }
        catch (Exception ex)
        {
            LogActivity($"[ERROR] Disconnect error: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task PttDownAsync()
    {
        if (!IsToggleMode && _link != null) await _link.Ptt.HandleUiPttAsync(true);
    }

    [RelayCommand]
    public async Task PttUpAsync()
    {
        if (!IsToggleMode && _link != null) await _link.Ptt.HandleUiPttAsync(false);
    }

    [RelayCommand]
    public async Task TogglePttClickAsync()
    {
        if (IsToggleMode && _link != null) await _link.Ptt.HandleUiPttAsync(true);
    }

    [RelayCommand]
    public void TogglePttMode()
    {
        IsToggleMode = !IsToggleMode;
        if (_link != null) _link.Ptt.Mode = IsToggleMode ? PttMode.Toggle : PttMode.Momentary;
        PttModeLabel = IsToggleMode ? "Toggle (Click)" : "Momentary (Hold)";
        LogActivity($"PTT Mode switched to: {PttModeLabel}");
    }

    [RelayCommand]
    public async Task SendDtmfDigitAsync(string digit)
    {
        if (string.IsNullOrEmpty(digit)) return;
        await SendDtmfSequenceAsync(digit);
    }

    /// <summary>AllStarLink function shortcuts: "link" = *3&lt;node&gt;, "unlink" = *1&lt;node&gt;, "status" = *70.</summary>
    [RelayCommand]
    public async Task SendDtmfMacroAsync(string macro)
    {
        string target = TargetNodeInput?.Trim() ?? "";
        string? sequence = macro switch
        {
            "link" when NodeSettingsValidator.IsValidNodeNumber(target) => "*3" + target,
            "unlink" when NodeSettingsValidator.IsValidNodeNumber(target) => "*1" + target,
            "status" => "*70",
            _ => null
        };

        if (sequence == null)
        {
            LogActivity("[ERROR] Enter the node number to link or unlink in the Connect box first.");
            return;
        }

        await SendDtmfSequenceAsync(sequence);
    }

    private async Task SendDtmfSequenceAsync(string sequence)
    {
        var link = _link;
        if (link == null || !IsConnected)
        {
            LogActivity("[DTMF] Not connected.");
            return;
        }

        LogActivity($"[DTMF] Sending '{sequence}'");
        foreach (char c in sequence)
        {
            await link.Session.SendDtmfAsync(c);
            await Task.Delay(120);   // let the far end decode each tone before the next one
        }
    }

    [RelayCommand]
    public void SelectFavoriteNode(string node) => TargetNodeInput = node;

    [RelayCommand]
    public void ClearLog() => ActivityLog.Clear();

    // --- Settings / registration / link plumbing (UI thread) ---

    private void OnSettingsSaved()
    {
        IsSettingsOpen = false;
        LogActivity("Settings saved.");
    }

    private void ApplyConfig(NodeConfig cfg)
    {
        var s = cfg.Settings;
        _idReminder.Configure(s.IdReminderEnabled, TimeSpan.FromMinutes(s.IdReminderMinutes));
        LocalNodeNumber = s.NodeNumber.Length > 0 ? s.NodeNumber : NotSet;
        LocalCallsign = s.Callsign.Length > 0 ? s.Callsign : s.ListenOnly ? "LISTEN ONLY" : "NO CALLSIGN";
        _runtime.Transmit.ListenOnly = s.ListenOnly;   // also set by the runtime; keeps the screen right if this runs first
        RefreshTransmit();

        FavoriteNodes.Clear();
        foreach (string node in s.FavoriteNodes) FavoriteNodes.Add(node);
        OnPropertyChanged(nameof(HasFavorites));
        if (!IsTransmitting) TotCounterText = FormatTot(TimeSpan.Zero, TotLimit);

        if (string.IsNullOrWhiteSpace(TargetNodeInput) && s.DefaultTargetNode.Length > 0)
            TargetNodeInput = s.DefaultTargetNode;

        if (_link == null)
        {
            IsToggleMode = s.TogglePtt;
            PttModeLabel = IsToggleMode ? "Toggle (Click)" : "Momentary (Hold)";
        }
    }

    private void ApplyRegistration()
    {
        IsAslRegistered = _runtime.IsRegistered;
        AslStatusText = _runtime.IsRegistered ? "ASL Registered" : _runtime.RegistrationStatus;
    }

    private void ApplyReconnect(ReconnectStatus status)
    {
        IsReconnecting = status.Active;
        if (status.Active)
        {
            ConnectionStateBadgeText = $"RECONNECTING ({status.Attempt})";
            ConnectedNodeDetails = status.Message;
        }
        else if (_link == null)
        {
            // Recovery ended without a link (user cancelled, or gave up).
            UpdateConnectionBadge();
            ConnectedNodeDisplay = "Not Connected";
            ConnectedNodeDetails = "Enter a node number to connect";
        }
        else
        {
            UpdateConnectionBadge();
        }
    }

    private void AttachLink(NodeLink? link)
    {
        DetachLink();
        _link = link;

        if (link == null)
        {
            _idReminder.Reset();
            SessionState = Iax2SessionState.Disconnected;
            UpdateConnectionBadge();
            LatencyText = "-- ms";
            if (!IsReconnecting)
            {
                ConnectedNodeDisplay = "Not Connected";
                ConnectedNodeDetails = "Enter a node number to connect";
            }
            IsTransmitting = false;
            IsRemoteKeyed = false;
            TxDurationDisplay = "00:00";
            TotProgressPercent = 0;
            TotCounterText = FormatTot(TimeSpan.Zero, TotLimit);
            _micMeter = _rxMeter = 0;
            return;
        }

        TotCounterText = FormatTot(TimeSpan.Zero, link.Ptt.TimeOutTimerLimit);
        IsToggleMode = link.Ptt.Mode == PttMode.Toggle;
        PttModeLabel = IsToggleMode ? "Toggle (Click)" : "Momentary (Hold)";

        link.Session.StateChanged += HandleSessionStateChanged;
        link.Session.RemoteKeyedChanged += OnRemoteKeyedChanged;
        link.Session.NodeMessageReceived += OnNodeMessage;
        link.Ptt.PttStateChanged += OnPttStateChanged;
        link.Ptt.TimeOutTimerExceeded += OnTimeOutExceeded;
        link.Ptt.WarningLogged += OnPttWarning;
        link.Audio.InputLevelChanged += OnInputLevel;
        link.Audio.OutputLevelChanged += OnOutputLevel;

        SessionState = link.Session.State;
        UpdateConnectionBadge();
    }

    private void DetachLink()
    {
        var link = _link;
        if (link == null) return;

        link.Session.StateChanged -= HandleSessionStateChanged;
        link.Session.RemoteKeyedChanged -= OnRemoteKeyedChanged;
        link.Session.NodeMessageReceived -= OnNodeMessage;
        link.Ptt.PttStateChanged -= OnPttStateChanged;
        link.Ptt.TimeOutTimerExceeded -= OnTimeOutExceeded;
        link.Ptt.WarningLogged -= OnPttWarning;
        link.Audio.InputLevelChanged -= OnInputLevel;
        link.Audio.OutputLevelChanged -= OnOutputLevel;
    }

    // --- Link event handlers (raised on network/audio threads) ---

    private void HandleSessionStateChanged(Iax2SessionState state) => OnUi(() =>
    {
        SessionState = state;
        UpdateConnectionBadge();
        LogActivity($"IAX2 Session State: {ConnectionStateBadgeText}");
    });

    private void UpdateConnectionBadge()
    {
        if (IsReconnecting && SessionState == Iax2SessionState.Disconnected)
        {
            ConnectionStateBadgeText = $"RECONNECTING ({_runtime.Reconnect.Attempt})";
            OnPropertyChanged(nameof(CanConnect));
            OnPropertyChanged(nameof(CanDisconnect));
            return;
        }

        ConnectionStateBadgeText = SessionState switch
        {
            Iax2SessionState.Connected => "CONNECTED",
            Iax2SessionState.Connecting => "CONNECTING...",
            Iax2SessionState.CallTokenHandshake => "HANDSHAKE...",
            Iax2SessionState.Authenticating => "AUTHENTICATING...",
            Iax2SessionState.Disconnecting => "DISCONNECTING...",
            _ => "DISCONNECTED"
        };

        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsConnecting));
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanDisconnect));
    }

    private void OnRemoteKeyedChanged(bool keyed) => OnUi(() =>
    {
        IsRemoteKeyed = keyed;
        LogActivity(keyed ? "<<< Remote Node Keyed (RX Active)" : "<<< Remote Node Unkeyed");
    });

    private void OnPttStateChanged(bool active, PttSource source) => OnUi(() =>
    {
        IsTransmitting = active;
        if (active)
        {
            _idReminder.TransmissionStarted();
            LogActivity($">>> Local PTT Keyed via {source}");
        }
        else
        {
            LogActivity(">>> Local PTT Unkeyed");
            TxDurationDisplay = "00:00";
            TotCounterText = FormatTot(TimeSpan.Zero, TotLimit);
            TotProgressPercent = 0;
        }
    });

    private void OnTimeOutExceeded() =>
        LogActivity("[TOT EXCEEDED] Push-To-Talk auto-unkeyed to prevent transmitter lockup.");

    private void OnPttWarning(string msg) => LogActivity($"[PTT WARNING] {msg}");

    private void OnNodeMessage(string msg) => LogActivity($"[NODE MSG] {msg}");

    private void OnInputLevel(AudioLevels l) => _micMeter = ToMeter(l);

    private void OnOutputLevel(AudioLevels l) => _rxMeter = ToMeter(l);

    /// <summary>Maps RMS level -60..0 dBFS onto 0..1 so ordinary speech moves the bar sensibly.</summary>
    private static float ToMeter(AudioLevels l) => (float)Math.Clamp((l.RmsDbfs + 60.0) / 60.0, 0.0, 1.0);

    private void OnUiTimerTick()
    {
        var link = _link;

        if (IsTransmitting && link != null)
        {
            var duration = link.Ptt.CurrentTxDuration;
            TxDurationDisplay = $"{duration.Minutes:D2}:{duration.Seconds:D2}";
            TotCounterText = FormatTot(duration, link.Ptt.TimeOutTimerLimit);
            TotProgressPercent = Math.Clamp(duration.TotalSeconds / link.Ptt.TimeOutTimerLimit.TotalSeconds, 0.0, 1.0);
        }

        MicAudioLevel = _micMeter;
        RxAudioLevel = _rxMeter;

        if (IsConnected && link != null)
        {
            LatencyText = link.Session.RoundTripTime > TimeSpan.Zero
                ? $"{link.Session.RoundTripTime.TotalMilliseconds:F0} ms"
                : "-- ms";
        }
    }

    // --- helpers ---

    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    private void LogActivity(string text)
    {
        string entry = $"[{DateTime.Now:HH:mm:ss}] {text}";

        OnUi(() =>
        {
            ActivityLog.Insert(0, entry);
            while (ActivityLog.Count > 100) ActivityLog.RemoveAt(ActivityLog.Count - 1);
        });
    }
}
