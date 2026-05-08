using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace BTChargeTrayWatcher;

/// <summary>
/// Publishes battery alerts to the ntfy.sh public push notification server.
/// HTTP transport concern is fully isolated here — no domain class owns it.
/// </summary>
public sealed class NtfyNotificationChannel : INotificationChannel
{
    private const string NtfyBaseUrl = "https://ntfy.sh/";
    private const string AppTitle    = "BTChargeTrayWatcher";

    private static readonly HttpClient _http = new();

    private readonly NtfyIntegrationSettings _ntfySettings;

    public NtfyNotificationChannel(NtfyIntegrationSettings ntfySettings)
    {
        _ntfySettings = ntfySettings;
    }

    public void NotifyLow(string deviceName, int battery)
        => Fire($"{deviceName} battery low: {battery}%", priority: "high");

    public void NotifyHigh(string deviceName, int battery)
        => Fire($"{deviceName} battery high: {battery}%", priority: "high");

    public void NotifyLaptopLow(int battery)
        => Fire($"Laptop battery low: {battery}%", priority: battery <= 10 ? "urgent" : "high");

    public void NotifyLaptopHigh(int battery)
        => Fire($"Laptop battery high: {battery}%", priority: "default");

    /// <summary>
    /// Sends a test notification to verify the integration is working.
    /// Returns true on HTTP 2xx, false otherwise.
    /// </summary>
    public async Task<bool> SendTestNotificationAsync()
    {
        if (!_ntfySettings.IsEnabled || string.IsNullOrWhiteSpace(_ntfySettings.Topic))
            return false;

        return await PublishAsync("BTChargeTrayWatcher test notification", "default"
        ).ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes the current charge status of all known devices as a single
    /// notification. Each device appears on its own line.
    /// Returns true on HTTP 2xx, false otherwise.
    /// </summary>
    public async Task<bool> SendStatusReportAsync(
        System.Collections.Generic.IReadOnlyList<DeviceBatteryInfo> btDevices,
        LaptopBatteryInfo? laptop)
    {
        if (!_ntfySettings.IsEnabled || string.IsNullOrWhiteSpace(_ntfySettings.Topic))
            return false;

        var sb = new StringBuilder();

        foreach (var d in btDevices)
        {
            if (d.Battery is null) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(d.Name).Append(" ").Append(d.Battery.Value).Append('%');
            if (d.IsCharging == true) sb.Append(" \u26a1");
        }

        if (laptop is { HasBattery: true })
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append("Laptop ").Append(laptop.BatteryPercent).Append('%');
            if (laptop.IsCharging)   sb.Append(" (charging)");
            else if (laptop.IsOnAcPower) sb.Append(" (plugged in)");
        }

        if (sb.Length == 0)
            sb.Append("No devices currently known.");

        return await PublishAsync(sb.ToString(), "default").ConfigureAwait(false);
    }

    // ── Private ─────────────────────────────────────────────────────────────────────────

    private void Fire(string body, string priority)
    {
        if (!_ntfySettings.IsEnabled || string.IsNullOrWhiteSpace(_ntfySettings.Topic))
            return;

        // Fire-and-forget; faults are logged, never propagated to callers.
        _ = PublishAsync(body, priority);
    }

    private async Task<bool> PublishAsync(string body, string priority)
    {
        try
        {
            string url = NtfyBaseUrl + _ntfySettings.Topic!.Trim();

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/plain")
            };
            request.Headers.TryAddWithoutValidation("Title",    AppTitle);
            request.Headers.TryAddWithoutValidation("Priority", priority);

            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                Debug.WriteLine($"[NtfyNotificationChannel] Publish failed: HTTP {(int)response.StatusCode}");

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NtfyNotificationChannel] Publish fault: {ex}");
            return false;
        }
    }
}
