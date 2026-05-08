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
                new Dictionary<string, DeviceThresholds>(_deviceOverrides, StringComparer.OrdinalIgnoreCase));
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
        }
    }
}

// ── Shared types ─────────────────────────────────────────────────────────────

public sealed record DeviceThresholds
{
    public int? Low { get; set; }
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
    Dictionary<string, DeviceThresholds> DeviceOverrides);
