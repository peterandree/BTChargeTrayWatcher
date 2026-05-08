namespace BTChargeTrayWatcher;

/// <summary>
/// Value object that holds the ntfy push notification configuration.
/// Stored inside <see cref="ThresholdSettings"/> and persisted by
/// <see cref="SettingsPersistence"/>. Transport logic does not live here.
/// </summary>
public sealed class NtfyIntegrationSettings
{
    public bool   IsEnabled { get; set; }
    public string? Topic    { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Topic);

    /// <summary>Returns a copy so the original is not mutated by callers.</summary>
    public NtfyIntegrationSettings Clone() =>
        new() { IsEnabled = IsEnabled, Topic = Topic };
}
