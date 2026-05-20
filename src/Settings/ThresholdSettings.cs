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
    // Backwards-compatible: device overrides and poll intervals may be keyed by
    // either display name or stable device id. New APIs prefer device id.
    // ── Per-device poll interval ─────────────────────────────────────────────

    public int? GetPollInterval(string deviceName)
    {
        lock (_thresholdLock)
            return _devicePollIntervals.TryGetValue(deviceName, out var interval) ? interval : (int?)null;
    }

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

    public bool HasCustomPollInterval(string deviceName)
    {
        lock (_thresholdLock)
            return _devicePollIntervals.ContainsKey(deviceName);
    }

    // ── Device-id-aware APIs (preferred) ───────────────────────────────────

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

            t.Low = value;
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

            t.High = value;
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
        {
            return _ignoredDevices.Contains(deviceId) || _ignoredDevices.Contains(displayName);
        }
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

    // ── Global thresholds ────────────────────────────────────────────────────

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

    // ── ntfy integration ─────────────────────────────────────────────────────

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

    // ── Per-device overrides ─────────────────────────────────────────────────

    public int GetLow(string deviceName)
    {
        lock (_thresholdLock)
            return _deviceOverrides.TryGetValue(deviceName, out var t) && t.Low.HasValue ? t.Low.Value : _low;
    }

    public int GetHigh(string deviceName)
    {
        lock (_thresholdLock)
            return _deviceOverrides.TryGetValue(deviceName, out var t) && t.High.HasValue ? t.High.Value : _high;
    }

    public bool HasCustomLow(string deviceName)
    {
        lock (_thresholdLock)
            return _deviceOverrides.TryGetValue(deviceName, out var t) && t.Low.HasValue;
    }

    public bool HasCustomHigh(string deviceName)
    {
        lock (_thresholdLock)
            return _deviceOverrides.TryGetValue(deviceName, out var t) && t.High.HasValue;
    }

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
                int effectiveHigh = GetHigh(deviceName);
                if (value.Value >= effectiveHigh)
                    throw new ArgumentOutOfRangeException(nameof(value),
                        $"Low threshold ({value.Value}) must be below effective High threshold ({effectiveHigh}) for device '{deviceName}'.");
            }

            t.Low = value;
            CleanupEmptyOverrides(deviceName);
        }
        Changed?.Invoke();
    }

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
                int effectiveLow = GetLow(deviceName);
                if (value.Value <= effectiveLow)
                    throw new ArgumentOutOfRangeException(nameof(value),
                        $"High threshold ({value.Value}) must be above effective Low threshold ({effectiveLow}) for device '{deviceName}'.");
            }

            t.High = value;
            CleanupEmptyOverrides(deviceName);
        }
        Changed?.Invoke();
    }

    private void CleanupEmptyOverrides(string deviceName)
    {
        if (_deviceOverrides.TryGetValue(deviceName, out var t))
            if (!t.Low.HasValue && !t.High.HasValue)
                _deviceOverrides.Remove(deviceName);
    }

    // ── Device sets ──────────────────────────────────────────────────────────

    public IReadOnlyCollection<string> IgnoredDevices
    {
        get { lock (_thresholdLock) return new HashSet<string>(_ignoredDevices, StringComparer.OrdinalIgnoreCase); }
    }

    public IReadOnlyCollection<string> TrayIconOverlayExcludedDevices
    {
        get { lock (_thresholdLock) return new HashSet<string>(_trayIconOverlayExcludedDevices, StringComparer.OrdinalIgnoreCase); }
    }

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
        {
            return _displayNameAliases.TryGetValue(deviceId, out var a) ? a : defaultName;
        }
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

    // ── Internal snapshot used by SettingsPersistence ────────────────────────

    internal SettingsSnapshot Snapshot()
    {
        lock (_thresholdLock)
        {
            return new SettingsSnapshot(
                _low, _high, _laptopLow, _laptopHigh,
                new HashSet<string>(_ignoredDevices, StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(_trayIconOverlayExcludedDevices, StringComparer.OrdinalIgnoreCase),
                _excludeLaptopFromTrayIconOverlay,
                new Dictionary<string, DeviceThresholds>(_deviceOverrides, StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, int>(_devicePollIntervals, StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>(_displayNameAliases, StringComparer.OrdinalIgnoreCase),
                _ntfy.Clone());
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
        }
    }
}

// ── Shared types ─────────────────────────────────────────────────────────────

public sealed record DeviceThresholds
{
    public int? Low  { get; set; }
    public int? High { get; set; }
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
    NtfyIntegrationSettings Ntfy);
