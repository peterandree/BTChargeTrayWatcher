using System.Text.Json;
using Microsoft.Win32;

namespace BTChargeTrayWatcher;

public sealed class ThresholdSettings
{
    private const string AppName = "BTChargeTrayWatcher";
    private readonly string _settingsFilePath;

    private readonly object _thresholdLock = new();

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
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string appDir = Path.Combine(localAppData, AppName);
        Directory.CreateDirectory(appDir);
        _settingsFilePath = Path.Combine(appDir, "settings.json");

        Load();
    }

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
            Save();
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
            Save();
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
            Save();
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
            Save();
            Changed?.Invoke();
            LaptopSettingsChanged?.Invoke();
        }
    }

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
        Save();
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
        Save();
        Changed?.Invoke();
    }

    private void CleanupEmptyOverrides(string deviceName)
    {
        if (_deviceOverrides.TryGetValue(deviceName, out var t))
        {
            if (!t.Low.HasValue && !t.High.HasValue)
                _deviceOverrides.Remove(deviceName);
        }
    }

    public IReadOnlyCollection<string> IgnoredDevices => _ignoredDevices;

    public IReadOnlyCollection<string> TrayIconOverlayExcludedDevices => _trayIconOverlayExcludedDevices;

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
            Save();
            Changed?.Invoke();
        }
    }

    public void SetIgnoredDevices(IEnumerable<string> devices)
    {
        lock (_thresholdLock)
            _ignoredDevices = new HashSet<string>(devices, StringComparer.OrdinalIgnoreCase);
        Save();
        Changed?.Invoke();
    }

    public void ToggleIgnoreDevice(string deviceName)
    {
        lock (_thresholdLock)
        {
            if (_ignoredDevices.Contains(deviceName))
                _ignoredDevices.Remove(deviceName);
            else
                _ignoredDevices.Add(deviceName);
        }
        Save();
        Changed?.Invoke();
    }

    public void ToggleExcludeFromTrayIconOverlay(string deviceName)
    {
        lock (_thresholdLock)
        {
            if (_trayIconOverlayExcludedDevices.Contains(deviceName))
                _trayIconOverlayExcludedDevices.Remove(deviceName);
            else
                _trayIconOverlayExcludedDevices.Add(deviceName);
        }
        Save();
        Changed?.Invoke();
    }

    public bool RunOnStartup
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false);
                var stored = key?.GetValue(AppName) as string;
                if (string.IsNullOrWhiteSpace(stored)) return false;
                string expected = $"\"{Application.ExecutablePath}\"";
                return string.Equals(stored, expected, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                if (key == null) return;

                if (value)
                    key.SetValue(AppName, $"\"{Application.ExecutablePath}\"");
                else
                    key.DeleteValue(AppName, false);

                Changed?.Invoke();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ThresholdSettings] RunOnStartup fault: {ex}");
            }
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                string json = File.ReadAllText(_settingsFilePath);
                var dto = JsonSerializer.Deserialize<SettingsDto>(json);
                if (dto != null)
                {
                    _low = dto.Low;
                    _high = dto.High;

                    if (_low >= _high) { _low = 20; _high = 80; }

                    _laptopLow = dto.LaptopLow;
                    _laptopHigh = dto.LaptopHigh;

                    if (_laptopLow >= _laptopHigh) { _laptopLow = 20; _laptopHigh = 80; }

                    if (dto.IgnoredDevices != null)
                        _ignoredDevices = new HashSet<string>(dto.IgnoredDevices, StringComparer.OrdinalIgnoreCase);

                    if (dto.TrayIconOverlayExcludedDevices != null)
                        _trayIconOverlayExcludedDevices = new HashSet<string>(dto.TrayIconOverlayExcludedDevices, StringComparer.OrdinalIgnoreCase);

                    _excludeLaptopFromTrayIconOverlay = dto.ExcludeLaptopFromTrayIconOverlay;

                    if (dto.DeviceOverrides != null)
                        _deviceOverrides = new Dictionary<string, DeviceThresholds>(dto.DeviceOverrides, StringComparer.OrdinalIgnoreCase);

                    return;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ThresholdSettings] Load fault: {ex}");
        }

        _low = 20;
        _high = 80;
        _laptopLow = 20;
        _laptopHigh = 80;
    }

    private void Save()
    {
        SettingsDto dto;
        lock (_thresholdLock)
        {
            dto = new SettingsDto
            {
                Low = _low,
                High = _high,
                LaptopLow = _laptopLow,
                LaptopHigh = _laptopHigh,
                IgnoredDevices = new List<string>(_ignoredDevices),
                TrayIconOverlayExcludedDevices = new List<string>(_trayIconOverlayExcludedDevices),
                ExcludeLaptopFromTrayIconOverlay = _excludeLaptopFromTrayIconOverlay,
                DeviceOverrides = new Dictionary<string, DeviceThresholds>(_deviceOverrides, StringComparer.OrdinalIgnoreCase)
            };
        }
        try
        {
            string json = JsonSerializer.Serialize(dto);
            string tmp = _settingsFilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _settingsFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ThresholdSettings] Save fault: {ex}");
        }
    }

    public class DeviceThresholds
    {
        public int? Low { get; set; }
        public int? High { get; set; }
    }

    private class SettingsDto
    {
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
