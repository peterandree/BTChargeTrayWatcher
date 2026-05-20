using System;
using System.Windows.Forms;
using BTChargeTrayWatcher;
using BTChargeTrayWatcher.Tray;
using Xunit;

namespace BTChargeTrayWatcher.Tests
{
    public sealed class OptionsFormNotificationsTabEdgeTests
    {
        [StaFact]
        public void TestNotificationButton_null_notifier_does_not_throw()
        {
            var settings = new ThresholdSettings();
            var monitor = new BluetoothBatteryMonitor(settings, null!);
            var form = new OptionsForm();
            form.Initialize(settings, monitor, null);
            var btn = GetButton(form, "testNotificationBtn");
            var ex = Record.Exception(() => btn.PerformClick());
            Assert.Null(ex);
        }

        private static Button GetButton(Form form, string field)
        {
            var f = form.GetType().GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(f);
            return (Button)f!.GetValue(form)!;
        }
    }
}
