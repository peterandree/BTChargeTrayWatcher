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
                var value = Registry.GetValue($"HKEY_CURRENT_USER\\{RunKey}", AppName, null) as string;
                if (string.IsNullOrWhiteSpace(value)) return false;
                string expected = $"\"{Application.ExecutablePath}\"";
                return string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    public static void Enable()
    {
        try
        {
            Registry.SetValue($"HKEY_CURRENT_USER\\{RunKey}", AppName, $"\"{Application.ExecutablePath}\"");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StartupRegistration] Enable Fault: {ex}");
        }
    }

    public static void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            key?.DeleteValue(AppName, false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StartupRegistration] Disable Fault: {ex}");
        }
    }
}
