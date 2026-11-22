using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NodeX.Core;
using NodeX.Core.Settings;

namespace NodeX.UI.ViewModels;

/// <summary>One saved private node row in the Private Nodes manager.</summary>
public sealed partial class PrivateNodeRowViewModel : ObservableObject
{
    private readonly Action<PrivateNodeEntry> _connect;
    private readonly Func<PrivateNodeRowViewModel, Task> _testPoke;
    private readonly Action<PrivateNodeEntry> _edit;
    private readonly Action<PrivateNodeEntry> _share;
    private readonly Func<PrivateNodeEntry, Task> _remove;

    public PrivateNodeRowViewModel(
        PrivateNodeEntry entry,
        Action<PrivateNodeEntry> connect,
        Func<PrivateNodeRowViewModel, Task> testPoke,
        Action<PrivateNodeEntry> edit,
        Action<PrivateNodeEntry> share,
        Func<PrivateNodeEntry, Task> remove)
    {
        Entry = entry;
        _connect = connect;
        _testPoke = testPoke;
        _edit = edit;
        _share = share;
        _remove = remove;
    }

    public PrivateNodeEntry Entry { get; }

    public string Title => !string.IsNullOrWhiteSpace(Entry.Name) ? Entry.Name!.Trim() : $"Node {Entry.Node}";

    public string NodeLine => !string.IsNullOrWhiteSpace(Entry.Name) ? $"Node {Entry.Node}" : "";

    public bool HasNodeLine => NodeLine.Length > 0;

    public string MetaText => $"Port {Entry.Port} · IAX user radio";

    public string ConnectAutomationName => $"Connect to private node {Title}";
    public string EditAutomationName => $"Edit private node {Title}";
    public string ShareAutomationName => $"Share private node {Title}";
    public string RemoveAutomationName => $"Remove private node {Title}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPokeText))]
    public partial string PokeText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsPokeTesting { get; set; }

    [ObservableProperty]
    public partial bool IsPokeReachable { get; set; }

    [ObservableProperty]
    public partial bool IsPokeError { get; set; }

    public bool HasPokeText => PokeText.Length > 0;

    [RelayCommand]
    private void Connect() => _connect(Entry);

    [RelayCommand]
    private Task TestPokeAsync() => _testPoke(this);

    [RelayCommand]
    private void Edit() => _edit(Entry);

    [RelayCommand]
    private void Share() => _share(Entry);

    [RelayCommand]
    private Task RemoveAsync() => _remove(Entry);
}

/// <summary>
/// Full-screen overlay for managing, adding, editing, testing (IAX POKE), and sharing private / manually
/// addressed nodes (§4.9). Never connects or saves on its own when opened from a <c>nodex://</c> share link.
/// </summary>
public sealed partial class PrivateNodesViewModel : ViewModelBase
{
    private readonly NodeRuntime _runtime;
    private readonly Func<PrivateNodeEntry, PrivateNodeEntry?, Task<(bool Success, string? Error)>> _saveEntry;
    private readonly Func<PrivateNodeEntry, Task<bool>> _removeEntry;

    private PrivateNodeEntry? _editingOriginal;
    private CancellationTokenSource? _pokeCts;

    public PrivateNodesViewModel(
        NodeRuntime runtime,
        Func<PrivateNodeEntry, PrivateNodeEntry?, Task<(bool Success, string? Error)>> saveEntry,
        Func<PrivateNodeEntry, Task<bool>> removeEntry)
    {
        _runtime = runtime;
        _saveEntry = saveEntry;
        _removeEntry = removeEntry;
    }

    public ObservableCollection<PrivateNodeRowViewModel> Items { get; } = [];

    public bool HasItems => Items.Count > 0;
    public bool IsEmpty => Items.Count == 0;

    // --- Overlay mode: List | Form (Add / Edit / Deep-link confirm) | Share ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListMode), nameof(IsFormMode), nameof(IsShareMode), nameof(HeaderTitle))]
    public partial int Mode { get; set; } // 0 = List, 1 = Form, 2 = Share

