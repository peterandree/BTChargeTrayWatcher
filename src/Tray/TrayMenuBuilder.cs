using System.Diagnostics;

namespace BTChargeTrayWatcher;

/// <summary>
/// Pre-constructed <see cref="ToolStripMenuItem"/> instances required by
/// <see cref="TrayMenuBuilder.Build"/>. Extracted as a parameter object to
/// eliminate the 4 same-typed positional parameters (closes #121).
/// </summary>
internal sealed record TrayMenuItems(
    ToolStripMenuItem LaptopMenuItem,
    ToolStripMenuItem ScanMenuItem,
    ToolStripMenuItem LowMenu,
    ToolStripMenuItem HighMenu);

/// <summary>
/// Builds the tray context menu, including the interactive submenus that used to be
/// stubbed out during the device-id settings refactor (#156):
/// global low/high threshold pickers, the laptop submenu
/// (thresholds + overlay/monitoring exclusion), and per-device submenus
/// (per-device thresholds, tray-icon-overlay exclusion, ignore).
///
/// All device-scoped reads/writes go through the device-id-aware
/// <see cref="ThresholdSettings"/> APIs (ADR-009) — never the removed name-keyed ones.
/// </summary>
internal sealed class TrayMenuBuilder
{
    private readonly ThresholdSettings _settings;
    private static readonly int[] LowThresholdCandidates  = { 10, 15, 20, 25, 30 };
    private static readonly int[] HighThresholdCandidates = { 70, 75, 80, 85, 90 };

    public TrayMenuBuilder(ThresholdSettings settings)
    {
        _settings = settings;
    }

    public static ContextMenuStrip Build(
        ThresholdSettings settings,
        TrayMenuItems items,
        Func<IReadOnlyList<DeviceBatteryInfo>> getDevices,
        Action onExit,
        Action? onOptions = null,
        Action? onStartupDiagnostics = null)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(items.LaptopMenuItem);
        var startDevicesSeparator = new ToolStripSeparator();
        menu.Items.Add(startDevicesSeparator);

        var endDevicesSeparator = new ToolStripSeparator();
        menu.Items.Add(endDevicesSeparator);

        menu.Opening += (_, _) =>
            PopulateDevicesSection(menu, settings, getDevices, startDevicesSeparator, endDevicesSeparator);

        menu.Items.Add(items.LowMenu);
        menu.Items.Add(items.HighMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(items.ScanMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        var optionsMenuItem = new ToolStripMenuItem("Options…");
        optionsMenuItem.Click += (_, _) =>
        {
            if (onOptions != null)
                onOptions();
            else
                BTChargeTrayWatcher.Tray.OptionsFormManager.ShowOptionsForm();
        };
        menu.Items.Add(optionsMenuItem);
        if (onStartupDiagnostics is not null)
        {
            var startupDiagnosticsItem = new ToolStripMenuItem("Startup diagnostics…");
            startupDiagnosticsItem.Click += (_, _) => onStartupDiagnostics();
            menu.Items.Add(startupDiagnosticsItem);
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => onExit());
        return menu;
    }

    // ── Device section ───────────────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds the device entries between the two separators on every menu open so the
    /// list and the battery levels are always current.
    /// </summary>
    private static void PopulateDevicesSection(
        ContextMenuStrip menu,
        ThresholdSettings settings,
        Func<IReadOnlyList<DeviceBatteryInfo>> getDevices,
        ToolStripSeparator startDevicesSeparator,
        ToolStripSeparator endDevicesSeparator)
    {
        try
        {
            int startIndex = menu.Items.IndexOf(startDevicesSeparator);
            int endIndex   = menu.Items.IndexOf(endDevicesSeparator);
            while (startIndex + 1 < endIndex)
            {
                menu.Items.RemoveAt(startIndex + 1);
                endIndex--;
            }

            var devices = getDevices();
            int insertIndex = menu.Items.IndexOf(endDevicesSeparator);
            if (devices.Count == 0)
            {
                menu.Items.Insert(insertIndex,
                    new ToolStripMenuItem("(no devices detected)") { Enabled = false });
                return;
            }

            foreach (var dev in devices)
                menu.Items.Insert(insertIndex++, BuildDeviceMenuItem(settings, dev));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TrayMenuBuilder] Device menu build fault: {ex}");
            int insertIndex = menu.Items.IndexOf(endDevicesSeparator);
            if (insertIndex >= 0)
                menu.Items.Insert(insertIndex, new ToolStripMenuItem("(device error)") { Enabled = false });
        }
    }

