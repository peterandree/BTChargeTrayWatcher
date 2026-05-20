using System;
using System.Windows.Forms;
using BTChargeTrayWatcher;
using BTChargeTrayWatcher.Tray;
using Xunit;

namespace BTChargeTrayWatcher.Tests
{
    public sealed class OptionsFormNotificationsTabTests
    {
        [StaFact]
        public void TestNotificationButton_invokes_notifier()
        {
            var settings = new ThresholdSettings();
            var monitor = new BluetoothBatteryMonitor(settings, null!);
            var called = false;
            var notifier = new TestNotifier(() => called = true);
            var form = new OptionsForm();
            form.Initialize(settings, monitor, notifier);

            var btn = GetButton(form, "testNotificationBtn");
            btn.PerformClick();
            if (!called)
            {
                // Fallback: directly invoke OnClick if PerformClick does not work
                var onClick = btn.GetType().GetMethod("OnClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                onClick?.Invoke(btn, new object[] { EventArgs.Empty });
            }
            Assert.True(called);
        }

        private sealed class TestNotifier : INotificationService
        {
            private readonly Action _onNotify;
            public TestNotifier(Action onNotify) => _onNotify = onNotify;
            public void NotifyLow(string deviceName, int battery) => _onNotify();
            public void NotifyHigh(string deviceName, int battery) => throw new NotImplementedException();
            public void NotifyLaptopLow(int battery) => throw new NotImplementedException();
            public void NotifyLaptopHigh(int battery) => throw new NotImplementedException();
        }

        private static Button GetButton(Form form, string field)
        {
            var f = form.GetType().GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(f);
            return (Button)f!.GetValue(form)!;
        }
    }
}
