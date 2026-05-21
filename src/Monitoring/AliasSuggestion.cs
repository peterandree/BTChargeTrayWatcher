namespace BTChargeTrayWatcher;

/// <summary>
/// Emitted by <see cref="BatteryReaderOrchestrator"/> when Stage 4 (Jaro-Winkler fuzzy
/// match) finds a high-confidence but not exact alias candidate for a device name that
/// is not yet in the <see cref="ThresholdSettings.AliasMap"/>.
/// The UI should surface this to the user for confirmation before auto-applying (ADR-015).
/// </summary>
/// <param name="DeviceId">Hardware ID of the newly observed device.</param>
/// <param name="DeviceName">Display name of the newly observed device.</param>
/// <param name="MatchedAliasKey">The existing AliasMap key that scored highest.</param>
/// <param name="CanonicalDeviceId">The canonical device ID the matched key resolves to.</param>
/// <param name="Score">Jaro-Winkler similarity score (0.0 – 1.0).</param>
public sealed record AliasSuggestion(
    string DeviceId,
    string DeviceName,
    string MatchedAliasKey,
    string CanonicalDeviceId,
    double Score);
