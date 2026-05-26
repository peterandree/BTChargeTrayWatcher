using System.Windows.Forms;
using BTChargeTrayWatcher.Tray;
using Xunit;

namespace BTChargeTrayWatcher.Tests
{
    public sealed class OptionsFormGeneralTabErrorTests
    {
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
        public void GeneralTab_invalid_thresholds_show_error_and_revert()
        {
            var settings = new ThresholdSettings();
            var monitor = CreateMonitor(settings);
            var form = new OptionsForm((owner, text, caption, buttons, icon) => DialogResult.OK); // Suppress MessageBox in test
            form.Initialize(settings, monitor);

            var lowField = GetNumeric(form, "lowNumeric");
            var highField = GetNumeric(form, "highNumeric");
            var laptopLowField = GetNumeric(form, "laptopLowNumeric");
            var laptopHighField = GetNumeric(form, "laptopHighNumeric");

            // Set invalid: Low >= High
            highField.Value = 30;
            lowField.Value = 30;
            Assert.NotEqual(30, settings.Low); // Should not update

            // Set invalid: High <= Low
            lowField.Value = 10;
            highField.Value = 10;
            Assert.NotEqual(10, settings.High); // Should not update

            // Laptop: Low >= High
            laptopHighField.Value = 40;
            laptopLowField.Value = 40;
            Assert.NotEqual(40, settings.LaptopLow);

            // Laptop: High <= Low
            laptopLowField.Value = 10;
            laptopHighField.Value = 10;
            Assert.NotEqual(10, settings.LaptopHigh);
        }

        private static NumericUpDown GetNumeric(Form form, string field)
        {
            var f = form.GetType().GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(f);
            return (NumericUpDown)f!.GetValue(form)!;
        }
    }
}
