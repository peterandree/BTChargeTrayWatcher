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
                                            new ClassicBatteryReader(),
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
            var field = typeof(BluetoothBatteryMonitor).GetField("_lastKnown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(field);
            var dict = (System.Collections.Concurrent.ConcurrentDictionary<string, DeviceBatteryInfo>)field.GetValue(monitor)!;
            dict[deviceId] = device;
            Assert.Single(monitor.LastKnownDevices);
            Assert.Equal(deviceId, monitor.LastKnownDevices[0].DeviceId);
            var form = new OptionsForm((owner, text, caption, buttons, icon) => DialogResult.OK);
            form.Initialize(settings, monitor);

            // Debug: print actual column names
            var debugGrid = GetGrid(form, "devicesGrid");
            var colNames = string.Join(", ", debugGrid.Columns.Cast<DataGridViewColumn>().Select(c => $"{c.Name} ({c.HeaderText})"));
            System.Diagnostics.Debug.WriteLine($"Grid columns: {colNames}");

            // Get the DataGridView and edit values (find columns by DataPropertyName)
            var grid = debugGrid;
            var colLow = grid.Columns.Cast<DataGridViewColumn>().FirstOrDefault(c => c.DataPropertyName == "Low");
            var colHigh = grid.Columns.Cast<DataGridViewColumn>().FirstOrDefault(c => c.DataPropertyName == "High");
            var colPoll = grid.Columns.Cast<DataGridViewColumn>().FirstOrDefault(c => c.DataPropertyName == "PollInterval");
            var colDisplayName = grid.Columns.Cast<DataGridViewColumn>().FirstOrDefault(c => c.DataPropertyName == "DisplayName");
            var colExcluded = grid.Columns.Cast<DataGridViewColumn>().FirstOrDefault(c => c.DataPropertyName == "Excluded");

            Assert.NotNull(colLow);
            Assert.NotNull(colHigh);
            Assert.NotNull(colPoll);
            Assert.NotNull(colDisplayName);
            Assert.NotNull(colExcluded);

            int idxLow = colLow.Index;
            int idxHigh = colHigh.Index;
            int idxPoll = colPoll.Index;
            int idxDisplayName = colDisplayName.Index;
            int idxExcluded = colExcluded.Index;

            grid.Rows[0].Cells[idxLow].Value = 10;
            grid.Rows[0].Cells[idxHigh].Value = 90;
            grid.Rows[0].Cells[idxPoll].Value = 123;
            grid.Rows[0].Cells[idxDisplayName].Value = "Alias";
            grid.Rows[0].Cells[idxExcluded].Value = true;

            // Commit edits so the form's CellValueChanged handler runs
            grid.CurrentCell = grid.Rows[0].Cells[idxDisplayName];
            grid.CommitEdit(DataGridViewDataErrorContexts.Commit);

            // Ensure the monitor reports the tracked device name so the form's logic can
            // compare the typed alias against the device's default tracked name.
            var deviceWatcher = new DeviceWatcherService();
            var devicesField = deviceWatcher.GetType().GetField("_devices", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(devicesField);
            var devicesDict = (System.Collections.IDictionary)devicesField!.GetValue(deviceWatcher)!;
            var watchedType = typeof(BluetoothBatteryMonitor).Assembly.GetType("BTChargeTrayWatcher.WatchedDevice");
            Assert.NotNull(watchedType);
            var watchedInstance = Activator.CreateInstance(watchedType!, new object[] { deviceId, "Test Device", true, true });
            devicesDict.GetType().GetMethod("Add")!.Invoke(devicesDict, new object[] { deviceId, watchedInstance! });

            var dwField = typeof(BluetoothBatteryMonitor).GetField("_deviceWatcher", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(dwField);
            dwField!.SetValue(monitor, deviceWatcher);

            // The form stores a private List<DeviceRow> called 'deviceRows'. Update it via reflection
            var drField = form.GetType().GetField("deviceRows", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(drField);
            var drList = (System.Collections.IList)drField!.GetValue(form)!;
            var firstRow = drList[0];
            Assert.NotNull(firstRow);
            var displayNameProp = firstRow.GetType().GetProperty("DisplayName");
            Assert.NotNull(displayNameProp);
            displayNameProp!.SetValue(firstRow, "Alias");

            // Invoke the form's DevicesGrid_CellValueChanged handler to apply the setting change
            var handler = form.GetType().GetMethod("DevicesGrid_CellValueChanged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(handler);
            handler!.Invoke(form, new object[] { grid, new DataGridViewCellEventArgs(idxDisplayName, 0) });

            Assert.Equal(10, settings.GetLowForDevice(deviceId, "Test Device"));
            Assert.Equal(90, settings.GetHighForDevice(deviceId, "Test Device"));
            Assert.Equal(123, settings.GetPollIntervalForDevice(deviceId, "Test Device"));
            Assert.Equal("Alias", settings.GetDisplayName(deviceId, "Test Device"));
            Assert.True(settings.IsIgnored(deviceId, "Test Device"));
        }
    }
}
