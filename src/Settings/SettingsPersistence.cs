using System.Text.Json;

namespace BTChargeTrayWatcher;

/// <summary>
/// Owns all JSON serialisation, file-path construction, and atomic disk writes
/// for <see cref="ThresholdSettings"/>. The domain class is kept free of I/O.
/// </summary>
internal sealed class SettingsPersistence : IDisposable
{
    private const string AppName = "BTChargeTrayWatcher";
    private const int SaveDebounceMs = 300;

    private readonly string _settingsFilePath;
    private readonly ThresholdSettings _settings;

    private CancellationTokenSource? _saveCts;
    private readonly object _saveLock = new();

    public SettingsPersistence(ThresholdSettings settings)
    {
        _settings = settings;

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string appDir = Path.Combine(localAppData, AppName);
        Directory.CreateDirectory(appDir);
        _settingsFilePath = Path.Combine(appDir, "settings.json");

        _settings.Changed += OnChanged;
    }

    /// <summary>
    /// Schedules a debounced save. Rapid successive mutations collapse into a
    /// single write 300 ms after the last change, preventing both UI-thread stalls
    /// and unnecessary disk I/O.
    /// </summary>
    private void OnChanged()
    {
        CancellationTokenSource cts;
        lock (_saveLock)
        {
            _saveCts?.Cancel();
            _saveCts?.Dispose();
            _saveCts = new CancellationTokenSource();
            cts = _saveCts;
        }

        var token = cts.Token;
        _ = Task.Delay(SaveDebounceMs, token)
            .ContinueWith(
                _ => Save(),
                token,
                TaskContinuationOptions.NotOnCanceled,
                TaskScheduler.Default);
    }

    // ── Public API ─────────────────────────────────────────────────────

