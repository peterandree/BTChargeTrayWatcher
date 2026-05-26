// src/Tray/ViewModels/TrayViewModel.cs
// Presentation logic for TrayApp: tooltip text assembly, combined alert state.
// No WinForms dependency — fully unit-testable.
using BTChargeTrayWatcher.Monitoring;
using BTChargeTrayWatcher.Settings;

namespace BTChargeTrayWatcher.Tray.ViewModels;

internal sealed class TrayViewModel
{
    private readonly ThresholdSettings       _settings;
    private readonly BluetoothBatteryMonitor _monitor;
    private readonly LaptopBatteryMonitor    _laptopMonitor;

    private bool _bluetoothAlert;
    private bool _laptopAlert;

    public bool HasAlert => _bluetoothAlert || _laptopAlert;

    // Raised whenever HasAlert changes value.
    public event Action<bool>? AlertChanged;

    // ── Bluetooth alert ───────────────────────────────────────────────────

    public void ApplyBluetoothAlert(bool hasAlert)
    {
        bool before = HasAlert;
        _bluetoothAlert = hasAlert;
        NotifyIfChanged(before);
    }

    // ── Laptop alert ──────────────────────────────────────────────────────

    public void ApplyLaptopBattery(LaptopBatteryInfo? info = null)
    {
        bool before = HasAlert;
        info ??= _laptopMonitor.LastKnownBattery;
        _laptopAlert = false;
        if (!_settings.ExcludeLaptopFromTrayIconOverlay
            && info is { HasBattery: true, BatteryPercent: >= 0 })
        {
            _laptopAlert = info.BatteryPercent <= _settings.LaptopLow
                        || info.BatteryPercent >= _settings.LaptopHigh;
        }
        NotifyIfChanged(before);
    }

    // ── Tooltip ───────────────────────────────────────────────────────────

    /// <summary>Assembles the NotifyIcon tooltip text (max 127 chars).</summary>
    public string BuildTooltip()
    {
        var sb = new System.Text.StringBuilder();

        foreach (var d in _monitor.LastKnownDevices)
        {
            if (d.Battery is null) continue;
            if (sb.Length > 0) sb.Append('\n');
            bool alert = d.Battery.Value <= _settings.GetLowForDevice(d.DeviceId, d.Name)
                      || d.Battery.Value >= _settings.GetHighForDevice(d.DeviceId, d.Name);
            if (alert) sb.Append("! ");
            sb.Append(d.Name).Append(' ')
              .Append(BatteryDisplay.FormatBattery(d.Battery.Value, d.IsCharging));
        }

        if (_laptopMonitor.LastKnownBattery is { HasBattery: true } laptop)
        {
            if (sb.Length > 0) sb.Append('\n');
            bool laptopAlert = laptop.BatteryPercent <= _settings.LaptopLow
                            || laptop.BatteryPercent >= _settings.LaptopHigh;
            if (laptopAlert) sb.Append("! ");
            sb.Append("Laptop ")
              .Append(BatteryDisplay.FormatBattery(laptop.BatteryPercent, laptop.IsCharging));
            if (laptop.IsCharging) sb.Append(" (charging)");
        }

        if (sb.Length == 0)
            sb.Append($"BT Battery Alert \u25bc{_settings.Low}% \u25b2{_settings.High}%");

        string text = sb.ToString();
        return text.Length > 127 ? text[..127] : text;
    }

    // ── Laptop menu item text ─────────────────────────────────────────────

    public string BuildLaptopMenuText(LaptopBatteryInfo info)
    {
        if (!info.HasBattery)
            return "\U0001f4bb Laptop: No battery";

        string chargeExtra = info.IsOnAcPower && !info.IsCharging ? " \U0001f50c Plugged in"
            : !info.IsOnAcPower ? " On battery"
            : string.Empty;

        return $"\U0001f4bb Laptop: {BatteryDisplay.FormatBattery(info.BatteryPercent, info.IsCharging)}{chargeExtra}";
    }

    private void NotifyIfChanged(bool before)
    {
        if (HasAlert != before)
            AlertChanged?.Invoke(HasAlert);
    }

    public TrayViewModel(
        ThresholdSettings       settings,
        BluetoothBatteryMonitor monitor,
        LaptopBatteryMonitor    laptopMonitor)
    {
        _settings      = settings;
        _monitor       = monitor;
        _laptopMonitor = laptopMonitor;
    }
}
