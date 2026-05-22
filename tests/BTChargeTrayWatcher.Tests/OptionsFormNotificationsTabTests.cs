using System.Windows.Forms;
using BTChargeTrayWatcher;
using BTChargeTrayWatcher.Tray;
using Xunit;

namespace BTChargeTrayWatcher.Tests
{
    public sealed class OptionsFormNotificationsTabTests
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
        public void NotificationsTab_controls_reflect_ntfy_settings()
        {
            var settings = new ThresholdSettings();
            settings.UpdateNtfySettings(s =>
            {
                s.IsEnabled = true;
                s.Topic = "topic-123";
            });
            var monitor = CreateMonitor(settings);
            var form = new OptionsForm((owner, text, caption, buttons, icon) => DialogResult.OK);
            form.Initialize(settings, monitor);

            var enabledCheck = GetCheckBox(form, "ntfyEnabledCheck");
            var topicTextBox = GetTextBox(form, "ntfyTopicTextBox");
            var sendButton = GetButton(form, "sendNtfyTestBtn");

            Assert.True(enabledCheck.Checked);
            Assert.Equal("topic-123", topicTextBox.Text);
            Assert.Equal("Send ntfy test", sendButton.Text);
        }

        private static Button GetButton(Form form, string field)
        {
            var f = form.GetType().GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(f);
            return (Button)f!.GetValue(form)!;
        }

        private static CheckBox GetCheckBox(Form form, string field)
        {
            var f = form.GetType().GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(f);
            return (CheckBox)f!.GetValue(form)!;
        }

        private static TextBox GetTextBox(Form form, string field)
        {
            var f = form.GetType().GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(f);
            return (TextBox)f!.GetValue(form)!;
        }
    }
}
