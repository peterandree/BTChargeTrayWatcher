using System.Windows.Forms;
using BTChargeTrayWatcher;
using BTChargeTrayWatcher.Tray;
using Xunit;

namespace BTChargeTrayWatcher.Tests
{
    public sealed class OptionsFormNotificationsTabEdgeTests
    {
        [StaFact]
        public void NotificationsTab_enable_toggle_updates_settings_without_notifier()
        {
            var settings = new ThresholdSettings();
            settings.UpdateNtfySettings(s =>
            {
                s.IsEnabled = false;
                s.Topic = "topic-123";
            });

            var monitor = new BluetoothBatteryMonitor(settings, null!);
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
