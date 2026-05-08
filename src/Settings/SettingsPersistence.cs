using System.Text.Json;

namespace BTChargeTrayWatcher;

/// <summary>
/// Owns all JSON serialisation, file-path construction, and atomic disk writes
/// for <see cref="ThresholdSettings"/>. The domain class is kept free of I/O.
/// </summary>
internal sealed class SettingsPersistence
{
    private const string AppName = "BTChargeTrayWatcher";
    private readonly string _settingsFilePath;
    private readonly ThresholdSettings _settings;

    public SettingsPersistence(ThresholdSettings settings)
    {
        _settings = settings;

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string appDir = Path.Combine(localAppData, AppName);
        Directory.CreateDirectory(appDir);
        _settingsFilePath = Path.Combine(appDir, "settings.json");

        // Auto-save on every domain mutation.
        _settings.Changed += OnChanged;
    }

    private void OnChanged() => Save();

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>Populates <paramref name="settings"/> from disk. Safe to call once at startup before any consumers are wired.</summary>
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
                    overrides[kvp.Key] = t;
                }
            }

            // Suppress auto-save during bulk load by unsubscribing temporarily.
            _settings.Changed -= OnChanged;
            try
            {
                _settings.ApplySnapshot(new SettingsSnapshot(
                    low, high, laptopLow, laptopHigh,
                    ignored, excluded,
                    dto.ExcludeLaptopFromTrayIconOverlay,
                    overrides));
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

    // ── Private ──────────────────────────────────────────────────────────────

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
            DeviceOverrides = new Dictionary<string, DeviceThresholds>(s.DeviceOverrides, StringComparer.OrdinalIgnoreCase)
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

    // ── DTO (private; only SettingsPersistence ever touches it) ──────────────

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
        public Dictionary<string, DeviceThresholds>? DeviceOverrides { get; set; }
    }
}
