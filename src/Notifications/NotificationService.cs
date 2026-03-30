using System.Diagnostics;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace BTChargeTrayWatcher;

public sealed class NotificationService
{
    private const string AppId = "BTChargeTrayWatcher";
    private readonly bool _toastsSupported;

    public event Action? OnNotificationClicked;

    public NotificationService()
    {
        _toastsSupported = CheckToastSupport();

        if (_toastsSupported)
        {
            try
            {
                RegisterAumid();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NotificationService] RegisterAumid fault: {ex}");
            }
        }
        else
        {
            Debug.WriteLine("[NotificationService] Windows Toast Notifications are not supported or available on this OS build.");
        }
    }

    public void NotifyLow(string deviceName, int battery)
    {
        string title = "Battery Low";
        string message = $"{deviceName} is at {battery}%. Please plug it in.";
        ShowToast(title, message);
    }

    public void NotifyHigh(string deviceName, int battery)
    {
        string title = "Battery High";
        string message = $"{deviceName} is at {battery}%. Consider unplugging it.";
        ShowToast(title, message);
    }

    private void ShowToast(string title, string message)
    {
        if (!_toastsSupported)
            return;

        try
        {
            string toastXmlString = $@"
                <toast>
                    <visual>
                        <binding template='ToastGeneric'>
                            <text hint-maxLines='1'>{System.Security.SecurityElement.Escape(title)}</text>
                            <text>{System.Security.SecurityElement.Escape(message)}</text>
                        </binding>
                    </visual>
                </toast>";

            var xmlDoc = new XmlDocument();
            xmlDoc.LoadXml(toastXmlString);

            var toast = new ToastNotification(xmlDoc);

            toast.Activated += (sender, args) =>
            {
                try
                {
                    OnNotificationClicked?.Invoke();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[NotificationService] Toast activation fault: {ex}");
                }
            };

            toast.Failed += (sender, args) =>
            {
                Debug.WriteLine($"[NotificationService] Toast failed to display: {args.ErrorCode?.Message}");
            };

            var notifier = ToastNotificationManager.CreateToastNotifier(AppId);
            notifier.Show(toast);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NotificationService] ShowToast critical fault: {ex}");
        }
    }

    private static bool CheckToastSupport()
    {
        try
        {
            return ToastNotificationManager.History != null;
        }
        catch
        {
            return false;
        }
    }

    private static void RegisterAumid()
    {
        string exePath = System.Windows.Forms.Application.ExecutablePath;
        string keyPath = $@"Software\Classes\AppUserModelId\{AppId}";

        using RegistryKey? key = Registry.CurrentUser.CreateSubKey(keyPath);
        if (key != null)
        {
            key.SetValue("DisplayName", "BTChargeTrayWatcher");
            key.SetValue("IconUri", exePath);
        }
    }

    public void NotifyLaptopLow(int battery)
    {
        string title = "Laptop Battery Low";
        string message = $"Laptop battery is at {battery}%. Please plug in your charger.";
        ShowToast(title, message);
    }

    public void NotifyLaptopHigh(int battery)
    {
        string title = "Laptop Battery High";
        string message = $"Laptop battery is at {battery}%. Consider unplugging to preserve battery health.";
        ShowToast(title, message);
    }

}
