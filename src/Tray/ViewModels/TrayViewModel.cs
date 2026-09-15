// src/Tray/ViewModels/TrayViewModel.cs
// Presentation logic for TrayApp: tooltip text assembly, combined alert state.
// No WinForms dependency — fully unit-testable.
namespace BTChargeTrayWatcher;

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
        _laptopAlert = IsLaptopAlert(
            info,
            _settings.LaptopLow,
            _settings.LaptopHigh,
            _settings.ExcludeLaptopFromMonitoring,
            _settings.ExcludeLaptopFromTrayIconOverlay);
        NotifyIfChanged(before);
    }

    /// <summary>
    /// Pure laptop alert decision used for both the tray-icon overlay and the tooltip "!"
    /// marker. Excluding the laptop from monitoring (#156) suppresses alerts everywhere,
    /// not just for the overlay. Advisory levels (no battery / unknown sentinel) never alert.
    /// </summary>
    internal static bool IsLaptopAlert(
        LaptopBatteryInfo? info,
        int low,
        int high,
        bool excludeFromMonitoring,
        bool excludeFromOverlay)
    {
        if (excludeFromMonitoring || excludeFromOverlay) return false;
        if (info is not { HasBattery: true, BatteryPercent: >= 0 }) return false;
        return info.BatteryPercent <= low || info.BatteryPercent >= high;
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
            // #144: an unknown level is the -1 sentinel — never alert on it.
            // #156: an unmonitored laptop is informational only — no "!" marker.
            if (IsLaptopAlert(laptop, _settings.LaptopLow, _settings.LaptopHigh,
                    _settings.ExcludeLaptopFromMonitoring, _settings.ExcludeLaptopFromTrayIconOverlay))
                sb.Append("! ");
            sb.Append("Laptop ")
              .Append(BatteryDisplay.FormatBattery(laptop.BatteryPercent, laptop.IsCharging));
            if (laptop.IsCharging) sb.Append(" (charging)");
            if (_settings.ExcludeLaptopFromMonitoring) sb.Append(" (not monitored)");
            // #154: show discharge rate and estimated time when available.
            if (laptop.DischargeRateWatts is not null)
                sb.Append(" \u00b7 ").Append(BatteryDisplay.FormatPowerRate(laptop.DischargeRateWatts));
            if (laptop.EstimatedRunTimeMinutes is not null)
                sb.Append(" \u00b7 ~").Append(BatteryDisplay.FormatDuration(laptop.EstimatedRunTimeMinutes));
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

        string healthExtra = info.HealthPercent is not null
            ? $" (health {BatteryDisplay.FormatHealth(info.HealthPercent)})"
            : string.Empty;

        return $"\U0001f4bb Laptop: {BatteryDisplay.FormatBattery(info.BatteryPercent, info.IsCharging)}{chargeExtra}{healthExtra}";
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
