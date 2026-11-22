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
    public bool IsRx => Clip.Direction == AudioClipDirection.Rx && !Clip.IsCombined;
    public bool IsTx => Clip.Direction == AudioClipDirection.Tx && !Clip.IsCombined;
    public bool IsCombined => Clip.IsCombined;
    public string DirectionBadge => IsCombined ? "COMBINED" : IsRx ? "RX" : "TX";

    public string Title
    {
        get
        {
            if (IsCombined)
            {
                if (string.IsNullOrWhiteSpace(Clip.NodeLabel))
                    return $"Combined ({Clip.Node})";
                return string.Equals(Clip.Node, "Combined", StringComparison.OrdinalIgnoreCase)
                    ? Clip.NodeLabel
                    : $"Node {Clip.Node} · {Clip.NodeLabel}";
            }

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

    [ObservableProperty]
    public partial bool IsSelecting { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value) => _owner.SetItemSelected(Clip.Id, value);

    public string PlayGlyph => IsPlaying ? "■" : "▶";
    public string PlayLabel => IsPlaying ? "Stop" : "Play";

    public string DurationDisplay => IsPlaying
        ? $"{FormatClipTime(PositionSeconds)} / {TotalDurationText}"
        : TotalDurationText;

    public string PlayAutomationName => IsPlaying ? $"Stop replay of {Title}" : $"Play clip from {Title}, {TotalDurationText}";
    public string ShareAutomationName => $"Share audio clip from {Title}";
    public string DeleteAutomationName => $"Delete audio clip from {Title}";

    [RelayCommand]
    private void ToggleSelect() => IsSelected = !IsSelected;

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
    private readonly HashSet<string> _selectedIds = new(StringComparer.Ordinal);
    private bool _suppressSelectionSync;
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

    [ObservableProperty]
    public partial string StorageSummaryText { get; set; } = "";

    // ----- multi-select & combine -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCombineSelected))]
    [NotifyPropertyChangedFor(nameof(CanDeleteSelected))]
    [NotifyPropertyChangedFor(nameof(SelectAllLabel))]
    public partial bool IsSelecting { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCombineSelected))]
    [NotifyPropertyChangedFor(nameof(CanDeleteSelected))]
    [NotifyPropertyChangedFor(nameof(SelectionSummaryText))]
    [NotifyPropertyChangedFor(nameof(CombineButtonText))]
    [NotifyPropertyChangedFor(nameof(SelectAllLabel))]
    public partial int SelectedCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionSummaryText))]
    public partial double SelectedDurationSeconds { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCombineSelected))]
    [NotifyPropertyChangedFor(nameof(CanDeleteSelected))]
    [NotifyPropertyChangedFor(nameof(CombineButtonText))]
    public partial bool IsCombining { get; set; }

    [ObservableProperty]
    public partial string CombineTitle { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastCombinedClip))]
    [NotifyPropertyChangedFor(nameof(CanShareLastCombined))]
    public partial AudioClip? LastCombinedClip { get; set; }

    public bool HasLastCombinedClip => LastCombinedClip != null;
    public bool CanShareLastCombined => LastCombinedClip != null && AppServices.AudioFileSharer != null;
    public bool CanCombineSelected => IsSelecting && SelectedCount >= 2 && !IsCombining;
    public bool CanDeleteSelected => IsSelecting && SelectedCount >= 1 && !IsCombining;
    public string CombineButtonText => IsCombining ? "Combining…" : $"Combine ({SelectedCount})";

    public string SelectionSummaryText => SelectedCount == 0
        ? "Tap clips to select"
        : $"{SelectedCount} {(SelectedCount == 1 ? "clip" : "clips")} ({AudioClipItemViewModel.FormatClipTime(SelectedDurationSeconds)})";

    public string SelectAllLabel =>
        Items.Count > 0 && Items.All(i => i.IsSelected) ? "Clear" : "Select all";

    private AudioClip? _latestRx;

    public void OnOpened()
    {
        StatusText = "";
        IsConfirmingClear = false;
        ExitSelectionMode();
        LastCombinedClip = null;
        RefreshFromStore();
    }

    public void RefreshRecordingFlag() => IsRecordingOff = !_isRecordingEnabled();

    public void RefreshFromSettings()
    {
        if (_disposed) return;
        if (Dispatcher.UIThread.CheckAccess()) RefreshFromStore();
        else Dispatcher.UIThread.Post(RefreshFromStore);
    }

    public Task StopPlaybackAsync() => _player.StopAsync();

    [RelayCommand]
    private async Task BackAsync()
    {
        if (IsSelecting)
        {
            CancelSelect();
            return;
        }
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
    private async Task BeginSelectAsync()
    {
        if (!HasAnyClips) return;
        IsConfirmingClear = false;
        LastCombinedClip = null;
        StatusText = "";
        await _player.StopAsync();
        _selectedIds.Clear();
        CombineTitle = "";
        IsSelecting = true;
        ApplySelectionFlagsToItems();
        UpdateSelectionMetrics();
    }

    [RelayCommand]
    private void CancelSelect() => ExitSelectionMode();

    [RelayCommand]
    private void ToggleSelectAll()
    {
        if (!IsSelecting || Items.Count == 0) return;
        bool selectAll = !Items.All(i => i.IsSelected);
        _suppressSelectionSync = true;
        try
        {
            foreach (var item in Items)
            {
                item.IsSelected = selectAll;
                if (selectAll) _selectedIds.Add(item.Id);
                else _selectedIds.Remove(item.Id);
            }
        }
        finally
        {
            _suppressSelectionSync = false;
        }
        UpdateSelectionMetrics();
    }

    [RelayCommand]
    private async Task CombineSelectedAsync()
    {
        if (!CanCombineSelected) return;
        string[] ids = [.. _selectedIds];
        string title = CombineTitle;

        IsCombining = true;
        StatusText = "";
        LastCombinedClip = null;
        try
        {
            var combined = await Task.Run(() => _store.CombineClips(ids, title));
            ExitSelectionMode();
            Filter = AudioHistoryFilter.All;
            RefreshFromStore();
            LastCombinedClip = combined;
            StatusText = $"Combined {ids.Length} clips ({AudioClipItemViewModel.FormatClipTime(combined.DurationSeconds)}) in chronological order.";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not combine clips: {ex.Message}";
        }
        finally
        {
            IsCombining = false;
        }
    }

    [RelayCommand]
    private async Task ShareLastCombinedAsync()
    {
        if (LastCombinedClip is { } clip)
            await ShareClipAsync(clip);
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (!CanDeleteSelected) return;
        string[] ids = [.. _selectedIds];
        if (ids.Length == 0) return;

        if (_player.CurrentClipId is { } playing && _selectedIds.Contains(playing))
            await _player.StopAsync();

        int deleted = _store.DeleteMany(ids);
        ExitSelectionMode();
        LastCombinedClip = null;
        StatusText = deleted == 1 ? "Deleted 1 clip." : $"Deleted {deleted} clips.";
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
        ExitSelectionMode();
        LastCombinedClip = null;
        await _player.StopAsync();
        _store.ClearAll();
        StatusText = "Audio history cleared.";
    }

    internal void SetItemSelected(string clipId, bool selected)
    {
        if (_suppressSelectionSync) return;
        if (selected) _selectedIds.Add(clipId);
        else _selectedIds.Remove(clipId);
        UpdateSelectionMetrics();
    }

    private void ExitSelectionMode()
    {
        IsSelecting = false;
        CombineTitle = "";
        _selectedIds.Clear();
        ApplySelectionFlagsToItems();
        UpdateSelectionMetrics();
    }

    private void ApplySelectionFlagsToItems()
    {
        _suppressSelectionSync = true;
        try
        {
            foreach (var item in Items)
            {
                item.IsSelecting = IsSelecting;
                item.IsSelected = IsSelecting && _selectedIds.Contains(item.Id);
            }
        }
        finally
        {
            _suppressSelectionSync = false;
        }
    }

    private void UpdateSelectionMetrics()
    {
        var clips = _store.Clips;
        int count = 0;
        double totalSeconds = 0;
        foreach (var c in clips)
        {
            if (_selectedIds.Contains(c.Id))
            {
                count++;
                totalSeconds += c.DurationSeconds;
            }
        }
        SelectedCount = count;
        SelectedDurationSeconds = totalSeconds;
        OnPropertyChanged(nameof(SelectAllLabel));
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
            string title = clip.IsCombined
                ? $"NodeX Combined {clip.NodeLabel} ({clip.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm})"
                : $"NodeX {clip.Direction} Node {clip.Node} ({clip.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm})";
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

        string used = AudioHistoryStore.FormatBytes(_store.TotalSizeBytes);
        StorageSummaryText = $"{used} · {all.Count} / {_store.MaxClips} clips";

        if (IsSelecting)
        {
            var validIds = all.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            _selectedIds.IntersectWith(validIds);
            if (!HasAnyClips) IsSelecting = false;
        }

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
            AudioHistoryFilter.Rx => all.Where(c => c.Direction == AudioClipDirection.Rx && !c.IsCombined),
            AudioHistoryFilter.Tx => all.Where(c => c.Direction == AudioClipDirection.Tx && !c.IsCombined),
            _ => all
        };

        _suppressSelectionSync = true;
        try
        {
            Items.Clear();
            foreach (var clip in filtered)
            {
                Items.Add(new AudioClipItemViewModel(this, clip)
                {
                    IsUnheard = clip.IsUnheard,
                    IsSelecting = IsSelecting,
                    IsSelected = IsSelecting && _selectedIds.Contains(clip.Id)
                });
            }
        }
        finally
        {
            _suppressSelectionSync = false;
        }

        HasVisibleClips = Items.Count > 0;
        UpdateSelectionMetrics();
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
