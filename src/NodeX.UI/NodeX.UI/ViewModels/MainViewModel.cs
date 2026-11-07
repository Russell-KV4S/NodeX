using System;
using System.Collections.Generic;
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

/// <summary>A saved favourite: the node number and its friendly name (empty when none was given).</summary>
public sealed record FavoriteItem(string Node, string Label)
{
    public bool HasLabel => Label.Length > 0;

    /// <summary>Main line of the quick-recall button: the name, or the number when there is no name.</summary>
    public string Title => HasLabel ? Label : Node;

    public string AutomationName => HasLabel ? $"Connect to {Label}, node {Node}" : $"Connect to node {Node}";
}

/// <summary>
/// Main console. All real work is delegated to <see cref="NodeRuntime"/> (registration, the active
/// <see cref="NodeLink"/>); this class only translates runtime events into bindable state on the UI thread.
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    private const string NotSet = "not set";

    private readonly NodeRuntime _runtime;
    private DispatcherTimer? _timer;
    private volatile float _micMeter;   // written on audio threads, applied by the UI timer
    private DateTime? _backgroundedAt;

    /// <summary>Per-link event hookups and receive meter; touched on the UI thread only.</summary>
    private readonly Dictionary<NodeLink, LinkWatch> _watches = [];

    private sealed class LinkWatch
    {
        public volatile float RxLevel;   // written on that link's playout thread
        public Action Detach = static () => { };
    }

    /// <summary>The link that transmits (the runtime's transmit link), or null.</summary>
    private NodeLink? TxLink => _runtime.Link;

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

    // --- Exit and Back ---
    public bool CanExit => AppServices.CanExit;

    /// <summary>Raised on the UI thread after everything has been stopped; the app shell ends the process.</summary>
    public event Action? ExitRequested;

    /// <summary>"Exit NodeX": hang up, stop registering, stop all background work, then close the app.</summary>
    [RelayCommand]
    public async Task ExitAsync()
    {
        LogActivity("Exiting NodeX…");
        try
        {
            await _runtime.ExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            LogActivity($"[ERROR] Exit: {ex.Message}");
        }
        OnUi(() => ExitRequested?.Invoke());
    }

    /// <summary>
    /// Back button: closes the top full-screen panel if one is open and returns true; otherwise returns false so
    /// the head can leave the app running in the background (Back is not Exit).
    /// </summary>
    public bool CloseTopOverlay()
    {
        if (IsSearchOpen) { IsSearchOpen = false; return true; }
        if (IsSettingsOpen) { IsSettingsOpen = false; return true; }
        return false;
    }

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
    private Task<bool> SetFavoriteAsync(string node, bool favorite) =>
        UpdateFavoriteAsync(node, favorite, favorite ? SuggestLabel(node) : null);

    /// <summary>A starting name for a node chosen from "Find a node": its frequency/name, else its callsign.</summary>
    private string? SuggestLabel(string node)
    {
        var hit = Search.Results.FirstOrDefault(r => r.NodeNumber == node)?.Node;
        if (hit == null) return null;
        string text = hit.Description.Trim();
        if (text.Length == 0) text = hit.Callsign.Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// Adds, removes or renames a favourite. <paramref name="label"/> replaces the stored name when non-empty; an
    /// empty or null label keeps the name already saved.
    /// </summary>
    private async Task<bool> UpdateFavoriteAsync(string node, bool favorite, string? label)
    {
        var cfg = _runtime.Config;
        if (!cfg.IsComplete || cfg.Password == null)
        {
            LogActivity("[ERROR] Finish your Settings before saving favorites.");
            return false;
        }

        var list = cfg.Settings.FavoriteNodes.ToList();
        var labels = new Dictionary<string, string>(cfg.Settings.FavoriteLabels);
        if (favorite)
        {
            if (!list.Contains(node)) list.Add(node);
            if (!string.IsNullOrWhiteSpace(label)) labels[node] = label.Trim();
        }
        else
        {
            list.Remove(node);
            labels.Remove(node);
        }

        try
        {
            var result = await _runtime.ConfigService.SaveAsync(
                cfg.Settings with { FavoriteNodes = list, FavoriteLabels = labels }, cfg.Password);
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
    public partial string ConnectionStateBadgeText { get; set; } = "DISCONNECTED";

    [ObservableProperty]
    public partial string LatencyText { get; set; } = "-- ms";

    // --- Linked nodes: up to NodeRuntime.MaxLinks at once, one of them the transmit node ---

    /// <summary>One row per linked node, in the order they were added.</summary>
    public ObservableCollection<LinkItemViewModel> LinkItems { get; } = [];

    public bool HasLinks => LinkItems.Count > 0;
    public bool HasMultipleLinks => LinkItems.Count > 1;

    /// <summary>"2 / 4 linked".</summary>
    public string LinkCountText => $"{LinkItems.Count} / {NodeRuntime.MaxLinks} linked";

    /// <summary>"Disconnect" for one link, "Disconnect all" for several.</summary>
    public string DisconnectAllText => HasMultipleLinks ? "Disconnect all" : "Disconnect";

    /// <summary>The transmit node's link is up.</summary>
    public bool IsConnected => TxLink?.Session.State == Iax2SessionState.Connected;

    /// <summary>True while the runtime is automatically re-dialling a lost link.</summary>
    [ObservableProperty]
    public partial bool IsReconnecting { get; set; }

    partial void OnIsReconnectingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanDisconnect));
    }

    /// <summary>Connect adds a link, so it stays available until <see cref="NodeRuntime.MaxLinks"/> are linked.</summary>
    public bool CanConnect => LinkItems.Count < NodeRuntime.MaxLinks;
    public bool CanDisconnect => HasLinks || IsReconnecting;

    // --- Transmit permission: listen-only or microphone denied. Listening always works. ---
    public bool CanTransmit => _runtime.Transmit.CanTransmit;
    public bool IsTransmitBlocked => !CanTransmit;
    public string TransmitBlockedText => _runtime.Transmit.Reason;

    /// <summary>Several nodes are linked and none has been chosen to transmit to (so the PTT key stays inert).</summary>
    public bool NeedsTransmitNode => CanTransmit && HasLinks && _runtime.TransmitNode == null;

    /// <summary>The PTT key works: transmitting is allowed and there is a transmit node (or nothing linked yet).</summary>
    public bool CanKey => CanTransmit && !NeedsTransmitNode;

    public string PttHeadline => !CanTransmit ? "LISTEN ONLY" : NeedsTransmitNode ? "CHOOSE TX NODE" : "PUSH TO TALK";

    public string PttCaption =>
        _runtime.Transmit.Block == TransmitBlock.MicrophoneDenied ? "MIC OFF"
        : !CanTransmit ? "NO TX"
        : NeedsTransmitNode ? "NO TX NODE"
        : _runtime.TransmitNode is { } tx && HasMultipleLinks ? $"TO NODE {tx}"
        : "MIC";

    /// <summary>True when the OS denied the microphone and the platform can show its permission prompt.</summary>
    public bool CanFixMicrophone =>
        _runtime.Transmit.Block == TransmitBlock.MicrophoneDenied && AppServices.MicrophonePermissionRequester != null;

    /// <summary>True when transmitting is off only because no callsign has been entered.</summary>
    public bool CanAddCallsign => _runtime.Transmit.Block == TransmitBlock.ListenOnly;

    private void RefreshTransmit()
    {
        OnPropertyChanged(nameof(CanTransmit));
        OnPropertyChanged(nameof(CanKey));
        OnPropertyChanged(nameof(NeedsTransmitNode));
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

    /// <summary>The log as one block of text (newest first, as listed) so it can be selected and copied in part.</summary>
    public string LogText => string.Join(Environment.NewLine, ActivityLog);
    public ObservableCollection<FavoriteItem> FavoriteNodes { get; } = [];
    public bool HasFavorites => FavoriteNodes.Count > 0;

    // --- Favourite the node typed in the connect box ---

    /// <summary>True when the node in the connect box is saved as a favourite.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetFavoriteGlyph), nameof(TargetFavoriteName))]
    public partial bool TargetIsFavorite { get; set; }

    /// <summary>The friendly name being edited for the favourite in the connect box.</summary>
    [ObservableProperty]
    public partial string FavoriteLabelInput { get; set; } = "";

    public string TargetFavoriteGlyph => TargetIsFavorite ? "★" : "☆";
    public string TargetFavoriteName => TargetIsFavorite ? "Remove this node from favorites" : "Add this node to favorites";

    private bool TargetIsValid => NodeSettingsValidator.IsValidNodeNumber(TargetNodeInput?.Trim());

    partial void OnTargetNodeInputChanged(string value) => RefreshTargetFavorite();

    private void RefreshTargetFavorite()
    {
        string node = TargetNodeInput?.Trim() ?? "";
        var item = FavoriteNodes.FirstOrDefault(f => f.Node == node);
        TargetIsFavorite = item != null;
        FavoriteLabelInput = item?.Label ?? "";
    }

    [RelayCommand]
    public async Task ToggleTargetFavoriteAsync()
    {
        string node = TargetNodeInput?.Trim() ?? "";
        if (!NodeSettingsValidator.IsValidNodeNumber(node))
        {
            LogActivity("[ERROR] Enter a valid node number (3 to 7 digits) to favorite it.");
            return;
        }

        bool want = !TargetIsFavorite;
        string? label = null;
        if (want)
        {
            label = FavoriteLabelInput;
            if (string.IsNullOrWhiteSpace(label))
            {
                var info = _runtime.Connections.FirstOrDefault(c => c.Node == node)?.Info;
                label = SuggestLabel(node) ?? (info == null ? null
                    : info.Description.Trim().Length > 0 ? info.Description.Trim() : info.Callsign.Trim());
            }
        }
        if (await UpdateFavoriteAsync(node, want, label))
            LogActivity(want ? $"Node {node} added to favorites." : $"Node {node} removed from favorites.");
    }

    /// <summary>Removes a favourite chip (right-click / long-press menu).</summary>
    [RelayCommand]
    public async Task RemoveFavoriteAsync(string? node)
    {
        if (string.IsNullOrEmpty(node)) return;
        if (await UpdateFavoriteAsync(node, favorite: false, label: null))
            LogActivity($"Node {node} removed from favorites.");
    }

    /// <summary>Saves the name typed for the favourite in the connect box (adding it as a favourite if it is not one yet).</summary>
    [RelayCommand]
    public async Task SaveFavoriteLabelAsync()
    {
        string node = TargetNodeInput?.Trim() ?? "";
        if (!NodeSettingsValidator.IsValidNodeNumber(node)) return;
        if (await UpdateFavoriteAsync(node, favorite: true, FavoriteLabelInput))
            LogActivity($"Saved favorite {node}{(FavoriteLabelInput.Trim().Length > 0 ? $" as \"{FavoriteLabelInput.Trim()}\"" : "")}.");
    }

    // --- Reordering favourites by dragging (the view moves items live, then saves once on drop) ---

    /// <summary>Moves a favourite in the on-screen list only; <see cref="SaveFavoriteOrderAsync"/> persists it.</summary>
    public void MoveFavorite(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= FavoriteNodes.Count || to >= FavoriteNodes.Count) return;
        FavoriteNodes.Move(from, to);
    }

    /// <summary>Saves the order the favourites are currently shown in (names are unchanged).</summary>
    public async Task SaveFavoriteOrderAsync()
    {
        var cfg = _runtime.Config;
        if (!cfg.IsComplete || cfg.Password == null) return;

        var order = FavoriteNodes.Select(f => f.Node).ToList();
        if (order.SequenceEqual(cfg.Settings.FavoriteNodes)) return;

        try
        {
            await _runtime.ConfigService.SaveAsync(cfg.Settings with { FavoriteNodes = order }, cfg.Password);
        }
        catch (Exception ex)
        {
            LogActivity($"[ERROR] Could not save favorite order: {ex.Message}");
        }
    }

    // --- Audio route (phone earpiece / speaker), where the platform can switch it ---

    public bool CanSwitchSpeaker => AppServices.SpeakerphoneSetter != null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeakerButtonText), nameof(SpeakerChipText))]
    public partial bool IsSpeakerOn { get; set; }

    /// <summary>Short two-line label for the chip beside the PTT key.</summary>
    public string SpeakerChipText => IsSpeakerOn ? "🔊" + Environment.NewLine + "Speaker" : "📞" + Environment.NewLine + "Phone";

    public string SpeakerButtonText => IsSpeakerOn ? "🔊 Speaker — tap for phone" : "📞 Phone — tap for speaker";

    [RelayCommand]
    public void ToggleSpeaker()
    {
        var setter = AppServices.SpeakerphoneSetter;
        if (setter == null) return;

        bool want = !IsSpeakerOn;
        if (setter(want))
        {
            IsSpeakerOn = want;
            LogActivity(want ? "Audio: loudspeaker." : "Audio: phone earpiece / headset.");
        }
        else
        {
            LogActivity("[ERROR] Could not switch the audio output.");
        }
    }

    /// <summary>The event log as plain text, oldest line first (for the clipboard).</summary>
    public string GetLogText() => string.Join(Environment.NewLine, ActivityLog.Reverse());

    public void NoteLogCopied() => LogActivity($"Event log copied ({ActivityLog.Count} lines).");

    /// <summary>Keys of the DTMF pad in display order (3 columns).</summary>
    public string[] DtmfKeys { get; } = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "*", "0", "#"];

    /// <summary>"elapsed / limit" for the transmit time-out timer, using the configured limit.</summary>
    [ObservableProperty]
    public partial string TotCounterText { get; set; } = "00:00 / 03:00";

    private TimeSpan TotLimit =>
        TxLink?.Ptt.TimeOutTimerLimit ?? TimeSpan.FromSeconds(_runtime.Config.Settings.TimeOutTimerSeconds);

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
        AppServices.HardwarePtt.Pressed += OnHardwarePtt;

        Search = new NodeSearchViewModel(
            directory ?? AppServices.Directory,
            () => FavoriteNodes.Select(f => f.Node).ToList(),
            SetFavoriteAsync);
        Search.Closed += () => IsSearchOpen = false;
        Search.NodeChosen += node =>
        {
            IsSearchOpen = false;
            _ = ConnectToAsync(node);   // "Use" dials straight away
        };

        Settings = new SettingsViewModel(runtime.ConfigService);
        Settings.Saved += OnSettingsSaved;
        Settings.Cancelled += () => IsSettingsOpen = false;

        runtime.RegistrationChanged += () => OnUi(ApplyRegistration);
        runtime.LinkAdded += link => OnUi(() => AttachLink(link));
        runtime.LinkRemoved += link => OnUi(() => DetachLink(link));
        runtime.ConnectionsChanged += () => OnUi(RefreshLinks);
        runtime.ErrorOccurred += msg => LogActivity($"[ERROR] {msg}");
        runtime.Notice += msg => LogActivity(msg);
        runtime.ReconnectChanged += status => OnUi(() => ApplyReconnect(status));
        runtime.ConfigService.Changed += cfg => OnUi(() => ApplyConfig(cfg));
        runtime.Transmit.Changed += () => OnUi(RefreshTransmit);
        // The OS audio route is reset when nothing is linked any more, so the button must say "phone" again.
        // When a link comes up, start on the loudspeaker if the user chose that as the default.
        runtime.LinkActiveChanged += (active, _) => OnUi(() =>
        {
            if (!active) { IsSpeakerOn = false; return; }
            if (!IsSpeakerOn && runtime.Config.Settings.DefaultToSpeaker && AppServices.SpeakerphoneSetter is { } setter && setter(true))
            {
                IsSpeakerOn = true;
                LogActivity("Audio: loudspeaker (default).");
            }
        });

        ActivityLog.CollectionChanged += (_, _) => OnPropertyChanged(nameof(LogText));

        ApplyConfig(runtime.Config);
        ApplyRegistration();
    }

    /// <summary>
    /// Battery: the 100 ms meter/timer refresh is only useful while the screen is showing. Mobile heads call
    /// this from the app's activated/deactivated events; the link, audio and reconnect logic are unaffected.
    /// </summary>
    public void SetForeground(bool foreground)
    {
        if (foreground)
        {
            // Coming back after a while: the OS may have throttled us, so re-register now instead of waiting
            // for the next beat (a lapsed registration costs minutes before nodes will accept our link).
            if (_backgroundedAt is { } since && DateTime.UtcNow - since > TimeSpan.FromSeconds(30))
                _runtime.NotifyResumed();
            _backgroundedAt = null;
        }
        else
        {
            _backgroundedAt ??= DateTime.UtcNow;
        }

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

    /// <summary>Puts <paramref name="node"/> in the connect box and dials it (favourite tapped, or "Use" in Find a node).</summary>
    private async Task ConnectToAsync(string node)
    {
        TargetNodeInput = node;
        if (LinkItems.Any(l => l.Node == node && l.IsConnected))
        {
            LogActivity($"Already connected to node {node}.");
            return;
        }
        await ConnectNodeAsync();
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

            LogActivity($"Connecting to {info.NodeNumber} at {info.EndPoint}...");
        }
        catch (Exception ex)
        {
            LogActivity($"[ERROR] Connection failed: {ex.Message}");
        }
    }

    /// <summary>Hangs up every linked node.</summary>
    [RelayCommand]
    public async Task DisconnectNodeAsync()
    {
        try
        {
            LogActivity(HasMultipleLinks ? "Disconnecting from all nodes..." : "Disconnecting from remote node...");
            await _runtime.DisconnectAsync();
        }
        catch (Exception ex)
        {
            LogActivity($"[ERROR] Disconnect error: {ex.Message}");
        }
    }

    private async void DisconnectOne(string node)
    {
        try
        {
            LogActivity($"Disconnecting from node {node}...");
            await _runtime.DisconnectAsync(node);
        }
        catch (Exception ex)
        {
            LogActivity($"[ERROR] Disconnect error: {ex.Message}");
        }
    }

    private void SelectTransmitNode(string node) => _runtime.SetTransmitNode(node);

    private void ToggleMuteNode(string node)
    {
        bool muted = LinkItems.FirstOrDefault(l => l.Node == node)?.IsMuted ?? false;
        _runtime.SetMuted(node, !muted);
        LogActivity(muted ? $"Node {node} audio on." : $"Node {node} muted (still linked).");
    }

    [RelayCommand]
    public async Task PttDownAsync()
    {
        if (!IsToggleMode && TxLink is { } link) await link.Ptt.HandleUiPttAsync(true);
    }

    [RelayCommand]
    public async Task PttUpAsync()
    {
        if (!IsToggleMode && TxLink is { } link) await link.Ptt.HandleUiPttAsync(false);
    }

    [RelayCommand]
    public async Task TogglePttClickAsync()
    {
        if (IsToggleMode && TxLink is { } link) await link.Ptt.HandleUiPttAsync(true);
    }

    /// <summary>A mapped phone button went down or up (Android volume key, headset button...).</summary>
    private void OnHardwarePtt(bool pressed)
    {
        var link = TxLink;
        if (link == null) return;
        _ = link.Ptt.HandleButtonAsync(pressed, PttSource.MediaKey);
    }

    [RelayCommand]
    public void TogglePttMode()
    {
        IsToggleMode = !IsToggleMode;
        foreach (var link in _runtime.Links)
            link.Ptt.Mode = IsToggleMode ? PttMode.Toggle : PttMode.Momentary;
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
        // DTMF commands act on the node you are talking to, so they go to the transmit node only.
        var link = TxLink;
        if (link == null || !IsConnected)
        {
            LogActivity(HasLinks && _runtime.TransmitNode == null
                ? "[DTMF] Choose a transmit node first (commands go to that node)."
                : "[DTMF] Not connected.");
            return;
        }

        LogActivity($"[DTMF] Sending '{sequence}' to node {link.Target.NodeNumber}");
        foreach (char c in sequence)
        {
            await link.Session.SendDtmfAsync(c);
            await Task.Delay(120);   // let the far end decode each tone before the next one
        }
    }

    [RelayCommand]
    public async Task SelectFavoriteNodeAsync(string node)
    {
        if (!string.IsNullOrEmpty(node)) await ConnectToAsync(node);
    }

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
        foreach (string node in s.FavoriteNodes)
            FavoriteNodes.Add(new FavoriteItem(node, s.FavoriteLabels.TryGetValue(node, out var label) ? label : ""));
        OnPropertyChanged(nameof(HasFavorites));
        if (!IsTransmitting) TotCounterText = FormatTot(TimeSpan.Zero, TotLimit);

        if (string.IsNullOrWhiteSpace(TargetNodeInput) && s.DefaultTargetNode.Length > 0)
            TargetNodeInput = s.DefaultTargetNode;
        RefreshTargetFavorite();

        AppServices.HardwarePtt.MappedKey = s.HardwarePttKey;

        if (_watches.Count == 0)
        {
            IsToggleMode = s.TogglePtt;
            PttModeLabel = IsToggleMode ? "Toggle (Click)" : "Momentary (Hold)";
        }

        RefreshLinks();   // favourite names appear in the link rows
    }

    private void ApplyRegistration()
    {
        IsAslRegistered = _runtime.IsRegistered;
        AslStatusText = _runtime.IsRegistered
            ? _runtime.RegisteredSince is { } since
                ? $"ASL Registered · {DurationText.Format(DateTimeOffset.UtcNow - since)}"
                : "ASL Registered"
            : _runtime.RegistrationStatus;
    }

    private void ApplyReconnect(ReconnectStatus status)
    {
        IsReconnecting = status.Active;
        RefreshLinks();
    }

    private bool IsFavoriteNode(string node) => FavoriteNodes.Any(f => f.Node == node);

    /// <summary>Star on a linked-node card. A new favourite is named after what the node reported about itself.</summary>
    private async void ToggleFavoriteNode(string node)
    {
        bool want = !IsFavoriteNode(node);
        string? label = null;
        if (want && _runtime.Connections.FirstOrDefault(c => c.Node == node)?.Info is { } info)
        {
            label = info.Description.Trim();
            if (label.Length == 0) label = info.Callsign.Trim();
        }
        if (await UpdateFavoriteAsync(node, want, label))
            LogActivity(want ? $"Node {node} added to favorites." : $"Node {node} removed from favorites.");
    }

    /// <summary>Rename box on a linked-node card; an empty name keeps the saved one.</summary>
    private async void SaveFavoriteNodeLabel(string node, string label)
    {
        if (await UpdateFavoriteAsync(node, favorite: true, label))
            LogActivity($"Saved favorite {node}{(label.Trim().Length > 0 ? $" as \"{label.Trim()}\"" : "")}.");
    }

    private string LabelFor(string node) =>
        _runtime.Config.Settings.FavoriteLabels.TryGetValue(node, out var label) ? label : "";

    /// <summary>"[node] " when more than one node is linked, so log lines say which link they are about.</summary>
    private string Tag(string node) => _runtime.Connections.Count > 1 ? $"[{node}] " : "";

    /// <summary>
    /// Brings the link rows, the hero card and the PTT state in line with the runtime's connections.
    /// Runs on the UI thread whenever <see cref="NodeRuntime.ConnectionsChanged"/> fires.
    /// </summary>
    private void RefreshLinks()
    {
        var conns = _runtime.Connections;

        for (int i = LinkItems.Count - 1; i >= 0; i--)
        {
            string node = LinkItems[i].Node;
            if (!conns.Any(c => c.Node == node)) LinkItems.RemoveAt(i);
        }

        for (int i = 0; i < conns.Count; i++)
        {
            var c = conns[i];
            int at = -1;
            for (int j = 0; j < LinkItems.Count; j++)
                if (LinkItems[j].Node == c.Node) { at = j; break; }

            if (at < 0)
                LinkItems.Insert(Math.Min(i, LinkItems.Count),
                    new LinkItemViewModel(c.Node, LabelFor, SelectTransmitNode, ToggleMuteNode, DisconnectOne,
                        IsFavoriteNode, ToggleFavoriteNode, SaveFavoriteNodeLabel));
            else if (at != i)
                LinkItems.Move(at, i);

            LinkItems[i].Refresh(c);
        }

        OnPropertyChanged(nameof(HasLinks));
        OnPropertyChanged(nameof(HasMultipleLinks));
        OnPropertyChanged(nameof(LinkCountText));
        OnPropertyChanged(nameof(DisconnectAllText));
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanDisconnect));
        OnPropertyChanged(nameof(IsConnected));

        IsRemoteKeyed = LinkItems.Any(l => l.IsReceiving);
        IsTransmitting = _runtime.Links.Any(l => l.Ptt.IsPttActive);
        RefreshTransmit();
        RefreshHero(conns);
    }

    /// <summary>Reconnect status with a live countdown ("in 42 s") while a retry is waiting.</summary>
    private static string ReconnectText(NodeConnection c)
    {
        var r = c.Reconnect;
        if (r.RetryAt is not { } at) return r.Message;
        double left = (at - DateTimeOffset.UtcNow).TotalSeconds;
        return left > 0
            ? $"Reconnecting to node {c.Node} in {Math.Ceiling(left):0} s (attempt {r.Attempt})."
            : $"Reconnecting to node {c.Node} (attempt {r.Attempt})…";
    }

    /// <summary>The hero card shows the transmit node (the one you are talking to) and a hint about the others.</summary>
    private void RefreshHero(IReadOnlyList<NodeConnection> conns)
    {
        if (conns.Count == 0)
        {
            ConnectionStateBadgeText = "DISCONNECTED";
            ConnectedNodeDisplay = "Not Connected";
            ConnectedNodeDetails = "Enter a node number to connect";
            LatencyText = "-- ms";
            return;
        }

        string? txNode = _runtime.TransmitNode;
        var tx = conns.FirstOrDefault(c => c.Node == txNode);
        if (tx == null)
        {
            ConnectionStateBadgeText = $"{conns.Count} LINKED";
            ConnectedNodeDisplay = "No transmit node";
            ConnectedNodeDetails = "Tap “Transmit here” on a linked node to choose where your voice goes.";
            LatencyText = "-- ms";
            return;
        }

        var item = LinkItems.First(l => l.Node == tx.Node);
        ConnectionStateBadgeText = item.StatusText;
        ConnectedNodeDisplay = $"Node {tx.Node}";

        string details = tx.Reconnect.Active && !item.IsConnected ? ReconnectText(tx)
            : tx.Info == null ? "Resolving…"
            : string.IsNullOrWhiteSpace(tx.Info.Callsign)
                ? $"{tx.Info.Description} ({tx.Info.EndPoint.Address})"
                : $"{tx.Info.Callsign} • {tx.Info.Description} ({tx.Info.EndPoint.Address})";
        if (conns.Count > 1)
            details += $" · also listening: {string.Join(", ", conns.Where(c => c != tx).Select(c => c.Node))}";
        ConnectedNodeDetails = details;
    }

    private void AttachLink(NodeLink link)
    {
        if (_watches.ContainsKey(link)) return;

        string node = link.Target.NodeNumber;
        bool first = _watches.Count == 0;
        var watch = new LinkWatch();

        void OnState(Iax2SessionState s) => OnUi(() =>
        {
            LogActivity($"{Tag(node)}IAX2 Session State: {LinkItemViewModel.StateText(s)}");
            RefreshLinks();
        });

        void OnKeyed(bool keyed) => OnUi(() =>
        {
            var item = LinkItems.FirstOrDefault(l => l.Node == node);
            if (item != null) item.IsReceiving = keyed;
            IsRemoteKeyed = LinkItems.Any(l => l.IsReceiving);
            LogActivity(_runtime.Connections.Count > 1
                ? keyed ? $"<<< Node {node} keyed (RX active)" : $"<<< Node {node} unkeyed"
                : keyed ? "<<< Remote Node Keyed (RX Active)" : "<<< Remote Node Unkeyed");
        });

        void OnMessage(string msg) => LogActivity($"[NODE MSG] {Tag(node)}{msg}");
        void OnPtt(bool active, PttSource source) => OnUi(() => OnPttStateChanged(node, active, source));
        void OnWarning(string msg) => LogActivity($"[PTT WARNING] {Tag(node)}{msg}");
        void OnInput(AudioLevels l) { if (ReferenceEquals(TxLink, link)) _micMeter = ToMeter(l); }
        void OnOutput(AudioLevels l) => watch.RxLevel = ToMeter(l);

        link.Session.StateChanged += OnState;
        link.Session.RemoteKeyedChanged += OnKeyed;
        link.Session.NodeMessageReceived += OnMessage;
        link.Ptt.PttStateChanged += OnPtt;
        link.Ptt.TimeOutTimerExceeded += OnTimeOutExceeded;
        link.Ptt.WarningLogged += OnWarning;
        link.Audio.InputLevelChanged += OnInput;
        link.Audio.OutputLevelChanged += OnOutput;

        watch.Detach = () =>
        {
            link.Session.StateChanged -= OnState;
            link.Session.RemoteKeyedChanged -= OnKeyed;
            link.Session.NodeMessageReceived -= OnMessage;
            link.Ptt.PttStateChanged -= OnPtt;
            link.Ptt.TimeOutTimerExceeded -= OnTimeOutExceeded;
            link.Ptt.WarningLogged -= OnWarning;
            link.Audio.InputLevelChanged -= OnInput;
            link.Audio.OutputLevelChanged -= OnOutput;
        };
        _watches[link] = watch;

        if (first)
        {
            // The first link adopts the mode it was created with (from Settings, or what the user last chose).
            TotCounterText = FormatTot(TimeSpan.Zero, link.Ptt.TimeOutTimerLimit);
            IsToggleMode = link.Ptt.Mode == PttMode.Toggle;
            PttModeLabel = IsToggleMode ? "Toggle (Click)" : "Momentary (Hold)";
        }
        else
        {
            link.Ptt.Mode = IsToggleMode ? PttMode.Toggle : PttMode.Momentary;
        }

        RefreshLinks();
    }

    private void DetachLink(NodeLink link)
    {
        if (_watches.Remove(link, out var watch)) watch.Detach();

        if (_watches.Count == 0)
        {
            _idReminder.Reset();
            IsTransmitting = false;
            IsRemoteKeyed = false;
            TxDurationDisplay = "00:00";
            TotProgressPercent = 0;
            TotCounterText = FormatTot(TimeSpan.Zero, TotLimit);
            _micMeter = 0;
        }

        RefreshLinks();
    }

    // --- Link event handlers (marshalled to the UI thread by the callers) ---

    private void OnPttStateChanged(string node, bool active, PttSource source)
    {
        IsTransmitting = _runtime.Links.Any(l => l.Ptt.IsPttActive);
        string where = _runtime.Connections.Count > 1 ? $" to node {node}" : "";
        if (active)
        {
            _idReminder.TransmissionStarted();
            LogActivity($">>> Local PTT Keyed via {source}{where}");
        }
        else
        {
            LogActivity(">>> Local PTT Unkeyed");
            if (IsTransmitting) return;   // another link is still keyed (cannot normally happen)
            TxDurationDisplay = "00:00";
            TotCounterText = FormatTot(TimeSpan.Zero, TotLimit);
            TotProgressPercent = 0;
        }
    }

    private void OnTimeOutExceeded() =>
        LogActivity("[TOT EXCEEDED] Push-To-Talk auto-unkeyed to prevent transmitter lockup.");

    /// <summary>Maps RMS level -60..0 dBFS onto 0..1 so ordinary speech moves the bar sensibly.</summary>
    private static float ToMeter(AudioLevels l) => (float)Math.Clamp((l.RmsDbfs + 60.0) / 60.0, 0.0, 1.0);

    private int _tick;

    private void OnUiTimerTick()
    {
        var link = TxLink;

        if (IsTransmitting && link != null)
        {
            var duration = link.Ptt.CurrentTxDuration;
            TxDurationDisplay = $"{duration.Minutes:D2}:{duration.Seconds:D2}";
            TotCounterText = FormatTot(duration, link.Ptt.TimeOutTimerLimit);
            TotProgressPercent = Math.Clamp(duration.TotalSeconds / link.Ptt.TimeOutTimerLimit.TotalSeconds, 0.0, 1.0);
        }

        MicAudioLevel = _micMeter;

        // The speaker meter shows the loudest linked node.
        float rx = 0;
        foreach (var w in _watches.Values) rx = Math.Max(rx, w.RxLevel);
        RxAudioLevel = rx;

        if (IsConnected && link != null)
        {
            LatencyText = link.Session.RoundTripTime > TimeSpan.Zero
                ? $"{link.Session.RoundTripTime.TotalMilliseconds:F0} ms"
                : "-- ms";
        }

        // Once a second is plenty for round-trip times and the "registered for ..." badge.
        if (_tick % 10 == 0 && _runtime.IsRegistered) ApplyRegistration();
        if (_tick % 10 == 0 && IsReconnecting) RefreshHero(_runtime.Connections);
        if (++_tick % 10 == 0 && LinkItems.Count > 0)
        {
            foreach (var c in _runtime.Connections)
                LinkItems.FirstOrDefault(l => l.Node == c.Node)?.RefreshRtt(c);
        }
    }

    // --- helpers ---

    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    /// <summary>
    /// Makes remote text safe to log and copy. Asterisk text frames end with a NUL, and the Windows clipboard
    /// ends text at the first NUL, so Copy silently dropped everything logged after such a message. Other control
    /// characters (including line breaks, which would split one entry across lines) become spaces.
    /// </summary>
    public static string SanitizeLogText(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var sb = new System.Text.StringBuilder(text.Length);
        foreach (char ch in text)
        {
            if (ch == '\0') continue;
            sb.Append(char.IsControl(ch) ? ' ' : ch);
        }
        return sb.ToString().Trim();
    }

    private void LogActivity(string text)
    {
        string clean = SanitizeLogText(text);
        string entry = $"[{DateTime.Now:HH:mm:ss}] {clean}";
        NodeX.Core.Diagnostics.DiagnosticLog.Default.Info("event", clean);   // same line, with millisecond time, in the log file

        OnUi(() =>
        {
            ActivityLog.Insert(0, entry);
            while (ActivityLog.Count > 100) ActivityLog.RemoveAt(ActivityLog.Count - 1);
        });
    }
}