    public void Load()
    {
        try
        {
            if (!File.Exists(_settingsFilePath)) return;

            string json = File.ReadAllText(_settingsFilePath);
            var dto = JsonSerializer.Deserialize<SettingsDto>(json);
            if (dto == null) return;

            int low = dto.Low;
            int high = dto.High;
            if (low >= high) { low = 20; high = 80; }

            int laptopLow = dto.LaptopLow;
            int laptopHigh = dto.LaptopHigh;
            if (laptopLow >= laptopHigh) { laptopLow = 20; laptopHigh = 80; }

            var ignored = dto.IgnoredDevices != null
                ? new HashSet<string>(dto.IgnoredDevices, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var excluded = dto.TrayIconOverlayExcludedDevices != null
                ? new HashSet<string>(dto.TrayIconOverlayExcludedDevices, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var overrides = new Dictionary<string, DeviceThresholds>(StringComparer.OrdinalIgnoreCase);
            if (dto.DeviceOverrides != null)
            {
                foreach (var kvp in dto.DeviceOverrides)
                {
                    var t = kvp.Value;
                    if (t is null) continue;
                    int effectiveLow = t.Low ?? low;
                    int effectiveHigh = t.High ?? high;
                    if (effectiveLow >= effectiveHigh)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[SettingsPersistence] Dropping invalid override for '{kvp.Key}': Low={t.Low}, High={t.High}");
                        continue;
                    }
                    overrides[kvp.Key] = new DeviceThresholds { Low = t.Low, High = t.High };
                }
            }

            var pollIntervals = dto.DevicePollIntervals != null
                ? new Dictionary<string, int>(dto.DevicePollIntervals, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            var displayAliases = dto.DeviceDisplayNameAliases != null
                ? new Dictionary<string, string>(dto.DeviceDisplayNameAliases, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var ntfy = new NtfyIntegrationSettings
            {
                IsEnabled = dto.NtfyEnabled,
                Topic     = dto.NtfyTopic
            };

            var categoryFilterOverrides = dto.CategoryFilterOverrides != null
                ? new HashSet<string>(dto.CategoryFilterOverrides, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var aliasMap = dto.AliasMap != null
                ? new Dictionary<string, string>(dto.AliasMap, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var suppressed = dto.SuppressedAliasSuggestions != null
                ? new HashSet<string>(dto.SuppressedAliasSuggestions, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            _settings.Changed -= OnChanged;
            try
            {
                var uiStates = dto.UiWindowStates != null
                    ? new Dictionary<string, UiWindowState>(dto.UiWindowStates, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, UiWindowState>(StringComparer.OrdinalIgnoreCase);

                _settings.ApplySnapshot(new SettingsSnapshot(
                    low, high, laptopLow, laptopHigh,
                    ignored, excluded,
                    dto.ExcludeLaptopFromTrayIconOverlay,
                    dto.AutoStartUseScheduledTaskFallback,
                    overrides,
                    pollIntervals,
                    displayAliases,
                    ntfy,
                    dto.CategoryFilterEnabled,
                    categoryFilterOverrides,
                    aliasMap,
                    suppressed,
                    uiStates));
            }
            finally
            {
                _settings.Changed += OnChanged;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsPersistence] Load fault: {ex}");
        }
    }

    public void Dispose()
    {
        _settings.Changed -= OnChanged;
        lock (_saveLock)
        {
            _saveCts?.Cancel();
            _saveCts?.Dispose();
            _saveCts = null;
        }
    }

    // ── Private ────────────────────────────────────────────────────────

    private void Save()
    {
        var s = _settings.Snapshot();
        var dto = new SettingsDto
        {
            Version = 1,
            Low = s.Low,
            High = s.High,
            LaptopLow = s.LaptopLow,
            LaptopHigh = s.LaptopHigh,
            IgnoredDevices = [.. s.IgnoredDevices],
            TrayIconOverlayExcludedDevices = [.. s.TrayIconOverlayExcludedDevices],
            ExcludeLaptopFromTrayIconOverlay = s.ExcludeLaptopFromTrayIconOverlay,
            AutoStartUseScheduledTaskFallback = s.AutoStartUseScheduledTaskFallback,
            DeviceOverrides = new Dictionary<string, DeviceThresholds>(s.DeviceOverrides, StringComparer.OrdinalIgnoreCase),
            DevicePollIntervals = new Dictionary<string, int>(s.DevicePollIntervals, StringComparer.OrdinalIgnoreCase),
            DeviceDisplayNameAliases = new Dictionary<string, string>(s.DeviceDisplayNameAliases, StringComparer.OrdinalIgnoreCase),
            NtfyEnabled = s.Ntfy.IsEnabled,
            NtfyTopic   = s.Ntfy.Topic,
            CategoryFilterEnabled = s.CategoryFilterEnabled,
            CategoryFilterOverrides = [.. s.CategoryFilterOverrides],
            AliasMap = new Dictionary<string, string>(s.AliasMap, StringComparer.OrdinalIgnoreCase),
                SuppressedAliasSuggestions = [.. s.SuppressedAliasSuggestions],
                UiWindowStates = new Dictionary<string, UiWindowState>(s.UiWindowStates, StringComparer.OrdinalIgnoreCase)
        };

        try
        {
            string json = JsonSerializer.Serialize(dto);
            string tmp = _settingsFilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _settingsFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsPersistence] Save fault: {ex}");
        }
    }

    // ── DTO ───────────────────────────────────────────────────────────────

    private sealed record SettingsDto
    {
        public int Version { get; set; } = 1;
        public int Low { get; set; }
        public int High { get; set; }
        public int LaptopLow { get; set; }
        public int LaptopHigh { get; set; }
        public List<string>? IgnoredDevices { get; set; }
        public List<string>? TrayIconOverlayExcludedDevices { get; set; }
        public bool ExcludeLaptopFromTrayIconOverlay { get; set; }
        public bool AutoStartUseScheduledTaskFallback { get; set; }
        public Dictionary<string, DeviceThresholds>? DeviceOverrides { get; set; }
        public Dictionary<string, int>? DevicePollIntervals { get; set; }
        public Dictionary<string, string>? DeviceDisplayNameAliases { get; set; }
        public bool    NtfyEnabled { get; set; }
        public string? NtfyTopic   { get; set; }
        // ADR-016
        public bool CategoryFilterEnabled { get; set; } = true;
        public List<string>? CategoryFilterOverrides { get; set; }
        // ADR-015
        public Dictionary<string, string>? AliasMap { get; set; }
            // Suppressions for alias suggestions
            public List<string>? SuppressedAliasSuggestions { get; set; }
            public Dictionary<string, UiWindowState>? UiWindowStates { get; set; }
    }
}
