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
        ShowToast("\u26a0 Low Battery", $"{deviceName}: {battery}% \u2014 charge now");

    public void NotifyHigh(string deviceName, int battery) =>
        ShowToast("\U0001f50b Battery High", $"{deviceName}: {battery}% \u2014 unplug charger");

    public void NotifyDeviceFound(string deviceName, int battery)
    {
        string body = battery >= 0
            ? $"{deviceName}: {battery}%  {BluetoothBatteryMonitor.BatteryBar(battery)}"
            : $"{deviceName}: battery n/a";

        ShowToast("\U0001f535 BT Device Found", body);
    }

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
}
