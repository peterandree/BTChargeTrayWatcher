namespace BTChargeTrayWatcher;

/// <summary>
/// Pure domain model for all user-configurable battery thresholds, device
/// overrides, aliases, and feature flags.
///
/// Threading: all mutations acquire <c>_lock</c>.  Events are dispatched via
/// <see cref="SettingsEventBus.Raise"/> AFTER releasing the lock so that
/// subscribers can safely call back into this class without deadlocking
/// (fixes the re-entrancy risk identified in #130 / #134).
/// </summary>
public sealed class ThresholdSettings
{
    private readonly Lock _lock = new();
    internal readonly SettingsEventBus Bus = new();

    // ── Convenience forwarders (preserve existing public API surface) ────────────────
    // These allow existing callers that subscribed to ThresholdSettings.Changed /
    // LaptopSettingsChanged to keep working without modification.
    public event Action? Changed
    {
        add    => Bus.Changed += value;
        remove => Bus.Changed -= value;
    }

    public event Action? LaptopSettingsChanged
    {
        add    => Bus.LaptopSettingsChanged += value;
        remove => Bus.LaptopSettingsChanged -= value;
    }

    private int _low;
    private int _high;
    private int _laptopLow;
    private int _laptopHigh;
    private HashSet<string> _ignoredDevices = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _trayIconOverlayExcludedDevices = new(StringComparer.OrdinalIgnoreCase);
    private bool _excludeLaptopFromTrayIconOverlay;
    private Dictionary<string, DeviceThresholds> _deviceOverrides = new(StringComparer.OrdinalIgnoreCase);
    private NtfyIntegrationSettings _ntfy = new();
    private Dictionary<string, int> _devicePollIntervals = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _displayNameAliases = new(StringComparer.OrdinalIgnoreCase);
    private bool _categoryFilterEnabled = true;
    private HashSet<string> _categoryFilterOverrides = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _aliasMap = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _suppressedAliasSuggestions = new(StringComparer.OrdinalIgnoreCase);

