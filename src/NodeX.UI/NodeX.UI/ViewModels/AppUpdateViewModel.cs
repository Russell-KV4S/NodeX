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
    private CancellationTokenSource? _operation;
    private DateTimeOffset _lastCheck;
    private string? _downloaded;
    private AndroidRelease? _release;

    public AppUpdateViewModel() { }

    public AppUpdateViewModel(GitHubReleaseUpdater updater, long installedCode, string directory,
        Func<string, AndroidRelease, Task<string>> installer, Func<bool> canInstall)
    {
        _updater = updater;
        _installedCode = installedCode;
        _directory = directory;
        _installer = installer;
        _canInstall = canInstall;
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

    public async Task CheckAutomaticallyAsync()
    {
        if (DateTimeOffset.UtcNow - _lastCheck < TimeSpan.FromDays(1)) return;
        await CheckAsync();
    }

    [RelayCommand]
    private async Task CheckAsync()
    {
        if (!Supported || IsBusy) return;
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
            Status = "Disconnect all nodes and leave Android Auto before installing an update.";
            return;
        }
        IsBusy = true;
        _operation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        Progress = 0;
        try
        {
            Status = "Downloading and verifying update…";
            _downloaded ??= await _updater!.DownloadAsync(_release, _directory,
                new Progress<double>(p => Progress = p * 100), _operation.Token);
            _operation.Token.ThrowIfCancellationRequested();
            if (_canInstall?.Invoke() != true)
            {
                Status = "Download ready. Disconnect all nodes and leave Android Auto, then tap Download and install again.";
                return;
            }
            Status = await _installer(_downloaded, _release);
        }
        catch (OperationCanceledException) { Status = "Update download cancelled or timed out."; }
        catch (Exception ex) { _downloaded = null; Status = ex.Message; }
        finally { _operation.Dispose(); _operation = null; IsBusy = false; }
    }

    [RelayCommand]
    private void Cancel() => _operation?.Cancel();
}
