using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NodeX.Core;
using NodeX.Core.Settings;

namespace NodeX.UI.ViewModels;

/// <summary>
/// Station settings screen: node number, node password (kept in OS-protected storage), callsign,
/// favourites, PTT behaviour and a "test registration" action.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly NodeConfigService _config;
    private readonly Func<AslRegistrationConfig, AslRegistrationService> _registrationFactory;

    public SettingsViewModel(
        NodeConfigService config,
        Func<AslRegistrationConfig, AslRegistrationService>? registrationFactory = null)
    {
        _config = config;
        _registrationFactory = registrationFactory ?? (c => new AslRegistrationService(c));
    }

    // ----- station -----

    [ObservableProperty]
    public partial string NodeNumber { get; set; } = "";

    [ObservableProperty]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    public partial bool ShowPassword { get; set; }

    [ObservableProperty]
    public partial string Callsign { get; set; } = "";

    /// <summary>For people without a callsign: connect and listen, PTT is turned off.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CallsignLabel))]
    public partial bool ListenOnly { get; set; }

    public string CallsignLabel => ListenOnly ? "Callsign (optional)" : "Callsign (required to transmit)";

    // ----- connecting -----

    [ObservableProperty]
    public partial string DefaultTargetNode { get; set; } = "";

    /// <summary>Quick-connect nodes, separated by commas, spaces or new lines.</summary>
    [ObservableProperty]
    public partial string FavoritesText { get; set; } = "";

    [ObservableProperty]
    public partial bool RegisterOnStartup { get; set; } = true;

    [ObservableProperty]
    public partial bool AutoReconnect { get; set; } = true;

    // ----- PTT & audio -----

    [ObservableProperty]
    public partial bool TogglePtt { get; set; }

    // ----- hardware PTT button (a phone key) -----

    public bool HardwareKeysSupported => AppServices.HardwareKeysSupported;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HardwareKeyText), nameof(HasHardwareKey))]
    public partial int HardwarePttKey { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HardwareKeyText))]
    public partial bool IsLearningKey { get; set; }

    public bool HasHardwareKey => HardwarePttKey != 0;

    public string HardwareKeyText => IsLearningKey
        ? "Press the button you want to use…"
        : AppServices.NameKey(HardwarePttKey);

    [RelayCommand]
    private void LearnHardwareKey()
    {
        var mapper = AppServices.HardwarePtt;
        mapper.KeyLearned -= OnKeyLearned;
        mapper.LearningChanged -= OnLearningChanged;
        mapper.KeyLearned += OnKeyLearned;
        mapper.LearningChanged += OnLearningChanged;
        mapper.BeginLearn();
    }

    [RelayCommand]
    private void ClearHardwareKey()
    {
        AppServices.HardwarePtt.CancelLearn();
        HardwarePttKey = 0;
    }

    private void OnKeyLearned(int code) => Dispatcher.UIThread.Post(() => HardwarePttKey = code);

    private void OnLearningChanged() => Dispatcher.UIThread.Post(() => IsLearningKey = AppServices.HardwarePtt.IsLearning);

    [ObservableProperty]
    public partial string TimeOutSecondsText { get; set; } = "180";

    [ObservableProperty]
    public partial double MicGain { get; set; } = 1.0;

    [ObservableProperty]
    public partial bool MicProcessing { get; set; } = true;

    // ----- station ID reminder (never transmits anything) -----

    [ObservableProperty]
    public partial bool IdReminderEnabled { get; set; }

    [ObservableProperty]
    public partial string IdReminderMinutesText { get; set; } = "10";

    // ----- advanced -----

    [ObservableProperty]
    public partial string RegistrationUrl { get; set; } = AslRegistrationService.DefaultRegistrationUrl;

    [ObservableProperty]
    public partial string IaxPortText { get; set; } = Iax2Protocol.DefaultPort.ToString(CultureInfo.InvariantCulture);

    // ----- feedback -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);
    public bool HasStatus => !string.IsNullOrEmpty(StatusText);
    public bool IsIdle => !IsBusy;

    /// <summary>Raised after settings were validated and saved.</summary>
    public event Action? Saved;

    /// <summary>Raised when the user dismisses the screen without saving.</summary>
    public event Action? Cancelled;

    private Dictionary<string, string> _favoriteLabels = [];

    /// <summary>Fills the form from the saved configuration.</summary>
    public async Task LoadAsync()
    {
        var cfg = await _config.LoadAsync();
        var s = cfg.Settings;

        NodeNumber = s.NodeNumber;
        Password = cfg.Password ?? "";
        Callsign = s.Callsign;
        ListenOnly = s.ListenOnly;
        DefaultTargetNode = s.DefaultTargetNode;
        FavoritesText = string.Join(", ", s.FavoriteNodes);
        _favoriteLabels = new Dictionary<string, string>(s.FavoriteLabels);
        RegisterOnStartup = s.RegisterOnStartup;
        AutoReconnect = s.AutoReconnect;
        TogglePtt = s.TogglePtt;
        HardwarePttKey = s.HardwarePttKey;
        TimeOutSecondsText = s.TimeOutTimerSeconds.ToString(CultureInfo.InvariantCulture);
        MicGain = s.MicGain;
        MicProcessing = s.MicProcessing;
        IdReminderEnabled = s.IdReminderEnabled;
        IdReminderMinutesText = s.IdReminderMinutes.ToString(CultureInfo.InvariantCulture);
        RegistrationUrl = s.RegistrationUrl;
        IaxPortText = s.IaxPort.ToString(CultureInfo.InvariantCulture);

        ShowPassword = false;
        ErrorText = "";
        StatusText = "";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        StatusText = "";
        if (!TryBuild(out var settings, out var issues))
        {
            ErrorText = Format(issues);
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _config.SaveAsync(settings, Password);
            if (!result.Success)
            {
                ErrorText = Format(result.Issues);
                return;
            }

            ErrorText = "";
            StatusText = "Saved.";
            Saved?.Invoke();
        }
        catch (Exception ex)
        {
            ErrorText = $"Could not save settings: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        AppServices.HardwarePtt.CancelLearn();
        ErrorText = "";
        StatusText = "";
        Cancelled?.Invoke();
    }

    /// <summary>Registers the node right now with the values on screen (nothing is saved).</summary>
    [RelayCommand]
    private async Task TestRegistrationAsync()
    {
        StatusText = "";
        if (!TryBuild(out var settings, out var issues))
        {
            ErrorText = Format(issues);
            return;
        }

        ErrorText = "";
        IsBusy = true;
        StatusText = "Contacting AllStarLink…";
        try
        {
            await using var service = _registrationFactory(
                new AslRegistrationConfig(settings.NodeNumber, Password, settings.IaxPort, settings.RegistrationUrl));
            bool ok = await service.SendHeartbeatAsync();
            if (ok) StatusText = $"Success: {service.LastStatusMessage}";
            else
            {
                StatusText = "";
                ErrorText = service.LastStatusMessage;
            }
        }
        catch (Exception ex)
        {
            StatusText = "";
            ErrorText = $"Registration test failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Builds <see cref="NodeSettings"/> from the form, collecting every problem found.</summary>
    private bool TryBuild(out NodeSettings settings, out List<SettingsIssue> issues)
    {
        issues = [];

        int timeout = 180;
        if (!int.TryParse(TimeOutSecondsText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out timeout))
            issues.Add(new(NodeSettingsValidator.Fields.TimeOutTimerSeconds, "Time-out timer must be a whole number of seconds."));

        int idMinutes = 10;
        if (!int.TryParse(IdReminderMinutesText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out idMinutes))
            issues.Add(new(NodeSettingsValidator.Fields.IdReminderMinutes, "ID reminder interval must be a whole number of minutes."));

        int port = Iax2Protocol.DefaultPort;
        if (!int.TryParse(IaxPortText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port))
            issues.Add(new(NodeSettingsValidator.Fields.IaxPort, "IAX port must be a whole number."));

        settings = new NodeSettings
        {
            NodeNumber = NodeNumber,
            Callsign = Callsign,
            ListenOnly = ListenOnly,
            DefaultTargetNode = DefaultTargetNode,
            FavoriteNodes = FavoritesText
                .Split([',', ' ', ';', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList(),
            FavoriteLabels = _favoriteLabels,   // names are edited on the main screen; Normalize drops any for removed favourites
            RegistrationUrl = RegistrationUrl,
            IaxPort = port,
            RegisterOnStartup = RegisterOnStartup,
            AutoReconnect = AutoReconnect,
            MicGain = (float)MicGain,
            MicProcessing = MicProcessing,
            TimeOutTimerSeconds = timeout,
            TogglePtt = TogglePtt,
            HardwarePttKey = HardwarePttKey,
            IdReminderEnabled = IdReminderEnabled,
            IdReminderMinutes = idMinutes
        };

        // Skip range checks for values that failed to parse; they already have a clearer message.
        var unparsed = issues.Select(p => p.Field).ToHashSet();
        var rules = NodeSettingsValidator.Validate(settings, Password)
            .Where(i => !unparsed.Contains(i.Field))
            .ToList();
        issues.AddRange(rules);

        return issues.Count == 0;
    }

    private static string Format(IEnumerable<SettingsIssue> issues) =>
        string.Join(Environment.NewLine, issues.Select(i => "• " + i.Message));
}