    public bool IsListMode => Mode == 0;
    public bool IsFormMode => Mode == 1;
    public bool IsShareMode => Mode == 2;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderTitle), nameof(ShowQuickPaste), nameof(CancelButtonText), nameof(CanSaveAndConnect))]
    public partial bool IsFromDeepLink { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderTitle), nameof(ShowQuickPaste))]
    public partial bool IsEditingExisting { get; set; }

    public string HeaderTitle => Mode switch
    {
        1 when IsEditingExisting => "Edit private node",
        1 when IsFromDeepLink => "Confirm shared private node",
        1 => "Add private node",
        2 => "Share private node",
        _ => "Private nodes"
    };

    public bool ShowQuickPaste => IsFormMode && !IsEditingExisting && !IsFromDeepLink;
    public string CancelButtonText => IsFromDeepLink ? "Discard" : "Cancel";
    public bool CanSaveAndConnect => !IsFromDeepLink;

    // --- Quick paste (ManualNodeAddress.TryParse) ---

    [ObservableProperty]
    public partial string PasteInput { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPasteStatus))]
    public partial string PasteStatusText { get; set; } = "";

    [ObservableProperty]
    public partial bool PasteStatusIsError { get; set; }

    public bool HasPasteStatus => PasteStatusText.Length > 0;

    partial void OnPasteInputChanged(string value)
    {
        string trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0)
        {
            PasteStatusText = "";
            PasteStatusIsError = false;
            return;
        }

        if (!ManualNodeAddress.TryParse(trimmed, out var parsed, out string? error))
        {
            PasteStatusText = error ?? "Could not parse address.";
            PasteStatusIsError = true;
            return;
        }

        if (!parsed!.IsPrivate)
        {
            PasteStatusText = "That is a public node number only. Add @host[:port] or paste a rpt.conf line.";
            PasteStatusIsError = true;
            return;
        }

        NodeInput = parsed.Node;
        HostInput = parsed.Host!;
        PortInput = parsed.Port.ToString(CultureInfo.InvariantCulture);
        ErrorText = "";
        PasteStatusIsError = false;
        PasteStatusText = parsed.IsIpv6
            ? $"Filled node {parsed.Node}, host [{parsed.Host}], port {parsed.Port} (note: IPv6 dialling is not supported yet)."
            : $"Filled node {parsed.Node}, host {parsed.Host}, port {parsed.Port}.";
    }

    // --- Form fields ---

    [ObservableProperty]
    public partial string NodeInput { get; set; } = "";

    [ObservableProperty]
    public partial string HostInput { get; set; } = "";

    [ObservableProperty]
    public partial string PortInput { get; set; } = Iax2Protocol.DefaultPort.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    public partial string NameInput { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string StatusText { get; set; } = "";

    public bool HasError => ErrorText.Length > 0;
    public bool HasStatus => StatusText.Length > 0;

    // --- Informational IAX POKE reachability check ---

    [ObservableProperty]
    public partial bool IsPokeTesting { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPokeResult))]
    public partial string PokeResultText { get; set; } = "";

    [ObservableProperty]
    public partial bool PokeResultReachable { get; set; }

    [ObservableProperty]
    public partial bool PokeResultError { get; set; }

    public bool HasPokeResult => PokeResultText.Length > 0;

    // --- rpt.conf [nodes] server-setup help (§4.9) ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HelpToggleText))]
    public partial bool IsHelpExpanded { get; set; } = true;

    public string HelpToggleText => IsHelpExpanded ? "Hide" : "Show help";

    [ObservableProperty]
    public partial string RptConfLine { get; set; } = "";

    [ObservableProperty]
    public partial string RptConfHint { get; set; } = "";

    [ObservableProperty]
    public partial string CopyRptButtonText { get; set; } = "Copy rpt.conf line";

    [RelayCommand]
    private void ToggleHelp() => IsHelpExpanded = !IsHelpExpanded;

    public void RefreshRptConfHelp()
    {
        string localNode = _runtime.Config.Settings.NodeNumber;
        var addr = _runtime.PerceivedAddress;
        int port = _runtime.PerceivedPort > 0 ? _runtime.PerceivedPort : _runtime.Config.Settings.IaxPort;
        RptConfLine = ManualNodeAddress.FormatRptConfLine(localNode, addr, port);
        RptConfHint = addr != null
            ? $"Pre-filled with your node ({localNode}) and the last address reported by the registry ({addr}:{port})."
            : "Not registered yet — replace <your-public-ip> with your public or LAN IP (or register first to fill it automatically).";
    }

    public void NoteRptConfCopied()
    {
        CopyRptButtonText = "Copied!";
        StatusText = "Copied rpt.conf [nodes] line to clipboard.";
    }

    // --- Share view (nodex://add?...) ---

    [ObservableProperty]
    public partial string ShareTitle { get; set; } = "";

    [ObservableProperty]
    public partial string ShareEndpoint { get; set; } = "";

    [ObservableProperty]
    public partial string ShareUri { get; set; } = "";

    [ObservableProperty]
    public partial string CopyShareButtonText { get; set; } = "Copy nodex:// link";

    public void NoteShareUriCopied()
    {
        CopyShareButtonText = "Link copied!";
        StatusText = "Copied private node share link to clipboard.";
    }

    // --- Events to MainViewModel ---

    public event Action? Closed;
    public event Action<ManualNodeAddress, string?>? ConnectRequested;

    // --- Lifecycle / navigation ---

    public void ReloadFromConfig()
    {
        var saved = _runtime.Config.Settings.PrivateNodes ?? [];
        var oldPoke = Items.ToDictionary(i => i.Entry.Key, i => (i.PokeText, i.IsPokeReachable, i.IsPokeError));

        Items.Clear();
        foreach (var entry in saved)
        {
            var row = new PrivateNodeRowViewModel(
                entry,
                e => ConnectRequested?.Invoke(e.ToManualAddress(), e.Name),
                TestRowPokeAsync,
                OpenEdit,
                OpenShare,
                RemoveRowAsync);

            if (oldPoke.TryGetValue(entry.Key, out var p))
            {
                row.PokeText = p.PokeText;
                row.IsPokeReachable = p.IsPokeReachable;
                row.IsPokeError = p.IsPokeError;
            }
            Items.Add(row);
        }

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmpty));
        RefreshRptConfHelp();
    }

    public void OpenList()
    {
        CancelPoke();
        ReloadFromConfig();
        ErrorText = "";
        StatusText = "";
        CopyRptButtonText = "Copy rpt.conf line";
        IsHelpExpanded = Items.Count == 0;
        Mode = 0;
    }

    public void OpenAdd(ManualNodeAddress? prefill = null, string? name = null, bool fromDeepLink = false)
    {
        CancelPoke();
        ReloadFromConfig();
        _editingOriginal = null;
        IsEditingExisting = false;
        IsFromDeepLink = fromDeepLink;
        PasteInput = "";
        PasteStatusText = "";
        PasteStatusIsError = false;
        NodeInput = prefill?.Node ?? "";
        HostInput = prefill?.Host ?? "";
        PortInput = (prefill?.Port ?? Iax2Protocol.DefaultPort).ToString(CultureInfo.InvariantCulture);
        NameInput = name ?? "";
        ErrorText = "";
        StatusText = "";
        PokeResultText = "";
        PokeResultReachable = false;
        PokeResultError = false;
        CopyRptButtonText = "Copy rpt.conf line";
        IsHelpExpanded = !fromDeepLink;
        Mode = 1;
    }

    public void OpenEdit(PrivateNodeEntry entry)
    {
        CancelPoke();
        ReloadFromConfig();
        _editingOriginal = entry;
        IsEditingExisting = true;
        IsFromDeepLink = false;
        PasteInput = "";
        PasteStatusText = "";
        PasteStatusIsError = false;
        NodeInput = entry.Node;
        HostInput = entry.Host;
        PortInput = entry.Port.ToString(CultureInfo.InvariantCulture);
        NameInput = entry.Name ?? "";
        ErrorText = "";
        StatusText = "";
        PokeResultText = "";
        PokeResultReachable = false;
        PokeResultError = false;
        CopyRptButtonText = "Copy rpt.conf line";
        IsHelpExpanded = false;
        Mode = 1;
    }

    public void OpenShare(PrivateNodeEntry entry)
    {
        CancelPoke();
        ShareTitle = !string.IsNullOrWhiteSpace(entry.Name) ? entry.Name!.Trim() : $"Node {entry.Node}";
        ShareEndpoint = $"Node {entry.Node}";
        ShareUri = ManualNodeAddress.BuildShareUri(entry);
        CopyShareButtonText = "Copy nodex:// link";
        ErrorText = "";
        StatusText = "";
        Mode = 2;
    }

    [RelayCommand]
    private void StartAdd() => OpenAdd();

    [RelayCommand]
    private void Back()
    {
        CancelPoke();
        if (Mode == 2 || (Mode == 1 && !IsFromDeepLink))
        {
            Mode = 0;
            ErrorText = "";
            StatusText = "";
            return;
        }
        Closed?.Invoke();
    }

    [RelayCommand]
    private void Close()
    {
        CancelPoke();
        Closed?.Invoke();
    }

    [RelayCommand]
    private void PreviewIncomingShareLink()
    {
        if (ManualNodeAddress.TryParseShareUri(ShareUri, out var entry, out _))
            OpenAdd(entry!.ToManualAddress(), entry.Name, fromDeepLink: true);
    }

    // --- Validation & Save ---

    private bool TryBuildEntry(out PrivateNodeEntry? entry, out string? error)
    {
        entry = null;
        error = null;

        string node = (NodeInput ?? "").Trim();
        if (!NodeSettingsValidator.IsValidNodeNumber(node))
        {
            error = "Node number must be 3 to 7 digits (for example 1999).";
            return false;
        }

        string hostRaw = (HostInput ?? "").Trim();
        if (!ManualNodeAddress.IsValidHost(hostRaw, out string host, out _))
        {
            error = "Enter a valid DNS hostname, IPv4 address, or [IPv6] literal.";
            return false;
        }

        string portRaw = (PortInput ?? "").Trim();
        if (!int.TryParse(portRaw, NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
        {
            error = "IAX port must be a whole number between 1 and 65535 (default 4569).";
            return false;
        }

        string? name = (NameInput ?? "").Trim();
        if (name.Length > NodeSettingsValidator.MaxFavoriteLabelLength)
            name = name[..NodeSettingsValidator.MaxFavoriteLabelLength].TrimEnd();
        if (name.Length == 0) name = null;

        entry = new PrivateNodeEntry(node, host, port, name);
        return true;
    }

    [RelayCommand]
    private Task SaveAsync() => SaveCoreAsync(andConnect: false);

    [RelayCommand]
    private Task SaveAndConnectAsync() => SaveCoreAsync(andConnect: true);

    private async Task SaveCoreAsync(bool andConnect)
    {
        ErrorText = "";
        StatusText = "";

        if (!TryBuildEntry(out var entry, out string? error) || entry == null)
        {
            ErrorText = error ?? "Invalid private node details.";
            return;
        }

        var (ok, saveError) = await _saveEntry(entry, _editingOriginal);
        if (!ok)
        {
            ErrorText = saveError ?? "Could not save private node.";
            return;
        }

        ReloadFromConfig();
        _editingOriginal = null;

        // Per §4.9: when an entry is saved, send one informational IAX POKE (non-blocking).
        var savedRow = Items.FirstOrDefault(r => r.Entry.Key == entry.Key);
        if (savedRow != null)
            _ = TestRowPokeAsync(savedRow);

        if (andConnect)
        {
            Closed?.Invoke();
            ConnectRequested?.Invoke(entry.ToManualAddress(), entry.Name);
        }
        else if (IsFromDeepLink)
        {
            IsFromDeepLink = false;
            Mode = 0;
            StatusText = $"Saved private node {entry.Node}.";
        }
        else
        {
            Mode = 0;
            StatusText = $"Saved private node {entry.Node}.";
        }
    }

    // --- POKE checks (informational only; never block saving or dialling) ---

    [RelayCommand]
    private async Task TestFormPokeAsync()
    {
        ErrorText = "";
        string host = (HostInput ?? "").Trim();
        if (!ManualNodeAddress.IsValidHost(host, out string normalizedHost, out _))
        {
            ErrorText = "Enter a valid host before testing reachability.";
            return;
        }
        if (!int.TryParse((PortInput ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
        {
            ErrorText = "Enter a valid port (1–65535) before testing reachability.";
            return;
        }

        CancelPoke();
        var cts = new CancellationTokenSource();
        _pokeCts = cts;
        IsPokeTesting = true;
        PokeResultReachable = false;
        PokeResultError = false;
        PokeResultText = $"Sending IAX POKE to {normalizedHost}:{port}…";

        try
        {
            var result = await _runtime.ProbePrivateNodeAsync(normalizedHost, port, cts.Token);
            if (cts.IsCancellationRequested) return;
            PokeResultReachable = result.Reachable;
            PokeResultError = !result.Resolved;
            PokeResultText = result.Message;
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_pokeCts, cts)) IsPokeTesting = false;
        }
    }

    private async Task TestRowPokeAsync(PrivateNodeRowViewModel row)
    {
        row.IsPokeTesting = true;
        row.IsPokeReachable = false;
        row.IsPokeError = false;
        row.PokeText = "Checking reachability (POKE)…";
        try
        {
            var result = await _runtime.ProbePrivateNodeAsync(row.Entry.Host, row.Entry.Port);
            row.IsPokeReachable = result.Reachable;
            row.IsPokeError = !result.Resolved;
            row.PokeText = result.Message;
        }
        catch
        {
            row.IsPokeReachable = false;
            row.IsPokeError = false;
            row.PokeText = "No answer";
        }
        finally
        {
            row.IsPokeTesting = false;
        }
    }

    private async Task RemoveRowAsync(PrivateNodeEntry entry)
    {
        if (await _removeEntry(entry))
        {
            ReloadFromConfig();
            StatusText = $"Removed private node {entry.Node}.";
        }
    }

    private void CancelPoke()
    {
        _pokeCts?.Cancel();
        _pokeCts?.Dispose();
        _pokeCts = null;
        IsPokeTesting = false;
    }
}
