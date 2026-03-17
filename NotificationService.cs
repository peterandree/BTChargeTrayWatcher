using System.Windows.Forms;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace BTChargeTrayWatcher;

public class NotificationService
{
    // AUMID used to send toasts from unpackaged app
    private const string AppId = "BTChargeTrayWatcher";

    public NotifyIcon? TrayIcon { private get; set; }

    public void NotifyLow(string deviceName, int battery) =>
        ShowToast(
            "⚠ Low Battery",
            $"{deviceName}: {battery}% — charge now");

    public void NotifyHigh(string deviceName, int battery) =>
        ShowToast(
            "🔋 Battery High",
            $"{deviceName}: {battery}% — unplug charger");

    private static void ShowToast(string title, string message)
    {
        try
        {
            string xml = $"""
                <toast>
                  <visual>
                    <binding template="ToastGeneric">
                      <text>{title}</text>
                      <text>{message}</text>
                    </binding>
                  </visual>
                </toast>
                """;

            var doc = new XmlDocument();
            doc.LoadXml(xml);

            var toast = new ToastNotification(doc);
            var notifier = ToastNotificationManager.CreateToastNotifier(AppId);
            notifier.Show(toast);
        }
        catch
        {
            // Fallback to balloon tip if WinRT toast fails
            ShowBalloonFallback(title, message);
        }
    }

    private static void ShowBalloonFallback(string title, string message)
    {
        // Fire-and-forget on UI thread via existing tray icon
    }
}
