namespace NodeX.Core.Settings;

/// <summary>Settings plus the node password, as used at runtime.</summary>
public sealed record NodeConfig(NodeSettings Settings, string? Password)
{
    public IReadOnlyList<SettingsIssue> Issues => NodeSettingsValidator.Validate(Settings, Password);

    /// <summary>True when node number and password are present and all values are valid.</summary>
    public bool IsComplete => Issues.Count == 0;

    /// <summary>Builds the registration configuration; only valid when <see cref="IsComplete"/>.</summary>
    public AslRegistrationConfig ToRegistrationConfig() =>
        new(Settings.NodeNumber, Password ?? "", Settings.IaxPort, Settings.RegistrationUrl);
}

/// <summary>Outcome of <see cref="NodeConfigService.SaveAsync"/>.</summary>
public sealed record SaveResult(bool Success, IReadOnlyList<SettingsIssue> Issues);

/// <summary>
/// Loads and saves the full configuration: settings go to an <see cref="ISettingsStore"/>, the
/// password goes to an <see cref="ISecretStore"/> and is never written to the settings file.
/// </summary>
public sealed class NodeConfigService
{
    private readonly ISettingsStore _settings;
    private readonly ISecretStore _secrets;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public NodeConfigService(ISettingsStore settings, ISecretStore secrets)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
    }

    /// <summary>Raised after a successful save with the new configuration.</summary>
    public event Action<NodeConfig>? Changed;

    public async Task<NodeConfig> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var settings = NodeSettingsValidator.Normalize(await _settings.LoadAsync(cancellationToken));
            string? password = await _secrets.GetAsync(SecretKeys.NodePassword, cancellationToken);
            return new NodeConfig(settings, password);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Validates, then stores the password securely and the settings as JSON.</summary>
    public async Task<SaveResult> SaveAsync(NodeSettings settings, string password, CancellationToken cancellationToken = default)
    {
        var normalized = NodeSettingsValidator.Normalize(settings);
        var issues = NodeSettingsValidator.Validate(normalized, password);
        if (issues.Count > 0) return new SaveResult(false, issues);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Password first: if the secret store fails we have not claimed a configuration was saved.
            await _secrets.SetAsync(SecretKeys.NodePassword, password, cancellationToken);
            await _settings.SaveAsync(normalized, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(new NodeConfig(normalized, password));
        return new SaveResult(true, []);
    }

    /// <summary>Forgets the stored password (for example on "sign out").</summary>
    public async Task ClearPasswordAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _secrets.DeleteAsync(SecretKeys.NodePassword, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }
}
