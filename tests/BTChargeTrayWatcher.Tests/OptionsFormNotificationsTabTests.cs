using System.Windows.Forms;
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
                                            (_, _) => Task.FromResult(new List<DeviceBatteryInfo>()),
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

        [StaFact]
        public void NotificationsTab_access_token_is_masked_and_reads_from_settings()
        {
            var settings = new ThresholdSettings();
            settings.UpdateNtfySettings(s =>
            {
                s.IsEnabled = true;
                s.Topic = "$private-topic";
                s.AccessToken = "tk_secret";
            });
            var monitor = CreateMonitor(settings);
            var form = new OptionsForm((owner, text, caption, buttons, icon) => DialogResult.OK);
            form.Initialize(settings, monitor);

            var tokenTextBox = GetTextBox(form, "ntfyAccessTokenTextBox");

            // #152: the token must never be rendered in clear text.
            Assert.True(tokenTextBox.UseSystemPasswordChar);
            Assert.Equal("tk_secret", tokenTextBox.Text);
        }

        [StaFact]
        public void NotificationsTab_access_token_round_trips_into_settings()
        {
            var settings = new ThresholdSettings();
            var monitor = CreateMonitor(settings);
            var form = new OptionsForm((owner, text, caption, buttons, icon) => DialogResult.OK);
            form.Initialize(settings, monitor);

            var tokenTextBox = GetTextBox(form, "ntfyAccessTokenTextBox");
            Assert.Equal(string.Empty, tokenTextBox.Text);

            tokenTextBox.Text = "tk_abc123";
            Assert.Equal("tk_abc123", settings.GetNtfySettings().AccessToken);

            // Clearing the field removes the token rather than persisting whitespace.
            tokenTextBox.Text = "   ";
            Assert.Null(settings.GetNtfySettings().AccessToken);
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
