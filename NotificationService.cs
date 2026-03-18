using System;
using System.Diagnostics;
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
            ? $"{deviceName}: {battery}%  {BluetoothBatteryMonitor.BatteryBar(battery)}"
            : $"{deviceName}: battery n/a";

        ShowToast("🔵 BT Device Found", body);
    }

    private static void RegisterAumid()
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(
                $@"SOFTWARE\Classes\AppUserModelId\{AppId}");

            key.SetValue("DisplayName", AppDisplay, RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NotificationService] RegisterAumid fault: {ex}");
        }
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
        catch (Exception ex)
        {
            Debug.WriteLine($"[NotificationService] ShowToast fault: {ex}");
        }
    }
}
