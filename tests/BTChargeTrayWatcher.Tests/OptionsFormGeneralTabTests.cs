using System.Windows.Forms;
using BTChargeTrayWatcher.Tray;
using Xunit;

namespace BTChargeTrayWatcher.Tests
{
    public sealed class OptionsFormGeneralTabTests
    {
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
        public void GeneralTab_controls_reflect_and_update_settings()
        {
            var settings = new ThresholdSettings();
            var monitor = CreateMonitor(settings);
            var form = new OptionsForm((owner, text, caption, buttons, icon) => DialogResult.OK);
            form.Initialize(settings, monitor);

            // Simulate user changing global thresholds
            var lowField = GetNumeric(form, "lowNumeric");
            var highField = GetNumeric(form, "highNumeric");
            var laptopLowField = GetNumeric(form, "laptopLowNumeric");
            var laptopHighField = GetNumeric(form, "laptopHighNumeric");
            var excludeCheck = GetCheckBox(form, "excludeLaptopOverlayCheck");

            lowField.Value = 15;
            highField.Value = 85;
            laptopLowField.Value = 10;
            laptopHighField.Value = 90;
            excludeCheck.Checked = true;

            Assert.Equal(15, settings.Low);
            Assert.Equal(85, settings.High);
            Assert.Equal(10, settings.LaptopLow);
            Assert.Equal(90, settings.LaptopHigh);
            Assert.True(settings.ExcludeLaptopFromTrayIconOverlay);
        }

        private static NumericUpDown GetNumeric(Form form, string field)
        {
            var f = form.GetType().GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(f);
            return (NumericUpDown)f!.GetValue(form)!;
        }
        private static CheckBox GetCheckBox(Form form, string field)
        {
            var f = form.GetType().GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(f);
            return (CheckBox)f!.GetValue(form)!;
        }
    }
}
