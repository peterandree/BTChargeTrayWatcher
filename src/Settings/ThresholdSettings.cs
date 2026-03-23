using System.Text.Json;
using Microsoft.Win32;

namespace BTChargeTrayWatcher;

public class ThresholdSettings
{
    private const string AppName = "BTChargeTrayWatcher";
    private readonly string _settingsFilePath;

    private int _low;
    private int _high;
    private HashSet<string> _ignoredDevices = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, DeviceThresholds> _deviceOverrides = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

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
        get => _low;
        set
        {
            if (_low == value) return;
            if (value >= _high) throw new ArgumentOutOfRangeException(nameof(value), "Low threshold must be below High threshold.");
            _low = value;
            Save();
            Changed?.Invoke();
        }
    }

    public int High
    {
        get => _high;
        set
        {
            if (_high == value) return;
            if (value <= _low) throw new ArgumentOutOfRangeException(nameof(value), "High threshold must be above Low threshold.");
            _high = value;
            Save();
            Changed?.Invoke();
        }
    }

    public int GetLow(string deviceName) =>
        _deviceOverrides.TryGetValue(deviceName, out var t) && t.Low.HasValue ? t.Low.Value : _low;

    public int GetHigh(string deviceName) =>
        _deviceOverrides.TryGetValue(deviceName, out var t) && t.High.HasValue ? t.High.Value : _high;

    public bool HasCustomLow(string deviceName) =>
        _deviceOverrides.TryGetValue(deviceName, out var t) && t.Low.HasValue;

    public bool HasCustomHigh(string deviceName) =>
        _deviceOverrides.TryGetValue(deviceName, out var t) && t.High.HasValue;

    public void SetLow(string deviceName, int? value)
    {
        if (!_deviceOverrides.TryGetValue(deviceName, out var t))
        {
            if (value == null) return;
            t = new DeviceThresholds();
            _deviceOverrides[deviceName] = t;
        }

        t.Low = value;
        CleanupEmptyOverrides(deviceName);
        Save();
        Changed?.Invoke();
    }

    public void SetHigh(string deviceName, int? value)
    {
        if (!_deviceOverrides.TryGetValue(deviceName, out var t))
        {
            if (value == null) return;
            t = new DeviceThresholds();
            _deviceOverrides[deviceName] = t;
        }

        t.High = value;
        CleanupEmptyOverrides(deviceName);
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

    public void SetIgnoredDevices(IEnumerable<string> devices)
    {
        _ignoredDevices = new HashSet<string>(devices, StringComparer.OrdinalIgnoreCase);
        Save();
        Changed?.Invoke();
    }

    public void ToggleIgnoreDevice(string deviceName)
    {
        if (_ignoredDevices.Contains(deviceName))
            _ignoredDevices.Remove(deviceName);
        else
            _ignoredDevices.Add(deviceName);

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
                return key?.GetValue(AppName) != null;
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
                    key.SetValue(AppName, Application.ExecutablePath);
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
                    if (dto.IgnoredDevices != null)
                        _ignoredDevices = new HashSet<string>(dto.IgnoredDevices, StringComparer.OrdinalIgnoreCase);

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
    }

    private void Save()
    {
        try
        {
            var dto = new SettingsDto
            {
                Low = _low,
                High = _high,
                IgnoredDevices = new List<string>(_ignoredDevices),
                DeviceOverrides = _deviceOverrides
            };
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
        public List<string>? IgnoredDevices { get; set; }
        public Dictionary<string, DeviceThresholds>? DeviceOverrides { get; set; }
    }
}
