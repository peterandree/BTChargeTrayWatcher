using System;
using System.Windows.Forms;

namespace BTChargeTrayWatcher.Tray
{
    public static class OptionsFormManager
    {
        private static OptionsForm? _instance;

        public static void ShowOptionsForm(ThresholdSettings? settings = null, BluetoothBatteryMonitor? monitor = null)
        {
            if (_instance != null && !_instance.IsDisposed)
            {
                _instance.BringToFront();
                _instance.Focus();
                return;
            }

            _instance = new OptionsForm();
            if (settings != null && monitor != null)
                _instance.Initialize(settings, monitor);
            _instance.FormClosed += (_, _) => _instance = null;
            _instance.Show();
        }
    }
}
