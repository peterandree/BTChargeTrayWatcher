// src/Tray/ViewModels/DevicesViewModel.cs
// Presentation logic for the Devices tab of OptionsForm.
// No WinForms dependency — fully unit-testable.
using BTChargeTrayWatcher.Monitoring;
using BTChargeTrayWatcher.Settings;

namespace BTChargeTrayWatcher.Tray.ViewModels;

internal sealed class DevicesViewModel
{
    private readonly ThresholdSettings _settings;
    private readonly BluetoothBatteryMonitor _monitor;

    public sealed class DeviceRow
    {
        public string DeviceId   { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public int?   Low         { get; set; }
        public int?   High        { get; set; }
        public int?   PollInterval { get; set; }
        public bool   Excluded    { get; set; }
    }

    // ── Query ─────────────────────────────────────────────────────────────

    public IReadOnlyList<DeviceRow> BuildRows()
    {
        var rows = new List<DeviceRow>();
        foreach (var d in _monitor.LastKnownDevices)
        {
            rows.Add(new DeviceRow
            {
                DeviceId     = d.DeviceId,
                DisplayName  = d.Name,
                Low          = _settings.GetLowForDevice(d.DeviceId, d.Name),
                High         = _settings.GetHighForDevice(d.DeviceId, d.Name),
                PollInterval = _settings.GetPollIntervalForDevice(d.DeviceId, d.Name)
                               ?? (int)PollingDefaults.PollingInterval.TotalSeconds,
                Excluded     = _settings.IsIgnored(d.DeviceId, d.Name)
            });
        }
        return rows;
    }

    // ── Mutations ─────────────────────────────────────────────────────────

    /// <summary>Applies a display-name change for one row. Returns null on success, error message on failure.</summary>
    public string? SetDisplayName(DeviceRow row, string newName)
    {
        try
        {
            string defaultName = row.DisplayName;
            var tracked = _monitor.TrackedDevices.FirstOrDefault(t => t.DeviceId == row.DeviceId);
            if (tracked != null) defaultName = tracked.Name;

            if (string.Equals(newName, defaultName, StringComparison.OrdinalIgnoreCase))
                _settings.SetDisplayNameAlias(row.DeviceId, null);
            else
                _settings.SetDisplayNameAlias(row.DeviceId, newName);
            return null;
        }
        catch (Exception ex) { return $"Failed to set alias: {ex.Message}"; }
    }

    /// <summary>Returns null on success, error message on failure.</summary>
    public string? SetLow(DeviceRow row, int? value)
    {
        try   { _settings.SetLowForDevice(row.DeviceId, value); return null; }
        catch (ArgumentOutOfRangeException ex) { return ex.Message; }
    }

    /// <summary>Returns null on success, error message on failure.</summary>
    public string? SetHigh(DeviceRow row, int? value)
    {
        try   { _settings.SetHighForDevice(row.DeviceId, value); return null; }
        catch (ArgumentOutOfRangeException ex) { return ex.Message; }
    }

    /// <summary>Returns null on success, error message on failure.</summary>
    public string? SetPollInterval(DeviceRow row, int? value)
    {
        if (value.HasValue && value.Value <= 0)
            return "Poll interval must be a positive integer.";
        _settings.SetPollIntervalForDevice(row.DeviceId, value);
        return null;
    }

    public void SetExcluded(DeviceRow row, bool excluded)
    {
        if (excluded)
            _settings.SetIgnoredDevicesByIds(_settings.IgnoredDevices.Union(new[] { row.DeviceId }));
        else
            _settings.SetIgnoredDevicesByIds(_settings.IgnoredDevices.Except(new[] { row.DeviceId }));
    }

    public void ResetDevice(DeviceRow row)
    {
        _settings.SetLowForDevice(row.DeviceId, null);
        _settings.SetHighForDevice(row.DeviceId, null);
        _settings.SetPollIntervalForDevice(row.DeviceId, null);
    }

    public void ResetAll(IReadOnlyList<DeviceRow> rows)
    {
        foreach (var row in rows)
            ResetDevice(row);
    }

    public DevicesViewModel(ThresholdSettings settings, BluetoothBatteryMonitor monitor)
    {
        _settings = settings;
        _monitor  = monitor;
    }
}
