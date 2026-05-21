namespace BTChargeTrayWatcher;

public sealed class ThresholdSettings
{
    private readonly Lock _thresholdLock = new();

    private int _low;
    private int _high;
    private int _laptopLow;
    private int _laptopHigh;
    private HashSet<string> _ignoredDevices = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _trayIconOverlayExcludedDevices = new(StringComparer.OrdinalIgnoreCase);
    private bool _excludeLaptopFromTrayIconOverlay;
    private Dictionary<string, DeviceThresholds> _deviceOverrides = new(StringComparer.OrdinalIgnoreCase);

    private NtfyIntegrationSettings _ntfy = new();

    // Per-device poll interval (seconds)
    private Dictionary<string, int> _devicePollIntervals = new(StringComparer.OrdinalIgnoreCase);
    // Optional user-specified display name aliases keyed by device id
    private Dictionary<string, string> _displayNameAliases = new(StringComparer.OrdinalIgnoreCase);

    // ADR-016: category filter
    private bool _categoryFilterEnabled = true;
    private HashSet<string> _categoryFilterOverrides = new(StringComparer.OrdinalIgnoreCase);

    // ADR-015: alias map — historical name variant (any casing) → canonical DeviceId
    private Dictionary<string, string> _aliasMap = new(StringComparer.OrdinalIgnoreCase);
    
    // Per-device suppression for alias suggestions (persisted)
    private HashSet<string> _suppressedAliasSuggestions = new(StringComparer.OrdinalIgnoreCase);