    /// <summary>
    /// One device entry: "<c>Name  55% ⚡</c>" with a dropdown holding the per-device
    /// controls that the Options dialog also exposes (Devices tab).
    /// </summary>
    internal static ToolStripMenuItem BuildDeviceMenuItem(ThresholdSettings settings, DeviceBatteryInfo dev)
    {
        string name    = settings.GetDisplayName(dev.DeviceId, dev.Name);
        string battery = dev.Battery.HasValue
            ? BatteryDisplay.FormatBattery(dev.Battery.Value, dev.IsCharging)
            : "N/A";
        string trend = dev.Battery.HasValue
            ? BatteryTrendHelper.GetArrow(null, dev.Battery.Value)
            : string.Empty;

        var item = new ToolStripMenuItem(
            trend.Length > 0 ? $"{name}  {battery} {trend}" : $"{name}  {battery}");

        string deviceId = dev.DeviceId;
        string displayName = dev.Name;

        item.DropDownItems.Add(BuildThresholdSubmenu(
            "Low threshold",
            LowThresholdCandidates,
            () => settings.GetLowForDevice(deviceId, displayName),
            value => settings.SetLowForDevice(deviceId, value)));

        item.DropDownItems.Add(BuildThresholdSubmenu(
            "High threshold",
            HighThresholdCandidates,
            () => settings.GetHighForDevice(deviceId, displayName),
            value => settings.SetHighForDevice(deviceId, value)));

        item.DropDownItems.Add(new ToolStripSeparator());

        var overlayToggle = new ToolStripMenuItem("Exclude from tray icon alert")
        {
            CheckOnClick = true,
            Checked = settings.IsTrayIconOverlayExcluded(deviceId, displayName)
        };
        overlayToggle.CheckedChanged += (_, _) =>
            settings.SetTrayIconOverlayExcluded(deviceId, displayName, overlayToggle.Checked);

        var ignoreToggle = new ToolStripMenuItem("Ignore device (no notifications)")
        {
            CheckOnClick = true,
            Checked = settings.IsIgnored(deviceId, displayName)
        };
        ignoreToggle.CheckedChanged += (_, _) =>
            settings.SetIgnored(deviceId, displayName, ignoreToggle.Checked);

        item.DropDownItems.Add(overlayToggle);
        item.DropDownItems.Add(ignoreToggle);

        // Re-sync on open so changes made in the Options dialog are reflected here.
        item.DropDownOpening += (_, _) =>
        {
            overlayToggle.Checked = settings.IsTrayIconOverlayExcluded(deviceId, displayName);
            ignoreToggle.Checked  = settings.IsIgnored(deviceId, displayName);
        };

        return item;
    }

    // ── Global threshold menus ───────────────────────────────────────────────────

    /// <summary>Global low threshold picker (applies to all Bluetooth devices without an override).</summary>
    public ToolStripMenuItem BuildLowMenu() => BuildThresholdSubmenu(
        "Low threshold",
        LowThresholdCandidates,
        () => _settings.Low,
        value => _settings.Low = value,
        radioCheck: true);

    /// <summary>Global high threshold picker (applies to all Bluetooth devices without an override).</summary>
    public ToolStripMenuItem BuildHighMenu() => BuildThresholdSubmenu(
        "High threshold",
        HighThresholdCandidates,
        () => _settings.High,
        value => _settings.High = value,
        radioCheck: true);

    // ── Laptop submenu ───────────────────────────────────────────────────────────

    /// <summary>
    /// Laptop battery entry. Its text is kept up to date with the current reading by
    /// <c>TrayApp</c>; the dropdown holds the laptop threshold pickers and the two
    /// exclusion toggles (#156).
    /// </summary>
    public ToolStripMenuItem BuildLaptopMenuItem()
    {
        var laptop = new ToolStripMenuItem("Laptop battery");

        laptop.DropDownItems.Add(BuildThresholdSubmenu(
            "Low threshold",
            LowThresholdCandidates,
            () => _settings.LaptopLow,
            value => _settings.LaptopLow = value));

        laptop.DropDownItems.Add(BuildThresholdSubmenu(
            "High threshold",
            HighThresholdCandidates,
            () => _settings.LaptopHigh,
            value => _settings.LaptopHigh = value));

        laptop.DropDownItems.Add(new ToolStripSeparator());

        var overlayToggle = new ToolStripMenuItem("Exclude from tray icon alert")
        {
            CheckOnClick = true,
            Checked = _settings.ExcludeLaptopFromTrayIconOverlay
        };
        overlayToggle.CheckedChanged += (_, _) =>
            _settings.ExcludeLaptopFromTrayIconOverlay = overlayToggle.Checked;

        var monitoringToggle = new ToolStripMenuItem("Exclude from monitoring and alerts")
        {
            CheckOnClick = true,
            Checked = _settings.ExcludeLaptopFromMonitoring
        };
        monitoringToggle.CheckedChanged += (_, _) =>
            _settings.ExcludeLaptopFromMonitoring = monitoringToggle.Checked;

        laptop.DropDownItems.Add(overlayToggle);
        laptop.DropDownItems.Add(monitoringToggle);

        laptop.DropDownOpening += (_, _) =>
        {
            overlayToggle.Checked    = _settings.ExcludeLaptopFromTrayIconOverlay;
            monitoringToggle.Checked = _settings.ExcludeLaptopFromMonitoring;
        };

        return laptop;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a submenu of candidate percentage values. The current value is checked when the
    /// submenu opens (so it never goes stale), and selecting an entry writes it through
    /// <paramref name="set"/>. Invalid combinations (low ≥ high) are rejected by
    /// <see cref="ThresholdSettings"/> and logged instead of crashing the UI thread.
    /// </summary>
    private static ToolStripMenuItem BuildThresholdSubmenu(
        string text,
        int[] candidates,
        Func<int> get,
        Action<int> set,
        bool radioCheck = false)
    {
        var menu = new ToolStripMenuItem(text);
        int initial = get();

        foreach (int candidate in candidates)
        {
            var entry = new ToolStripMenuItem($"{candidate} %")
            {
                Tag = candidate,
                Checked = candidate == initial
            };
            entry.Click += (_, _) =>
            {
                try { set(candidate); }
                catch (ArgumentOutOfRangeException ex)
                {
                    Debug.WriteLine($"[TrayMenuBuilder] Rejected threshold {candidate}: {ex.Message}");
                }
            };
            menu.DropDownItems.Add(entry);
        }

        if (radioCheck)
        {
            menu.DropDownItems.Add(new ToolStripSeparator());
            var hint = new ToolStripMenuItem("Applies to devices without an override") { Enabled = false };
            menu.DropDownItems.Add(hint);
        }

        menu.DropDownOpening += (_, _) =>
        {
            int current = get();
            foreach (ToolStripItem dropDownItem in menu.DropDownItems)
            {
                if (dropDownItem is ToolStripMenuItem { Tag: int value } entry)
                    entry.Checked = value == current;
            }
        };

        return menu;
    }
}
