using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NodeX.Core;
using NodeX.Core.Cast;
using NodeX.Core.Settings;

namespace NodeX.UI.ViewModels;

public sealed record CastDeviceItem(CastDevice Device)
{
    public string FriendlyName => Device.FriendlyName;
    public string Subtitle => Device.DisplaySubtitle;
    public string KindBadge => Device.HasScreen ? "TV / Display" : "Audio Speaker";
}

/// <summary>Chromecast discovery, selection, volume, local-speaker mute, and active cast controls.</summary>
public partial class CastViewModel : ViewModelBase, IDisposable
{
    private readonly NodeRuntime _runtime;
    private readonly CastSessionManager _manager;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _volCts;
    private bool _syncingFromManager;
    private bool _disposed;

    public CastViewModel(NodeRuntime runtime, CastSessionManager? manager = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _manager = manager ?? new CastSessionManager(runtime);
        _manager.StateChanged += OnManagerStateChanged;
        _runtime.ConfigService.Changed += OnConfigChanged;
        Devices.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasDevices));
            OnPropertyChanged(nameof(ShowEmptyDevices));
        };
        SyncFromManager();
    }

    public ObservableCollection<CastDeviceItem> Devices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyDevices))]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeBadgeText), nameof(MuteTvButtonText))]
    public partial bool IsCasting { get; set; }

    [ObservableProperty]
    public partial bool IsConnecting { get; set; }

    [ObservableProperty]
    public partial string ActiveDeviceName { get; set; } = "";

    [ObservableProperty]
    public partial string ActiveDeviceSubtitle { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeBadgeText))]
    public partial bool IsCustomDashboardMode { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Select a Chromecast below to cast screen and sound.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorText { get; set; } = "";

    public bool HasError => ErrorText.Length > 0;

    [ObservableProperty]
    public partial double TvVolume { get; set; } = 1.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MuteTvButtonText))]
    public partial bool IsTvMuted { get; set; }

    [ObservableProperty]
    public partial bool MuteLocalSpeaker { get; set; } = true;

    [ObservableProperty]
    public partial string ManualAddressInput { get; set; } = "";

    public bool HasCustomAppId => NodeSettingsValidator.IsValidCastAppId(_runtime.Config.Settings.CastAppId);

    public string CustomAppIdDisplay => HasCustomAppId
        ? _runtime.Config.Settings.CastAppId.Trim().ToUpperInvariant()
        : "Default Media Receiver (CC1AD845)";

    public string ModeBadgeText => IsCustomDashboardMode ? "TV Dashboard + Audio" : "Default Media Receiver";
    public string MuteTvButtonText => IsTvMuted ? "Unmute TV" : "Mute TV";
    public bool HasDevices => Devices.Count > 0;
    public bool ShowEmptyDevices => !IsScanning && Devices.Count == 0;

    public event Action? Closed;
    public event Action? OpenSettingsRequested;
    public event Action? StateChanged;

    public void OnOpened()
    {
        ErrorText = "";
        SyncFromManager();
        if (!IsCasting && !IsScanning && Devices.Count == 0)
            _ = ScanAsync();
    }

    [RelayCommand]
    public void Back() => Closed?.Invoke();

    [RelayCommand]
    public void OpenSettings() => OpenSettingsRequested?.Invoke();

    [RelayCommand]
    public async Task ScanAsync()
    {
        if (_disposed) return;
        ErrorText = "";
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        var cts = _scanCts = new CancellationTokenSource();

        IsScanning = true;
        try
        {
            var found = await _manager.DiscoverAsync(TimeSpan.FromMilliseconds(2400), cts.Token);
            if (_disposed || cts.IsCancellationRequested) return;
            OnUi(() =>
            {
                Devices.Clear();
                foreach (var d in found)
                    Devices.Add(new CastDeviceItem(d));
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            OnUi(() => ErrorText = $"Scan failed: {ex.Message}");
        }
        finally
        {
            OnUi(() => IsScanning = false);
        }
    }

    [RelayCommand]
    public Task ConnectDeviceAsync(CastDeviceItem? item) =>
        item == null ? Task.CompletedTask : StartCastCoreAsync(item.Device);

    [RelayCommand]
    public async Task ConnectManualAsync()
    {
        if (!CastDevice.TryParseManual(ManualAddressInput, out var device, out var error) || device == null)
        {
            ErrorText = error ?? "Enter a valid local IPv4 address.";
            return;
        }
        await StartCastCoreAsync(device);
    }

    [RelayCommand]
    public async Task ToggleTvMuteAsync()
    {
        try
        {
            ErrorText = "";
            await _manager.SetTvMutedAsync(!IsTvMuted);
        }
        catch (Exception ex)
        {
            ErrorText = $"Could not change TV mute: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task StopCastAsync()
    {
        try
        {
            ErrorText = "";
            await _manager.StopCastAsync();
        }
        catch (Exception ex)
        {
            ErrorText = $"Could not stop casting: {ex.Message}";
        }
    }

    partial void OnTvVolumeChanged(double value)
    {
        if (_syncingFromManager || _disposed || !IsCasting) return;
        _volCts?.Cancel();
        _volCts?.Dispose();
        var cts = _volCts = new CancellationTokenSource();
        _ = SetVolumeDebouncedAsync(value, cts.Token);
    }

    partial void OnMuteLocalSpeakerChanged(bool value)
    {
        if (_syncingFromManager || _disposed) return;
        _manager.SetMuteLocalSpeaker(value);
        _ = PersistLocalMuteAsync(value);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = null;
        _volCts?.Cancel();
        _volCts?.Dispose();
        _volCts = null;
        _manager.StateChanged -= OnManagerStateChanged;
        _runtime.ConfigService.Changed -= OnConfigChanged;
    }

    private async Task StartCastCoreAsync(CastDevice device)
    {
        try
        {
            ErrorText = "";
            await _manager.StartCastAsync(device);
        }
        catch (Exception ex)
        {
            OnUi(() => ErrorText = $"Cast failed: {ex.Message}");
        }
    }

    private async Task SetVolumeDebouncedAsync(double level, CancellationToken ct)
    {
        try
        {
            await Task.Delay(80, ct);
            if (!ct.IsCancellationRequested && !_disposed && IsCasting)
                await _manager.SetVolumeAsync(level, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            OnUi(() => ErrorText = $"TV volume error: {ex.Message}");
        }
    }

    private async Task PersistLocalMuteAsync(bool mute)
    {
        try
        {
            var cfg = _runtime.Config;
            if (!cfg.IsComplete || cfg.Password == null || cfg.Settings.CastMuteLocalSpeaker == mute) return;
            await _runtime.ConfigService.SaveAsync(cfg.Settings with { CastMuteLocalSpeaker = mute }, cfg.Password);
        }
        catch
        {
            // Live mute already applied even if settings persistence fails
        }
    }

    private void OnManagerStateChanged() => OnUi(() =>
    {
        SyncFromManager();
        StateChanged?.Invoke();
    });

    private void OnConfigChanged(NodeConfig _) => OnUi(() =>
    {
        OnPropertyChanged(nameof(HasCustomAppId));
        OnPropertyChanged(nameof(CustomAppIdDisplay));
        SyncFromManager();
    });

    private void SyncFromManager()
    {
        _syncingFromManager = true;
        try
        {
            IsCasting = _manager.IsCasting;
            IsConnecting = _manager.IsConnecting;
            ActiveDeviceName = _manager.ActiveDevice?.FriendlyName ?? "";
            ActiveDeviceSubtitle = _manager.ActiveDevice?.DisplaySubtitle ?? "";
            IsCustomDashboardMode = _manager.IsCustomDashboardMode;
            StatusText = IsCasting || IsConnecting
                ? _manager.StatusText
                : "Select a Chromecast below to cast screen and sound.";
            TvVolume = _manager.VolumeLevel;
            IsTvMuted = _manager.IsTvMuted;
            MuteLocalSpeaker = _manager.IsCasting
                ? _manager.MuteLocalSpeaker
                : _runtime.Config.Settings.CastMuteLocalSpeaker;
        }
        finally
        {
            _syncingFromManager = false;
        }
    }

    private void OnUi(Action action)
    {
        if (_disposed) return;
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(() => { if (!_disposed) action(); });
    }
}
