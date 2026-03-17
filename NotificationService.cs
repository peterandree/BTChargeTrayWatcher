using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace BTChargeTrayWatcher;

public class NotificationService
{
    private const string AppId = "BTChargeTrayWatcher";
    private const string AppDisplay = "BT Charge Tray Watcher";

    public NotificationService()
    {
        RegisterAumid();
    }

    public void NotifyLow(string deviceName, int battery) =>
        ShowToast("⚠ Low Battery", $"{deviceName}: {battery}% — charge now");

    public void NotifyHigh(string deviceName, int battery) =>
        ShowToast("🔋 Battery High", $"{deviceName}: {battery}% — unplug charger");

    public void NotifyDeviceFound(string deviceName, int battery)
    {
        string body = battery >= 0
            ? $"{deviceName}: {battery}%  {BatteryBar(battery)}"
            : $"{deviceName}: battery n/a";

        ShowToast("🔵 BT Device Found", body);
    }

    // Register AUMID so unpackaged app can send toasts [web:36]
    private static void RegisterAumid()
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(
                $@"SOFTWARE\Classes\AppUserModelId\{AppId}");
            key.SetValue("DisplayName", AppDisplay, RegistryValueKind.String);
        }
        catch { }
    }

    private static void ShowToast(string title, string message)
    {
        try
        {
            // Escape XML special chars
            string safeTitle = System.Security.SecurityElement.Escape(title);
            string safeMessage = System.Security.SecurityElement.Escape(message);

            string xml = $"""
                <toast>
                  <visual>
                    <binding template="ToastGeneric">
                      <text>{safeTitle}</text>
                      <text>{safeMessage}</text>
                    </binding>
                  </visual>
                </toast>
                """;

            var doc = new XmlDocument();
            doc.LoadXml(xml);

            ToastNotificationManager
                .CreateToastNotifier(AppId)
                .Show(new ToastNotification(doc));
        }
        catch { }
    }

    private static string BatteryBar(int pct)
    {
        int filled = (int)Math.Round(pct / 10.0);
        return "[" + new string('█', filled) + new string('░', 10 - filled) + "]";
    }
}
