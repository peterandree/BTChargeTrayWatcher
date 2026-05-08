# Plan: Mobile Push Notifications via ntfy.sh

**Status:** Proposed  
**Author:** Architecture review  
**Date:** 2026-05-08

---

## Goal

Deliver battery threshold alerts to iOS and Android phones without running any additional infrastructure. The Windows tray app publishes a plain HTTP POST to the public ntfy.sh server. The ntfy mobile app receives the push notification natively.

---

## Constraints

| Constraint | Decision |
|---|---|
| No self-hosted server | Use `https://ntfy.sh` public service |
| No external dependencies added to the build | Use `System.Net.Http.HttpClient` (already in BCL) |
| Adhere to ADR-001 (single constructor, no nullable fields) | `NtfySettings` carried as required constructor param |
| Adhere to ADR-005 (atomic persistence) | `NtfySettings` added to `SettingsDto` and `SettingsSnapshot` |
| Adhere to ADR-006 (notifications via `INotificationService`) | `NtfyNotificationService` wraps existing interface alongside the WinRT service |
| Feature is opt-in; zero impact when disabled | Guard all HTTP calls behind `IsEnabled` flag |
| User must never need to touch a config file | Wizard in the tray menu guides through topic setup |

---

## Architecture

### New types

```
src/
  Notifications/
    NtfySettings.cs           // immutable options record (topic, server url, enabled flag)
    NtfyNotificationService.cs // implements INotificationService, fires-and-forgets HTTP POST
    NtfySetupWizard.cs        // WinForms multi-step dialog
  Settings/
    SettingsPersistence.cs    // extended: NtfySettings round-trips through SettingsDto
    ThresholdSettings.cs      // extended: NtfySettings property + NtfySettingsChanged event
docs/
  ntfy-install-ios.md
  ntfy-install-android.md
  ntfy-setup.md
  plans/
    ntfy-push-notifications.md  (this file)
  adr/
    adr-014-ntfy-push-notifications.md
```

### Data flow

```
TrayApp.cs (battery alert triggered)
  └─► INotificationService.NotifyLow / NotifyHigh
        ├─► NotificationService (WinRT toast)          [existing, unchanged]
        └─► NtfyNotificationService.NotifyLow/High     [new]
              └─ if NtfySettings.IsEnabled
                   HttpClient.PostAsync("https://ntfy.sh/{topic}", body)
```

Both services are wired at startup in `TrayApp`. No dispatcher/event bus introduced — direct call, matching existing style.

### `NtfySettings` record

```csharp
// src/Notifications/NtfySettings.cs
namespace BTChargeTrayWatcher;

/// <summary>Immutable snapshot of ntfy.sh configuration.</summary>
public sealed record NtfySettings
{
    public static readonly NtfySettings Disabled = new();

    public bool   IsEnabled { get; init; } = false;
    public string Topic     { get; init; } = string.Empty;
    public string ServerUrl { get; init; } = "https://ntfy.sh";
}
```

### `NtfyNotificationService`

```csharp
// src/Notifications/NtfyNotificationService.cs
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace BTChargeTrayWatcher;

/// <summary>
/// Forwards battery alerts to a ntfy.sh topic via HTTP POST.
/// Disabled by default; activates when <see cref="NtfySettings.IsEnabled"/> is true.
/// Fire-and-forget: failures are logged to Debug, never surfaced to the caller.
/// </summary>
public sealed class NtfyNotificationService : INotificationService
{
    private static readonly HttpClient _http = new();

    private readonly ThresholdSettings _settings;

    public event Action? OnNotificationClicked { add { } remove { } }

    public NtfyNotificationService(ThresholdSettings settings)
    {
        _settings = settings;
    }

    public void NotifyLow(string deviceName, int battery)
        => Post($"Battery Low", $"{deviceName} is at {battery}%. Please plug it in.", priority: "high");

    public void NotifyHigh(string deviceName, int battery)
        => Post($"Battery High", $"{deviceName} is at {battery}%. Consider unplugging it.", priority: "default");

    public void NotifyLaptopLow(int battery)
        => Post("Laptop Battery Low", $"Laptop is at {battery}%. Plug in charger.", priority: "high");

    public void NotifyLaptopHigh(int battery)
        => Post("Laptop Battery High", $"Laptop is at {battery}%. Consider unplugging.", priority: "default");

    // ── Private ──────────────────────────────────────────────────────────────

    private void Post(string title, string message, string priority)
    {
        var s = _settings.Ntfy;
        if (!s.IsEnabled || string.IsNullOrWhiteSpace(s.Topic)) return;

        _ = PostAsync(s.ServerUrl.TrimEnd('/') + "/" + s.Topic, title, message, priority);
    }

    private static async Task PostAsync(string url, string title, string message, string priority)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.TryAddWithoutValidation("Title",    title);
            req.Headers.TryAddWithoutValidation("Priority", priority);
            req.Headers.TryAddWithoutValidation("Tags",     "battery");
            req.Content = new StringContent(message, Encoding.UTF8, "text/plain");

            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                Debug.WriteLine($"[Ntfy] POST failed {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Ntfy] POST fault: {ex.Message}");
        }
    }
}
```

### `NtfySetupWizard` (tray-menu entry point)

A `Form`-based 3-step wizard:

| Step | Content |
|---|---|
| 1 – Install | Instructions + deep-link / QR to App Store / Play Store |
| 2 – Topic | TextBox for topic name; "Generate random" button produces `btwatcher-{Guid[..8]}`; live preview of the full URL |
| 3 – Test | "Send test notification" button fires a real POST; user confirms receipt; Finish enables the feature |

The wizard writes directly to `ThresholdSettings.Ntfy`, which auto-saves through the existing `SettingsPersistence` subscriber.

### Menu wiring

In `TrayMenuBuilder.Build()`, insert before the separator above "Exit":

```csharp
var ntfyItem = new ToolStripMenuItem("📱 Mobile notifications (ntfy.sh)…");
ntfyItem.Click += (_, _) => new NtfySetupWizard(settings).ShowDialog();
menu.Items.Insert(menu.Items.IndexOf(autostartItem) + 1, ntfyItem);
// optional: add checkmark if already enabled
ntfyItem.Checked = settings.Ntfy.IsEnabled;
```

### Settings persistence extension

Add to `SettingsDto`:

```csharp
public bool   NtfyEnabled   { get; set; }
public string NtfyTopic     { get; set; } = string.Empty;
public string NtfyServerUrl { get; set; } = "https://ntfy.sh";
```

Add to `ThresholdSettings`:

```csharp
public NtfySettings Ntfy { get; set; } = NtfySettings.Disabled;
public event Action? NtfySettingsChanged;
```

Round-trip in `Load()` / `Save()` following the existing pattern.

---

## Privacy & security

- Topics on the public ntfy.sh are **not access-controlled by default**. Document this prominently and recommend a random UUID-based topic name (wizard generates one automatically).
- No credentials, tokens, or personal data are transmitted beyond the battery % and device name the user already sees locally.
- Users who want private topics can self-host ntfy or use a reserved topic with a Bearer token — out of scope for this plan but the `ServerUrl` field accommodates it.

---

## Out of scope

- ntfy access tokens / authentication headers (follow-up ADR if needed)
- Self-hosted ntfy server setup
- Notification history / delivery confirmation UI
- Per-device ntfy enable/disable
