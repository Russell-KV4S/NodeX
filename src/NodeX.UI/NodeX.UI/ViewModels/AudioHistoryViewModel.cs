using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NodeX.Core;
using NodeX.Core.Audio;

namespace NodeX.UI.ViewModels;

public enum AudioHistoryFilter
{
    All = 0,
    Rx = 1,
    Tx = 2
}

/// <summary>One Zello-style voice message card in the Audio History overlay.</summary>
public sealed partial class AudioClipItemViewModel : ViewModelBase
{
    private readonly AudioHistoryViewModel _owner;

    public AudioClipItemViewModel(AudioHistoryViewModel owner, AudioClip clip)
    {
        _owner = owner;
        Clip = clip;
    }

    public AudioClip Clip { get; }
    public string Id => Clip.Id;
    public bool IsRx => Clip.Direction == AudioClipDirection.Rx;
    public bool IsTx => Clip.Direction == AudioClipDirection.Tx;
    public string DirectionBadge => IsRx ? "RX" : "TX";

    public string Title
    {
        get
        {
            string target = string.IsNullOrWhiteSpace(Clip.NodeLabel)
                ? $"Node {Clip.Node}"
                : $"{Clip.Node} · {Clip.NodeLabel}";
            return IsTx ? $"You → {target}" : target;
        }
    }

    public string TimeText
    {
        get
        {
            var local = Clip.TimestampUtc.ToLocalTime();
            var now = DateTimeOffset.Now;
            string clock = local.Date == now.Date
                ? local.ToString("h:mm:ss tt")
                : local.ToString("MMM d, h:mm tt");
            var age = now - local;
            string ago = age.TotalSeconds < 60
                ? "just now"
                : $"{DurationText.Format(age)} ago";
            return $"{clock} · {ago}";
        }
    }

    public string TotalDurationText => FormatClipTime(Clip.DurationSeconds);

    public bool CanShare => AppServices.AudioFileSharer != null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayGlyph))]
    [NotifyPropertyChangedFor(nameof(PlayLabel))]
    [NotifyPropertyChangedFor(nameof(DurationDisplay))]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationDisplay))]
    public partial double PositionSeconds { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial bool IsUnheard { get; set; }

    public string PlayGlyph => IsPlaying ? "■" : "▶";
    public string PlayLabel => IsPlaying ? "Stop" : "Play";

    public string DurationDisplay => IsPlaying
        ? $"{FormatClipTime(PositionSeconds)} / {TotalDurationText}"
        : TotalDurationText;

    public string PlayAutomationName => IsPlaying ? $"Stop replay of {Title}" : $"Play clip from {Title}, {TotalDurationText}";
    public string ShareAutomationName => $"Share audio clip from {Title}";
    public string DeleteAutomationName => $"Delete audio clip from {Title}";

    [RelayCommand]
    private Task TogglePlayAsync() => _owner.TogglePlayClipAsync(Clip);

    [RelayCommand]
    private Task ShareAsync() => _owner.ShareClipAsync(Clip);

    [RelayCommand]
    private Task DeleteAsync() => _owner.DeleteClipAsync(Clip);

    public static string FormatClipTime(double seconds)
    {
        int total = Math.Max(0, (int)Math.Round(seconds));
        return $"{total / 60}:{total % 60:00}";
    }
}

/// <summary>View-model for the Zello-style Audio Replay &amp; History overlay.</summary>
public sealed partial class AudioHistoryViewModel : ViewModelBase, IDisposable
{
    private readonly AudioHistoryStore _store;
    private readonly AudioClipPlayer _player;
    private readonly Func<bool> _isRecordingEnabled;
    private bool _disposed;

    public AudioHistoryViewModel(
        AudioHistoryStore? store = null,
        AudioClipPlayer? player = null,
        Func<bool>? isRecordingEnabled = null)
    {
        _store = store ?? AppServices.AudioHistory;
        _player = player ?? AppServices.AudioPlayer;
        _isRecordingEnabled = isRecordingEnabled ?? (() => AppServices.Runtime.Config.Settings.SaveAudioHistory);

        _store.Changed += OnStoreChanged;
        _player.StateChanged += OnPlayerStateChanged;
        _player.ErrorOccurred += OnPlayerError;

        RefreshFromStore();
    }

    public event Action? Closed;