    public ThresholdSettings()
    {
        _low        = 20;
        _high       = 80;
        _laptopLow  = 20;
        _laptopHigh = 80;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────

    // Pattern: every mutation
    //   1. acquires _lock
    //   2. computes PendingRaise inside the lock
    //   3. releases the lock
    //   4. calls Bus.Raise(pending) — outside the lock

    private void RaiseChanged() =>
        Bus.Raise(SettingsEventBus.PendingRaise.Changed);

    private void RaiseLaptopChanged() =>
        Bus.Raise(SettingsEventBus.PendingRaise.Changed | SettingsEventBus.PendingRaise.LaptopSettingsChanged);

    // ── Global thresholds ────────────────────────────────────────────────────────────

    public int Low
    {
        get { lock (_lock) return _low; }
        set
        {
            bool changed;
            lock (_lock)
            {
                if (_low == value) return;
                if (value >= _high) throw new ArgumentOutOfRangeException(nameof(value), "Low threshold must be below High threshold.");
                _low = value;
                changed = true;
            }
            if (changed) RaiseChanged();
        }
    }

    public int High
    {
        get { lock (_lock) return _high; }
        set
        {
            bool changed;
            lock (_lock)
            {
                if (_high == value) return;
                if (value <= _low) throw new ArgumentOutOfRangeException(nameof(value), "High threshold must be above Low threshold.");
                _high = value;
                changed = true;
            }
            if (changed) RaiseChanged();
        }
    }

    public int LaptopLow
    {
        get { lock (_lock) return _laptopLow; }
        set
        {
            bool changed;
            lock (_lock)
            {
                if (_laptopLow == value) return;
                if (value >= _laptopHigh) throw new ArgumentOutOfRangeException(nameof(value), "Laptop Low threshold must be below Laptop High threshold.");
                _laptopLow = value;
                changed = true;
            }
            if (changed) RaiseLaptopChanged();
        }
    }

    public int LaptopHigh
    {
        get { lock (_lock) return _laptopHigh; }
        set
        {
            bool changed;
            lock (_lock)
            {
                if (_laptopHigh == value) return;
                if (value <= _laptopLow) throw new ArgumentOutOfRangeException(nameof(value), "Laptop High threshold must be above Laptop Low threshold.");
                _laptopHigh = value;
                changed = true;
            }
            if (changed) RaiseLaptopChanged();
        }
    }

    // ── Tray icon overlay exclusion ──────────────────────────────────────────────────

    public bool ExcludeLaptopFromTrayIconOverlay
    {
        get { lock (_lock) return _excludeLaptopFromTrayIconOverlay; }
        set
        {
            bool changed;
            lock (_lock)
            {
                if (_excludeLaptopFromTrayIconOverlay == value) return;
                _excludeLaptopFromTrayIconOverlay = value;
                changed = true;
            }
            if (changed) RaiseChanged();
        }
    }

    public bool IsTrayIconOverlayExcluded(string deviceId, string displayName)
    {
        lock (_lock)
            return _trayIconOverlayExcludedDevices.Contains(deviceId)
                || _trayIconOverlayExcludedDevices.Contains(displayName);
    }

    public void SetTrayIconOverlayExcludedDevices(IEnumerable<string> devices)
    {
        lock (_lock)
            _trayIconOverlayExcludedDevices = new HashSet<string>(devices, StringComparer.OrdinalIgnoreCase);
        RaiseChanged();
    }

    // ── Device sets ──────────────────────────────────────────────────────────────────

    public IReadOnlyCollection<string> IgnoredDevices
    {
        get { lock (_lock) return new HashSet<string>(_ignoredDevices, StringComparer.OrdinalIgnoreCase); }
    }

    public IReadOnlyCollection<string> TrayIconOverlayExcludedDevices
    {
        get { lock (_lock) return new HashSet<string>(_trayIconOverlayExcludedDevices, StringComparer.OrdinalIgnoreCase); }
    }

    public bool IsIgnored(string deviceId, string displayName)
    {
        lock (_lock)
            return _ignoredDevices.Contains(deviceId) || _ignoredDevices.Contains(displayName);
    }

    public void SetIgnoredDevices(IEnumerable<string> devices)
    {
        lock (_lock)
            _ignoredDevices = new HashSet<string>(devices, StringComparer.OrdinalIgnoreCase);
        RaiseChanged();
    }

    public void SetIgnoredDevicesByIds(IEnumerable<string> devices)
    {
        lock (_lock)
            _ignoredDevices = new HashSet<string>(devices, StringComparer.OrdinalIgnoreCase);
        RaiseChanged();
    }

    public void ToggleIgnoreDevice(string deviceName)
    {
        lock (_lock)
        {
            if (!_ignoredDevices.Remove(deviceName))
                _ignoredDevices.Add(deviceName);
        }
        RaiseChanged();
    }

    public void ToggleExcludeFromTrayIconOverlay(string deviceName)
    {
        lock (_lock)
        {
            if (!_trayIconOverlayExcludedDevices.Remove(deviceName))
                _trayIconOverlayExcludedDevices.Add(deviceName);
        }
        RaiseChanged();
    }

    // ── Device-id-aware threshold APIs ───────────────────────────────────────────────

    public int GetLowForDevice(string deviceId, string displayName)
    {
        lock (_lock)
        {
            if (_deviceOverrides.TryGetValue(deviceId, out var t) && t.Low.HasValue) return t.Low.Value;
            if (_deviceOverrides.TryGetValue(displayName, out var t2) && t2.Low.HasValue) return t2.Low.Value;
            return _low;
        }
    }

    public int GetHighForDevice(string deviceId, string displayName)
    {
        lock (_lock)
        {
            if (_deviceOverrides.TryGetValue(deviceId, out var t) && t.High.HasValue) return t.High.Value;
            if (_deviceOverrides.TryGetValue(displayName, out var t2) && t2.High.HasValue) return t2.High.Value;
            return _high;
        }
    }

    public void SetLowForDevice(string deviceId, int? value)
    {
        lock (_lock)
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
        RaiseChanged();
    }

    public void SetHighForDevice(string deviceId, int? value)
    {
        lock (_lock)
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
        RaiseChanged();
    }

    // ── Per-device poll intervals ────────────────────────────────────────────────────

    public int? GetPollIntervalForDevice(string deviceId, string displayName)
    {
        lock (_lock)
        {
            if (_devicePollIntervals.TryGetValue(deviceId, out var v)) return v;
            if (_devicePollIntervals.TryGetValue(displayName, out var v2)) return v2;
            return null;
        }
    }

    public void SetPollIntervalForDevice(string deviceId, int? interval)
    {
        lock (_lock)
        {
            if (interval.HasValue)
                _devicePollIntervals[deviceId] = interval.Value;
            else
                _devicePollIntervals.Remove(deviceId);
        }
        RaiseChanged();
    }

    public bool HasCustomPollIntervalForDevice(string deviceId)
    {
        lock (_lock) return _devicePollIntervals.ContainsKey(deviceId);
    }

    // ── Display name aliases ─────────────────────────────────────────────────────────

    public void SetDisplayNameAlias(string deviceId, string? alias)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(alias))
                _displayNameAliases.Remove(deviceId);
            else
                _displayNameAliases[deviceId] = alias!;
        }
        RaiseChanged();
    }

    public string GetDisplayName(string deviceId, string defaultName)
    {
        lock (_lock)
            return _displayNameAliases.TryGetValue(deviceId, out var a) ? a : defaultName;
    }

    // ── ntfy integration ─────────────────────────────────────────────────────────────

    /// <summary>Returns a snapshot copy of the current ntfy settings. Never null.</summary>
    public NtfyIntegrationSettings GetNtfySettings()
    {
        lock (_lock) return _ntfy.Clone();
    }

    /// <summary>
    /// Applies mutations to the ntfy settings using copy-out / mutate / assign-back
    /// so the caller's delegate runs outside <c>_lock</c>, preventing re-entrant
    /// deadlocks (ADR-014 compliance, #134).
    /// </summary>
    public void UpdateNtfySettings(Action<NtfyIntegrationSettings> mutate)
    {
        NtfyIntegrationSettings copy;
        lock (_lock) copy = _ntfy.Clone();
        mutate(copy);
        lock (_lock) _ntfy = copy;
        RaiseChanged();
    }

    // ── ADR-016: category filter ─────────────────────────────────────────────────────

    public bool CategoryFilterEnabled
    {
        get { lock (_lock) return _categoryFilterEnabled; }
    }

    public void SetCategoryFilterEnabled(bool value)
    {
        bool changed;
        lock (_lock)
        {
            if (_categoryFilterEnabled == value) return;
            _categoryFilterEnabled = value;
            changed = true;
        }
        if (changed) RaiseChanged();
    }

    public IReadOnlyCollection<string> CategoryFilterOverrides
    {
        get { lock (_lock) return new HashSet<string>(_categoryFilterOverrides, StringComparer.OrdinalIgnoreCase); }
    }

    public bool IsCategoryFilterOverridden(string deviceId)
    {
        lock (_lock) return _categoryFilterOverrides.Contains(deviceId);
    }

    public void SetCategoryFilterOverrides(IEnumerable<string> deviceIds)
    {
        lock (_lock)
            _categoryFilterOverrides = new HashSet<string>(deviceIds, StringComparer.OrdinalIgnoreCase);
        RaiseChanged();
    }

    // ── ADR-015: alias map ───────────────────────────────────────────────────────────

    public IReadOnlyDictionary<string, string> AliasMap
    {
        get
        {
            lock (_lock)
                return new Dictionary<string, string>(_aliasMap, StringComparer.OrdinalIgnoreCase);
        }
    }

    public void SetAliasMap(IEnumerable<KeyValuePair<string, string>> entries)
    {
        lock (_lock)
        {
            _aliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in entries)
                if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                    _aliasMap[kv.Key] = kv.Value;
        }
        RaiseChanged();
    }

    public void AddAlias(string nameVariant, string canonicalDeviceId)
    {
        if (string.IsNullOrWhiteSpace(nameVariant)) throw new ArgumentException("Name variant must not be empty.", nameof(nameVariant));
        if (string.IsNullOrWhiteSpace(canonicalDeviceId)) throw new ArgumentException("Canonical device ID must not be empty.", nameof(canonicalDeviceId));
        lock (_lock)
            _aliasMap[nameVariant] = canonicalDeviceId;
        RaiseChanged();
    }

    public void RemoveAlias(string nameVariant)
    {
        bool changed;
        lock (_lock)
            changed = _aliasMap.Remove(nameVariant);
        if (changed) RaiseChanged();
    }

    // ── UI window geometry storage ───────────────────────────────────────────


    // ── Alias suggestion suppression ─────────────────────────────────────────────────

    public bool IsAliasSuggestionSuppressed(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;
        lock (_lock) return _suppressedAliasSuggestions.Contains(deviceId);
    }

    public void SuppressAliasSuggestion(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return;
        bool changed;
        lock (_lock)
            changed = _suppressedAliasSuggestions.Add(deviceId);
        if (changed) RaiseChanged();
    }

    public void UnsuppressAliasSuggestion(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return;
        bool changed;
        lock (_lock)
            changed = _suppressedAliasSuggestions.Remove(deviceId);
        if (changed) RaiseChanged();
    }

    // ── Snapshot / restore ───────────────────────────────────────────────────────────

    internal SettingsSnapshot Snapshot()
    {
        lock (_lock)
        {
            var overridesCopy = new Dictionary<string, DeviceThresholds>(StringComparer.OrdinalIgnoreCase);
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
                new HashSet<string>(_suppressedAliasSuggestions, StringComparer.OrdinalIgnoreCase),
                null);
        }
    }

    // ApplySnapshot does NOT raise Changed — callers (SettingsPersistence.Load)
    // intentionally suppress events during bulk load to avoid redundant saves.
    internal void ApplySnapshot(SettingsSnapshot s)
    {
        lock (_lock)
        {
            _low                              = s.Low;
            _high                             = s.High;
            _laptopLow                        = s.LaptopLow;
            _laptopHigh                       = s.LaptopHigh;
            _ignoredDevices                   = s.IgnoredDevices;
            _trayIconOverlayExcludedDevices   = s.TrayIconOverlayExcludedDevices;
            _excludeLaptopFromTrayIconOverlay = s.ExcludeLaptopFromTrayIconOverlay;
            _deviceOverrides                  = s.DeviceOverrides;
            _devicePollIntervals              = s.DevicePollIntervals;
            _displayNameAliases               = s.DeviceDisplayNameAliases;
            _ntfy                             = s.Ntfy;
            _categoryFilterEnabled            = s.CategoryFilterEnabled;
            _categoryFilterOverrides          = s.CategoryFilterOverrides;
            _aliasMap                         = s.AliasMap;
            _suppressedAliasSuggestions       = s.SuppressedAliasSuggestions;
            // _uiWindowStates removed; UI state now handled by UiSettings
        }
    }
}

// ── Shared types ─────────────────────────────────────────────────────────────────────

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
    HashSet<string> SuppressedAliasSuggestions,
    Dictionary<string, UiWindowState> UiWindowStates);
