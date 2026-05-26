using System.Windows.Forms;
using BTChargeTrayWatcher.Tray;
using Xunit;

namespace BTChargeTrayWatcher.Tests
{
    public sealed class OptionsFormDeviceTabTests
    {
        private static DataGridView GetGrid(Form form, string field)
        {
            var f = form.GetType().GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(f);
            return (DataGridView)f!.GetValue(form)!;
        }

        private static BluetoothBatteryMonitor CreateMonitor(ThresholdSettings settings)
        {
            var infrastructure = new BluetoothMonitoringInfrastructure(
                DeviceWatcher:          new DeviceWatcherService(),
                Orchestrator:           new BatteryReaderOrchestrator(
                                            new GattConnectionManager(),
                                            _ => Task.FromResult(new List<DeviceBatteryInfo>()),
                                            new DeviceCapabilityCache()),
                GattConnectionManager:  new GattConnectionManager(),
                CapabilityCache:        new DeviceCapabilityCache(),
                AliasSuggestionService: new AliasSuggestionService());
            return new BluetoothBatteryMonitor(settings, NullNotificationService.Instance, infrastructure);
        }

        [StaFact]
        public void DeviceTab_editing_updates_settings()
        {
            var settings = new ThresholdSettings();
            var monitor = CreateMonitor(settings);
            var deviceId = "dev-xyz";
            var device = new DeviceBatteryInfo(deviceId, "Test Device", 50, null, BatterySource.Gatt);

            // Inject device into monitor._lastKnown via reflection
            var lastKnownField = typeof(BluetoothBatteryMonitor).GetField(
                "_lastKnown",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(lastKnownField);
            var dict = (System.Collections.Concurrent.ConcurrentDictionary<string, DeviceBatteryInfo>)lastKnownField!.GetValue(monitor)!;
            dict[deviceId] = device;
            Assert.Single(monitor.LastKnownDevices);
            Assert.Equal(deviceId, monitor.LastKnownDevices[0].DeviceId);

            var form = new OptionsForm((owner, text, caption, buttons, icon) => DialogResult.OK);
            form.Initialize(settings, monitor);

            var debugGrid = GetGrid(form, "devicesGrid");
            var colNames = string.Join(", ", debugGrid.Columns.Cast<DataGridViewColumn>().Select(c => $"{c.Name} ({c.HeaderText})"));
            System.Diagnostics.Debug.WriteLine($"Grid columns: {colNames}");

            var grid = debugGrid;
            var colLow         = grid.Columns.Cast<DataGridViewColumn>().FirstOrDefault(c => c.DataPropertyName == "Low");
            var colHigh        = grid.Columns.Cast<DataGridViewColumn>().FirstOrDefault(c => c.DataPropertyName == "High");
            var colPoll        = grid.Columns.Cast<DataGridViewColumn>().FirstOrDefault(c => c.DataPropertyName == "PollInterval");
            var colDisplayName = grid.Columns.Cast<DataGridViewColumn>().FirstOrDefault(c => c.DataPropertyName == "DisplayName");
            var colExcluded    = grid.Columns.Cast<DataGridViewColumn>().FirstOrDefault(c => c.DataPropertyName == "Excluded");

            Assert.NotNull(colLow);
            Assert.NotNull(colHigh);
            Assert.NotNull(colPoll);
            Assert.NotNull(colDisplayName);
            Assert.NotNull(colExcluded);

            int idxLow         = colLow!.Index;
            int idxHigh        = colHigh!.Index;
            int idxPoll        = colPoll!.Index;
            int idxDisplayName = colDisplayName!.Index;
            int idxExcluded    = colExcluded!.Index;

            grid.Rows[0].Cells[idxLow].Value         = 10;
            grid.Rows[0].Cells[idxHigh].Value        = 90;
            grid.Rows[0].Cells[idxPoll].Value        = 123;
            grid.Rows[0].Cells[idxDisplayName].Value = "Alias";
            grid.Rows[0].Cells[idxExcluded].Value    = true;

            grid.CurrentCell = grid.Rows[0].Cells[idxDisplayName];
            grid.CommitEdit(DataGridViewDataErrorContexts.Commit);

            // Inject a WatchedDevice into DeviceWatcherService._devices using typed access
            var deviceWatcher  = new DeviceWatcherService();
            var devicesField   = typeof(DeviceWatcherService).GetField(
                "_devices",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(devicesField);
            var devicesDict = (Dictionary<string, WatchedDevice>)devicesField!.GetValue(deviceWatcher)!;
            devicesDict[deviceId] = new WatchedDevice(deviceId, "Test Device", IsBle: true, IsConnected: true);

            var dwField = typeof(BluetoothBatteryMonitor).GetField(
                "_deviceWatcher",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(dwField);
            dwField!.SetValue(monitor, deviceWatcher);

            // Update the form's private DeviceRow list so its CellValueChanged handler sees "Alias"
            var drField = form.GetType().GetField(
                "deviceRows",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(drField);
            var drList    = (System.Collections.IList)drField!.GetValue(form)!;
            var firstRow  = drList[0];
            Assert.NotNull(firstRow);
            var displayNameProp = firstRow!.GetType().GetProperty("DisplayName");
            Assert.NotNull(displayNameProp);
            displayNameProp!.SetValue(firstRow, "Alias");

            var handler = form.GetType().GetMethod(
                "DevicesGrid_CellValueChanged",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(handler);
            handler!.Invoke(form, new object[] { grid, new DataGridViewCellEventArgs(idxDisplayName, 0) });

            Assert.Equal(10,      settings.GetLowForDevice(deviceId, "Test Device"));
            Assert.Equal(90,      settings.GetHighForDevice(deviceId, "Test Device"));
            Assert.Equal(123,     settings.GetPollIntervalForDevice(deviceId, "Test Device"));
            Assert.Equal("Alias", settings.GetDisplayName(deviceId, "Test Device"));
            Assert.True(settings.IsIgnored(deviceId, "Test Device"));
        }
    }
}
