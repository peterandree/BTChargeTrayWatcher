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
            // Directly invoke the click handler for headless test reliability
            btn.PerformClick();
            if (!called)
            {
                // Fallback: directly invoke click event handler if PerformClick did not work (headless)
                var clickEvent = btn.GetType().GetEvent("Click");
                var method = typeof(OptionsForm).GetMethod("<OptionsForm>b__0_0", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (method != null)
                    method.Invoke(form, new object[] { btn, EventArgs.Empty });
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
