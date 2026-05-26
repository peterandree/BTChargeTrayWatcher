using System.Windows.Forms;
using BTChargeTrayWatcher.Tray;
using Xunit;

namespace BTChargeTrayWatcher.Tests
{
    public sealed class OptionsFormNotificationsTabEdgeTests
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
        public void NotificationsTab_enable_toggle_updates_settings_without_notifier()
        {
            var settings = new ThresholdSettings();
            settings.UpdateNtfySettings(s =>
            {
                s.IsEnabled = false;
                s.Topic = "topic-123";
            });
            var monitor = CreateMonitor(settings);
            var form = new OptionsForm((owner, text, caption, buttons, icon) => DialogResult.OK);
            form.Initialize(settings, monitor, null);

            var enabledCheck = GetCheckBox(form, "ntfyEnabledCheck");
            var ex = Record.Exception(() => enabledCheck.Checked = true);

            Assert.Null(ex);
            Assert.True(settings.GetNtfySettings().IsEnabled);
        }

        private static CheckBox GetCheckBox(Form form, string field)
        {
            var f = form.GetType().GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(f);
            return (CheckBox)f!.GetValue(form)!;
        }
    }
}
