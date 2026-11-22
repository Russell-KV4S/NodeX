using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NodeX.Core.Updates;

namespace NodeX.UI.ViewModels;

/// <summary>The phone's opt-in download/install flow; automatic checks never install or interrupt a link.</summary>
public partial class AppUpdateViewModel : ViewModelBase
{
    private readonly GitHubReleaseUpdater? _updater;
    private readonly long _installedCode;
    private readonly string _directory = "";
    private readonly Func<string, AndroidRelease, Task<string>>? _installer;
    private readonly Func<bool>? _canInstall;
    private readonly Func<Task>? _disconnectAll;
    private readonly Func<bool>? _isCarActive;
    private readonly Func<int>? _linkedCount;
    private CancellationTokenSource? _operation;
    private DateTimeOffset _lastCheck;
    private string? _downloaded;
    private AndroidRelease? _release;

    public AppUpdateViewModel() { }

    public AppUpdateViewModel(
        GitHubReleaseUpdater updater,
        long installedCode,
        string directory,
        Func<string, AndroidRelease, Task<string>> installer,
        Func<bool> canInstall,
        Func<Task>? disconnectAll = null,
        Func<bool>? isCarActive = null,
        Func<int>? linkedCount = null)
    {
        _updater = updater;
        _installedCode = installedCode;
        _directory = directory;
        _installer = installer;
        _canInstall = canInstall;
        _disconnectAll = disconnectAll;
        _isCarActive = isCarActive;
        _linkedCount = linkedCount;
    }

    public bool Supported => _updater != null;
    public bool HasUpdate => _release != null;
    public string AvailableText => HasUpdate ? $"NodeX {_release!.Version} is available. Open Settings to update." : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; set; }
    public bool IsIdle => !IsBusy;

    [ObservableProperty]
    public partial string Status { get; set; } = "Check GitHub for updates to this full Android edition.";

    [ObservableProperty]
    public partial double Progress { get; set; }

    /// <summary>True when active links block installation and the user is asked whether to disconnect them now.</summary>
    [ObservableProperty]
    public partial bool IsConfirmingDisconnect { get; set; }

    [ObservableProperty]
    public partial string DisconnectPromptText { get; set; } = "";

    public async Task CheckAutomaticallyAsync()
    {
        if (DateTimeOffset.UtcNow - _lastCheck < TimeSpan.FromDays(1)) return;
        await CheckAsync();
    }

    [RelayCommand]
    private async Task CheckAsync()
    {
        if (!Supported || IsBusy) return;
        IsConfirmingDisconnect = false;
        IsBusy = true;
        _operation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _lastCheck = DateTimeOffset.UtcNow;
        Status = "Checking GitHub…";
        try
        {
            var release = await _updater!.CheckAsync(_installedCode, _operation.Token);
            _release = release;
            _downloaded = null;
            OnPropertyChanged(nameof(HasUpdate));
            OnPropertyChanged(nameof(AvailableText));
            Status = release == null ? "This Android edition is up to date." : $"NodeX {release.Version} is available ({release.Size / 1048576.0:F1} MB).";
        }
        catch (OperationCanceledException) { Status = "Update check cancelled or timed out. Try again when connected."; }
        catch (Exception ex) { Status = ex.Message; }
        finally { _operation.Dispose(); _operation = null; IsBusy = false; }
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (IsBusy || _release == null || _installer == null) return;
        if (_canInstall?.Invoke() != true)
        {
            HandleBlockedInstall(downloadReady: _downloaded != null);
            return;
        }
        IsConfirmingDisconnect = false;
        await RunDownloadAndInstallAsync();
    }

    [RelayCommand]
    private void CancelDisconnectPrompt()
    {
        IsConfirmingDisconnect = false;
        Status = "Update paused. Disconnect manually when you're ready, then tap Download and install.";
    }

    [RelayCommand]
    private async Task DisconnectAndInstallAsync()
    {
        if (IsBusy || _release == null || _installer == null || _disconnectAll == null) return;
        IsConfirmingDisconnect = false;

        if (_isCarActive?.Invoke() == true)
        {
            Status = "Leave Android Auto before installing an update.";
            return;
        }

        IsBusy = true;
        Status = "Disconnecting linked nodes…";
        try
        {
            await _disconnectAll();
        }
        catch (Exception ex)
        {
            IsBusy = false;
            Status = $"Could not disconnect nodes: {ex.Message}";
            return;
        }

        if (_canInstall?.Invoke() != true)
        {
            IsBusy = false;
            Status = _isCarActive?.Invoke() == true
                ? "Leave Android Auto before installing an update."
                : "Disconnect all nodes and leave Android Auto before installing an update.";
            return;
        }

        await RunDownloadAndInstallAsync(alreadyBusy: true);
    }

    private void HandleBlockedInstall(bool downloadReady)
    {
        if (_isCarActive?.Invoke() == true)
        {
            IsConfirmingDisconnect = false;
            Status = downloadReady
                ? "Download ready. Leave Android Auto before installing this update."
                : "Leave Android Auto before installing an update.";
            return;
        }

        if (_disconnectAll != null && _release != null)
        {
            int count = _linkedCount?.Invoke() ?? 0;
            string links = count == 1 ? "1 linked node"
                : count > 1 ? $"{count} linked nodes"
                : "all linked nodes";
            DisconnectPromptText = downloadReady
                ? $"Update ready. Disconnect {links} now to install NodeX {_release.Version}?"
                : $"Disconnect {links} now to download and install NodeX {_release.Version}?";
            IsConfirmingDisconnect = true;
            Status = "Disconnect linked nodes before installing this update, or choose Cancel to disconnect manually.";
            return;
        }

        IsConfirmingDisconnect = false;
        Status = downloadReady
            ? "Download ready. Disconnect all nodes and leave Android Auto, then tap Download and install again."
            : "Disconnect all nodes and leave Android Auto before installing an update.";
    }

    private async Task RunDownloadAndInstallAsync(bool alreadyBusy = false)
    {
        if (!alreadyBusy) IsBusy = true;
        _operation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        Progress = 0;
        try
        {
            Status = "Downloading and verifying update…";
            _downloaded ??= await _updater!.DownloadAsync(_release!, _directory,
                new Progress<double>(p => Progress = p * 100), _operation.Token);
            _operation.Token.ThrowIfCancellationRequested();
            if (_canInstall?.Invoke() != true)
            {
                HandleBlockedInstall(downloadReady: true);
                return;
            }
            Status = await _installer!(_downloaded, _release!);
        }
        catch (OperationCanceledException) { Status = "Update download cancelled or timed out."; }
        catch (Exception ex) { _downloaded = null; Status = ex.Message; }
        finally { _operation.Dispose(); _operation = null; IsBusy = false; }
    }

    [RelayCommand]
    private void Cancel()
    {
        IsConfirmingDisconnect = false;
        _operation?.Cancel();
    }
}
