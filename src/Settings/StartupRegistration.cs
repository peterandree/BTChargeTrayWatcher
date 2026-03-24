using System.Windows.Forms;
using Microsoft.Win32;

namespace BTChargeTrayWatcher;

internal static class StartupRegistration
{
    private const string AppName = "BTChargeTrayWatcher";
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
                var stored = key?.GetValue(AppName) as string;
                if (string.IsNullOrWhiteSpace(stored)) return false;
                string expected = $"\"{Application.ExecutablePath}\"";
                return string.Equals(stored, expected, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
                if (key == null) return;

                if (value)
                    key.SetValue(AppName, $"\"{Application.ExecutablePath}\"");
                else
                    key.DeleteValue(AppName, false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StartupRegistration] Fault: {ex}");
            }
        }
    }
}