    // ── ADR-015: alias map ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a snapshot of the alias map (historical name variant → canonical DeviceId).
    /// </summary>
    public IReadOnlyDictionary<string, string> AliasMap
    {
        get
        {
            lock (_thresholdLock)
                return new Dictionary<string, string>(_aliasMap, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Replaces the alias map wholesale. Pass an empty enumerable to clear all aliases.
    /// </summary>
    public void SetAliasMap(IEnumerable<KeyValuePair<string, string>> entries)
    {
        lock (_thresholdLock)
        {
            _aliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in entries)
                if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                    _aliasMap[kv.Key] = kv.Value;
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Adds or updates a single alias entry. Raises <see cref="Changed"/>.
    /// </summary>
    public void AddAlias(string nameVariant, string canonicalDeviceId)
    {
        if (string.IsNullOrWhiteSpace(nameVariant)) throw new ArgumentException("Name variant must not be empty.", nameof(nameVariant));
        if (string.IsNullOrWhiteSpace(canonicalDeviceId)) throw new ArgumentException("Canonical device ID must not be empty.", nameof(canonicalDeviceId));
        lock (_thresholdLock)
            _aliasMap[nameVariant] = canonicalDeviceId;
        Changed?.Invoke();
    }

    /// <summary>
    /// Removes an alias entry by its name variant key. No-op if the key is absent.
    /// </summary>
    public void RemoveAlias(string nameVariant)
    {
        bool changed;
        lock (_thresholdLock)
            changed = _aliasMap.Remove(nameVariant);
        if (changed) Changed?.Invoke();
    }

    // ── ADR-016: category filter ─────────────────────────────────────────────────────

    /// <summary>
    /// When <c>true</c> (default), <see cref="BatteryReaderOrchestrator"/> excludes devices
    /// whose <see cref="DeviceBatteryInfo.Category"/> is a known but non-battery-bearing
    /// category. Set to <c>false</c> to pass all devices through regardless of category.
    /// </summary>
    public bool CategoryFilterEnabled
    {
        get { lock (_thresholdLock) return _categoryFilterEnabled; }
    }

    public void SetCategoryFilterEnabled(bool value)
    {
        lock (_thresholdLock)
        {
            if (_categoryFilterEnabled == value) return;
            _categoryFilterEnabled = value;
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Returns a snapshot of the device IDs that bypass the category filter.
    /// </summary>
    public IReadOnlyCollection<string> CategoryFilterOverrides
    {
        get { lock (_thresholdLock) return new HashSet<string>(_categoryFilterOverrides, StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="deviceId"/> is present in the
    /// category filter override set and should bypass filtering unconditionally.
    /// </summary>
    public bool IsCategoryFilterOverridden(string deviceId)
    {
        lock (_thresholdLock) return _categoryFilterOverrides.Contains(deviceId);
    }

    public void SetCategoryFilterOverrides(IEnumerable<string> deviceIds)
    {
        lock (_thresholdLock)
            _categoryFilterOverrides = new HashSet<string>(deviceIds, StringComparer.OrdinalIgnoreCase);
        Changed?.Invoke();
    }

    // ── Per-device poll interval (legacy name-keyed API) ─────────────────────────────
    // Prefer GetPollIntervalForDevice / SetPollIntervalForDevice (device-id-keyed).

    [Obsolete("Use GetPollIntervalForDevice(deviceId, displayName) instead.")]
    public int? GetPollInterval(string deviceName)
    {
        lock (_thresholdLock)
            return _devicePollIntervals.TryGetValue(deviceName, out var interval) ? interval : (int?)null;
    }

    [Obsolete("Use SetPollIntervalForDevice(deviceId, interval) instead.")]
    public void SetPollInterval(string deviceName, int? interval)
    {
        lock (_thresholdLock)
        {
            if (interval.HasValue)
                _devicePollIntervals[deviceName] = interval.Value;
            else
                _devicePollIntervals.Remove(deviceName);
        }
        Changed?.Invoke();
    }

    [Obsolete("Use HasCustomPollIntervalForDevice(deviceId) instead.")]
    public bool HasCustomPollInterval(string deviceName)
    {
        lock (_thresholdLock)
            return _devicePollIntervals.ContainsKey(deviceName);
    }

    // ── Device-id-aware APIs (preferred) ────────────────────────────────────────────

    public int GetLowForDevice(string deviceId, string displayName)
    {
        lock (_thresholdLock)
        {
            if (_deviceOverrides.TryGetValue(deviceId, out var t) && t.Low.HasValue) return t.Low.Value;
            if (_deviceOverrides.TryGetValue(displayName, out var t2) && t2.Low.HasValue) return t2.Low.Value;
            return _low;
        }
    }

    public int GetHighForDevice(string deviceId, string displayName)
    {
        lock (_thresholdLock)
        {
            if (_deviceOverrides.TryGetValue(deviceId, out var t) && t.High.HasValue) return t.High.Value;
            if (_deviceOverrides.TryGetValue(displayName, out var t2) && t2.High.HasValue) return t2.High.Value;
            return _high;
        }
    }

    public void SetLowForDevice(string deviceId, int? value)
    {
        lock (_thresholdLock)
        {
            if (!_deviceOverrides.TryGetValue(deviceId, out var t))
            {
                if (value == null) return;
                t = new DeviceThresholds();
                _deviceOverrides[deviceId] = t;
            }

            if (value.HasValue)
            {
                int effectiveHigh = GetHighForDevice(deviceId, deviceId);
                if (value.Value >= effectiveHigh)
                    throw new ArgumentOutOfRangeException(nameof(value),
                        $"Low threshold ({value.Value}) must be below effective High threshold ({effectiveHigh}) for device '{deviceId}'.");
            }

            t = t with { Low = value };
            _deviceOverrides[deviceId] = t;
            if (!t.Low.HasValue && !t.High.HasValue)
                _deviceOverrides.Remove(deviceId);
        }
        Changed?.Invoke();
    }

    public void SetHighForDevice(string deviceId, int? value)
    {
        lock (_thresholdLock)
        {
            if (!_deviceOverrides.TryGetValue(deviceId, out var t))
            {
                if (value == null) return;
                t = new DeviceThresholds();
                _deviceOverrides[deviceId] = t;
            }

            if (value.HasValue)
            {
                int effectiveLow = GetLowForDevice(deviceId, deviceId);
                if (value.Value <= effectiveLow)
                    throw new ArgumentOutOfRangeException(nameof(value),
                        $"High threshold ({value.Value}) must be above effective Low threshold ({effectiveLow}) for device '{deviceId}'.");
            }

            t = t with { High = value };
            _deviceOverrides[deviceId] = t;
            if (!t.Low.HasValue && !t.High.HasValue)
                _deviceOverrides.Remove(deviceId);
        }
        Changed?.Invoke();
    }

    public int? GetPollIntervalForDevice(string deviceId, string displayName)
    {
        lock (_thresholdLock)
        {
            if (_devicePollIntervals.TryGetValue(deviceId, out var v)) return v;
            if (_devicePollIntervals.TryGetValue(displayName, out var v2)) return v2;
            return null;
        }
    }

    public void SetPollIntervalForDevice(string deviceId, int? interval)
    {
        lock (_thresholdLock)
        {
            if (interval.HasValue)
                _devicePollIntervals[deviceId] = interval.Value;
            else
                _devicePollIntervals.Remove(deviceId);
        }
        Changed?.Invoke();
    }

    public bool HasCustomPollIntervalForDevice(string deviceId)
    {
        lock (_thresholdLock) return _devicePollIntervals.ContainsKey(deviceId);
    }

    public bool IsIgnored(string deviceId, string displayName)
    {
        lock (_thresholdLock)
            return _ignoredDevices.Contains(deviceId) || _ignoredDevices.Contains(displayName);
    }

    public void SetIgnoredDevicesByIds(IEnumerable<string> devices)
    {
        lock (_thresholdLock)
            _ignoredDevices = new HashSet<string>(devices, StringComparer.OrdinalIgnoreCase);
        Changed?.Invoke();
    }

    public event Action? Changed;
    public event Action? LaptopSettingsChanged;

    public ThresholdSettings()
    {
        _low = 20;
        _high = 80;
        _laptopLow = 20;
        _laptopHigh = 80;
    }

    /// <summary>
    /// Returns true when alias suggestions for the specified deviceId have been
    /// suppressed by the user and should not be emitted.
    /// </summary>
    public bool IsAliasSuggestionSuppressed(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;
        lock (_thresholdLock) return _suppressedAliasSuggestions.Contains(deviceId);
    }

    /// <summary>
    /// Persistently suppress alias suggestions for the given device id.
    /// </summary>
    public void SuppressAliasSuggestion(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return;
        lock (_thresholdLock)
        {
            if (!_suppressedAliasSuggestions.Add(deviceId)) return;
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Remove suppression for the given device id.
    /// </summary>
    public void UnsuppressAliasSuggestion(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return;
        lock (_thresholdLock)
        {
            if (!_suppressedAliasSuggestions.Remove(deviceId)) return;
        }
        Changed?.Invoke();
    }

    // ── Global thresholds ────────────────────────────────────────────────────────────

    public int Low
    {
        get { lock (_thresholdLock) return _low; }
        set
        {
            lock (_thresholdLock)
            {
                if (_low == value) return;
                if (value >= _high) throw new ArgumentOutOfRangeException(nameof(value), "Low threshold must be below High threshold.");
                _low = value;
            }
            Changed?.Invoke();
        }
    }

    public int High
    {
        get { lock (_thresholdLock) return _high; }
        set
        {
            lock (_thresholdLock)
            {
                if (_high == value) return;
                if (value <= _low) throw new ArgumentOutOfRangeException(nameof(value), "High threshold must be above Low threshold.");
                _high = value;
            }
            Changed?.Invoke();
        }
    }

    public int LaptopLow
    {
        get { lock (_thresholdLock) return _laptopLow; }
        set
        {
            lock (_thresholdLock)
            {
                if (_laptopLow == value) return;
                if (value >= _laptopHigh) throw new ArgumentOutOfRangeException(nameof(value), "Laptop Low threshold must be below Laptop High threshold.");
                _laptopLow = value;
            }
            Changed?.Invoke();
            LaptopSettingsChanged?.Invoke();
        }
    }

    public int LaptopHigh
    {
        get { lock (_thresholdLock) return _laptopHigh; }
        set
        {
            lock (_thresholdLock)
            {
                if (_laptopHigh == value) return;
                if (value <= _laptopLow) throw new ArgumentOutOfRangeException(nameof(value), "Laptop High threshold must be above Laptop Low threshold.");
                _laptopHigh = value;
            }
            Changed?.Invoke();
            LaptopSettingsChanged?.Invoke();
        }
    }

    // ── Tray icon overlay exclusion ──────────────────────────────────────────────────

    public bool ExcludeLaptopFromTrayIconOverlay
    {
        get { lock (_thresholdLock) return _excludeLaptopFromTrayIconOverlay; }
        set
        {
            lock (_thresholdLock)
            {
                if (_excludeLaptopFromTrayIconOverlay == value) return;
                _excludeLaptopFromTrayIconOverlay = value;
            }
            Changed?.Invoke();
        }
    }

    public bool IsTrayIconOverlayExcluded(string deviceId, string displayName)
    {
        lock (_thresholdLock)
            return _trayIconOverlayExcludedDevices.Contains(deviceId)
                || _trayIconOverlayExcludedDevices.Contains(displayName);
    }

    public void SetTrayIconOverlayExcludedDevices(IEnumerable<string> devices)
    {
        lock (_thresholdLock)
            _trayIconOverlayExcludedDevices = new HashSet<string>(devices, StringComparer.OrdinalIgnoreCase);
        Changed?.Invoke();
    }

    // ── Device sets ──────────────────────────────────────────────────────────────────

    public IReadOnlyCollection<string> IgnoredDevices
    {
        get { lock (_thresholdLock) return new HashSet<string>(_ignoredDevices, StringComparer.OrdinalIgnoreCase); }
    }

    public IReadOnlyCollection<string> TrayIconOverlayExcludedDevices
    {
        get { lock (_thresholdLock) return new HashSet<string>(_trayIconOverlayExcludedDevices, StringComparer.OrdinalIgnoreCase); }
    }

    public void SetIgnoredDevices(IEnumerable<string> devices)
    {
        lock (_thresholdLock)
            _ignoredDevices = new HashSet<string>(devices, StringComparer.OrdinalIgnoreCase);
        Changed?.Invoke();
    }

    public void SetDisplayNameAlias(string deviceId, string? alias)
    {
        lock (_thresholdLock)
        {
            if (string.IsNullOrWhiteSpace(alias))
                _displayNameAliases.Remove(deviceId);
            else
                _displayNameAliases[deviceId] = alias!;
        }
        Changed?.Invoke();
    }

    public string GetDisplayName(string deviceId, string defaultName)
    {
        lock (_thresholdLock)
            return _displayNameAliases.TryGetValue(deviceId, out var a) ? a : defaultName;
    }

    public void ToggleIgnoreDevice(string deviceName)
    {
        lock (_thresholdLock)
        {
            if (!_ignoredDevices.Remove(deviceName))
                _ignoredDevices.Add(deviceName);
        }
        Changed?.Invoke();
    }

    public void ToggleExcludeFromTrayIconOverlay(string deviceName)
    {
        lock (_thresholdLock)
        {
            if (!_trayIconOverlayExcludedDevices.Remove(deviceName))
                _trayIconOverlayExcludedDevices.Add(deviceName);
        }
        Changed?.Invoke();
    }

    // ── ntfy integration ─────────────────────────────────────────────────────────────

    /// <summary>Returns a snapshot copy of the current ntfy settings. Never null.</summary>
    public NtfyIntegrationSettings GetNtfySettings()
    {
        lock (_thresholdLock) return _ntfy.Clone();
    }

    public void UpdateNtfySettings(Action<NtfyIntegrationSettings> mutate)
    {
        lock (_thresholdLock) mutate(_ntfy);
        Changed?.Invoke();
    }

    // ── Per-device overrides (legacy name-keyed API) ─────────────────────────────────
    // Prefer SetLowForDevice / SetHighForDevice (device-id-keyed).

    [Obsolete("Use GetLowForDevice(deviceId, displayName) instead.")]
    public int GetLow(string deviceName)
    {
        lock (_thresholdLock)
            return _deviceOverrides.TryGetValue(deviceName, out var t) && t.Low.HasValue ? t.Low.Value : _low;
    }

    [Obsolete("Use GetHighForDevice(deviceId, displayName) instead.")]
    public int GetHigh(string deviceName)
    {
        lock (_thresholdLock)
            return _deviceOverrides.TryGetValue(deviceName, out var t) && t.High.HasValue ? t.High.Value : _high;
    }

    [Obsolete("Use GetLowForDevice(deviceId, displayName) to check for a custom value.")]
    public bool HasCustomLow(string deviceName)
    {
        lock (_thresholdLock)
            return _deviceOverrides.TryGetValue(deviceName, out var t) && t.Low.HasValue;
    }

    [Obsolete("Use GetHighForDevice(deviceId, displayName) to check for a custom value.")]
    public bool HasCustomHigh(string deviceName)
    {
        lock (_thresholdLock)
            return _deviceOverrides.TryGetValue(deviceName, out var t) && t.High.HasValue;
    }

    [Obsolete("Use SetLowForDevice(deviceId, value) instead.")]
    public void SetLow(string deviceName, int? value)
    {
        lock (_thresholdLock)
        {
            if (!_deviceOverrides.TryGetValue(deviceName, out var t))
            {
                if (value == null) return;
                t = new DeviceThresholds();
                _deviceOverrides[deviceName] = t;
            }

            if (value.HasValue)
            {
#pragma warning disable CS0618
                int effectiveHigh = GetHigh(deviceName);
#pragma warning restore CS0618
                if (value.Value >= effectiveHigh)
                    throw new ArgumentOutOfRangeException(nameof(value),
                        $"Low threshold ({value.Value}) must be below effective High threshold ({effectiveHigh}) for device '{deviceName}'.");
            }

            t = t with { Low = value };
            _deviceOverrides[deviceName] = t;
            if (!t.Low.HasValue && !t.High.HasValue)
                _deviceOverrides.Remove(deviceName);
        }
        Changed?.Invoke();
    }

    [Obsolete("Use SetHighForDevice(deviceId, value) instead.")]
    public void SetHigh(string deviceName, int? value)
    {
        lock (_thresholdLock)
        {
            if (!_deviceOverrides.TryGetValue(deviceName, out var t))
            {
                if (value == null) return;
                t = new DeviceThresholds();
                _deviceOverrides[deviceName] = t;
            }

            if (value.HasValue)
            {
#pragma warning disable CS0618
                int effectiveLow = GetLow(deviceName);
#pragma warning restore CS0618
                if (value.Value <= effectiveLow)
                    throw new ArgumentOutOfRangeException(nameof(value),
                        $"High threshold ({value.Value}) must be above effective Low threshold ({effectiveLow}) for device '{deviceName}'.");
            }

            t = t with { High = value };
            _deviceOverrides[deviceName] = t;
            if (!t.Low.HasValue && !t.High.HasValue)
                _deviceOverrides.Remove(deviceName);
        }
        Changed?.Invoke();
    }

    // ── Snapshot / restore ───────────────────────────────────────────────────────────

    internal SettingsSnapshot Snapshot()
    {
        lock (_thresholdLock)
        {
            var overridesCopy = new Dictionary<string, DeviceThresholds>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in _deviceOverrides)
                overridesCopy[kvp.Key] = kvp.Value with { };

            return new SettingsSnapshot(
                _low, _high, _laptopLow, _laptopHigh,
                new HashSet<string>(_ignoredDevices, StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(_trayIconOverlayExcludedDevices, StringComparer.OrdinalIgnoreCase),
                _excludeLaptopFromTrayIconOverlay,
                overridesCopy,
                new Dictionary<string, int>(_devicePollIntervals, StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>(_displayNameAliases, StringComparer.OrdinalIgnoreCase),
                _ntfy.Clone(),
                _categoryFilterEnabled,
                new HashSet<string>(_categoryFilterOverrides, StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>(_aliasMap, StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(_suppressedAliasSuggestions, StringComparer.OrdinalIgnoreCase));
        }
    }

    internal void ApplySnapshot(SettingsSnapshot s)
    {
        lock (_thresholdLock)
        {
            _low = s.Low;
            _high = s.High;
            _laptopLow = s.LaptopLow;
            _laptopHigh = s.LaptopHigh;
            _ignoredDevices = s.IgnoredDevices;
            _trayIconOverlayExcludedDevices = s.TrayIconOverlayExcludedDevices;
            _excludeLaptopFromTrayIconOverlay = s.ExcludeLaptopFromTrayIconOverlay;
            _deviceOverrides = s.DeviceOverrides;
            _devicePollIntervals = s.DevicePollIntervals;
            _displayNameAliases = s.DeviceDisplayNameAliases;
            _ntfy = s.Ntfy;
            _categoryFilterEnabled = s.CategoryFilterEnabled;
            _categoryFilterOverrides = s.CategoryFilterOverrides;
            _aliasMap = s.AliasMap;
            _suppressedAliasSuggestions = s.SuppressedAliasSuggestions;
        }
    }
}

// ── Shared types ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Per-device low/high battery threshold overrides.
/// Use init-only setters to preserve value semantics; mutate via 'with' expressions.
/// </summary>
public sealed record DeviceThresholds
{
    public int? Low  { get; init; }
    public int? High { get; init; }
}

internal sealed record SettingsSnapshot(
    int Low,
    int High,
    int LaptopLow,
    int LaptopHigh,
    HashSet<string> IgnoredDevices,
    HashSet<string> TrayIconOverlayExcludedDevices,
    bool ExcludeLaptopFromTrayIconOverlay,
    Dictionary<string, DeviceThresholds> DeviceOverrides,
    Dictionary<string, int> DevicePollIntervals,
    Dictionary<string, string> DeviceDisplayNameAliases,
    NtfyIntegrationSettings Ntfy,
    bool CategoryFilterEnabled,
    HashSet<string> CategoryFilterOverrides,
    Dictionary<string, string> AliasMap,
    HashSet<string> SuppressedAliasSuggestions);