    public ObservableCollection<AudioClipItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllFilter))]
    [NotifyPropertyChangedFor(nameof(IsRxFilter))]
    [NotifyPropertyChangedFor(nameof(IsTxFilter))]
    public partial AudioHistoryFilter Filter { get; set; } = AudioHistoryFilter.All;

    public bool IsAllFilter => Filter == AudioHistoryFilter.All;
    public bool IsRxFilter => Filter == AudioHistoryFilter.Rx;
    public bool IsTxFilter => Filter == AudioHistoryFilter.Tx;

    [ObservableProperty]
    public partial bool HasAnyClips { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool HasVisibleClips { get; set; }

    public bool IsEmpty => !HasVisibleClips;

    [ObservableProperty]
    public partial bool HasLatestRx { get; set; }

    [ObservableProperty]
    public partial string LatestRxTitle { get; set; } = "";

    [ObservableProperty]
    public partial string LatestRxMeta { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatestRxPlayGlyph))]
    [NotifyPropertyChangedFor(nameof(LatestRxPlayLabel))]
    public partial bool IsPlayingLatestRx { get; set; }

    [ObservableProperty]
    public partial double LatestRxProgress { get; set; }

    public string LatestRxPlayGlyph => IsPlayingLatestRx ? "■" : "▶";
    public string LatestRxPlayLabel => IsPlayingLatestRx ? "Stop" : "Replay last";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnheard))]
    public partial int UnheardCount { get; set; }

    public bool HasUnheard => UnheardCount > 0;

    [ObservableProperty]
    public partial bool IsRecordingOff { get; set; }

    [ObservableProperty]
    public partial bool IsConfirmingClear { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string StatusText { get; set; } = "";

    public bool HasStatus => !string.IsNullOrEmpty(StatusText);

    private AudioClip? _latestRx;

    public void OnOpened()
    {
        StatusText = "";
        IsConfirmingClear = false;
        RefreshFromStore();
    }

    public void RefreshRecordingFlag() => IsRecordingOff = !_isRecordingEnabled();

    public Task StopPlaybackAsync() => _player.StopAsync();

    [RelayCommand]
    private async Task BackAsync()
    {
        IsConfirmingClear = false;
        await _player.StopAsync();
        Closed?.Invoke();
    }

    [RelayCommand]
    private void SetFilter(string filterName)
    {
        Filter = filterName switch
        {
            "rx" => AudioHistoryFilter.Rx,
            "tx" => AudioHistoryFilter.Tx,
            _ => AudioHistoryFilter.All
        };
        RefreshFromStore();
    }

    [RelayCommand]
    private async Task ReplayLastAsync()
    {
        if (_latestRx == null) return;
        await TogglePlayClipAsync(_latestRx);
    }

    [RelayCommand]
    private void RequestClear()
    {
        if (HasAnyClips) IsConfirmingClear = true;
    }

    [RelayCommand]
    private void CancelClear() => IsConfirmingClear = false;

    [RelayCommand]
    private async Task ConfirmClearAsync()
    {
        IsConfirmingClear = false;
        await _player.StopAsync();
        _store.ClearAll();
        StatusText = "Audio history cleared.";
    }

    public async Task TogglePlayClipAsync(AudioClip clip)
    {
        StatusText = "";
        if (_player.IsPlaying && _player.CurrentClipId == clip.Id)
        {
            await _player.StopAsync();
            return;
        }
        _store.MarkHeard(clip.Id);
        await _player.PlayAsync(clip, _store.GetFilePath(clip));
    }

    public async Task ShareClipAsync(AudioClip clip)
    {
        StatusText = "";
        var sharer = AppServices.AudioFileSharer;
        if (sharer == null)
        {
            StatusText = "Sharing audio clips isn't available on this device.";
            return;
        }
        string path = _store.GetFilePath(clip);
        if (!System.IO.File.Exists(path))
        {
            StatusText = "Audio file is no longer available.";
            return;
        }
        try
        {
            string title = $"NodeX {clip.Direction} Node {clip.Node} ({clip.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm})";
            await sharer(path, title);
        }
        catch (Exception ex)
        {
            StatusText = $"Could not share clip: {ex.Message}";
        }
    }

    public async Task DeleteClipAsync(AudioClip clip)
    {
        if (_player.CurrentClipId == clip.Id) await _player.StopAsync();
        _store.Delete(clip.Id);
    }

    private void OnStoreChanged()
    {
        if (_disposed) return;
        if (Dispatcher.UIThread.CheckAccess()) RefreshFromStore();
        else Dispatcher.UIThread.Post(RefreshFromStore);
    }

    private void OnPlayerStateChanged()
    {
        if (_disposed) return;
        if (Dispatcher.UIThread.CheckAccess()) ApplyPlaybackState();
        else Dispatcher.UIThread.Post(ApplyPlaybackState);
    }

    private void OnPlayerError(string msg)
    {
        if (_disposed) return;
        void Apply() => StatusText = msg;
        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }

    private void RefreshFromStore()
    {
        if (_disposed) return;
        IsRecordingOff = !_isRecordingEnabled();
        var all = _store.Clips;
        HasAnyClips = all.Count > 0;
        UnheardCount = _store.UnheardCount;

        _latestRx = _store.LatestRxClip;
        HasLatestRx = _latestRx != null;
        if (_latestRx != null)
        {
            var latestItem = new AudioClipItemViewModel(this, _latestRx);
            LatestRxTitle = latestItem.Title;
            LatestRxMeta = $"{latestItem.TimeText} · {latestItem.TotalDurationText}";
        }
        else
        {
            LatestRxTitle = "";
            LatestRxMeta = "";
        }

        var filtered = Filter switch
        {
            AudioHistoryFilter.Rx => all.Where(c => c.Direction == AudioClipDirection.Rx),
            AudioHistoryFilter.Tx => all.Where(c => c.Direction == AudioClipDirection.Tx),
            _ => all
        };

        Items.Clear();
        foreach (var clip in filtered)
        {
            Items.Add(new AudioClipItemViewModel(this, clip) { IsUnheard = clip.IsUnheard });
        }
        HasVisibleClips = Items.Count > 0;
        ApplyPlaybackState();
    }

    private void ApplyPlaybackState()
    {
        if (_disposed) return;
        string? playingId = _player.IsPlaying ? _player.CurrentClipId : null;
        double pos = _player.PositionSeconds;
        double prog = _player.Progress;

        IsPlayingLatestRx = _latestRx != null && playingId == _latestRx.Id;
        LatestRxProgress = IsPlayingLatestRx ? prog : 0;

        foreach (var item in Items)
        {
            bool active = playingId == item.Id;
            item.IsPlaying = active;
            item.PositionSeconds = active ? pos : 0;
            item.Progress = active ? prog : 0;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _store.Changed -= OnStoreChanged;
        _player.StateChanged -= OnPlayerStateChanged;
        _player.ErrorOccurred -= OnPlayerError;
    }
}
