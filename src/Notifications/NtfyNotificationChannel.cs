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

    // ── Private ──────────────────────────────────────────────────────────────

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
