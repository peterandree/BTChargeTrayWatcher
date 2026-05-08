using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BTChargeTrayWatcher;

/// <summary>
/// Builds the "Mobile notifications" tray submenu and handles all
/// user interactions for the ntfy integration. All state mutations
/// go through <see cref="ThresholdSettings.UpdateNtfySettings"/>.
/// </summary>
internal sealed class NtfyMobileNotificationsMenuBuilder
{
    private readonly ThresholdSettings           _settings;
    private readonly NtfyNotificationChannel     _ntfyChannel;

    public NtfyMobileNotificationsMenuBuilder(
        ThresholdSettings       settings,
        NtfyNotificationChannel ntfyChannel)
    {
        _settings    = settings;
        _ntfyChannel = ntfyChannel;
    }

    public ToolStripMenuItem Build()
    {
        var root = new ToolStripMenuItem("\U0001f4f1 Mobile notifications");
        root.DropDownOpening += (_, _) => Rebuild(root);
        Rebuild(root);
        return root;
    }

    // ── Menu population ──────────────────────────────────────────────────────

    private void Rebuild(ToolStripMenuItem root)
    {
        while (root.DropDownItems.Count > 0)
        {
            var item = root.DropDownItems[0];
            root.DropDownItems.RemoveAt(0);
            item.Dispose();
        }

        var ntfy = _settings.GetNtfySettings();

        // Status header (non-interactive)
        string statusText = ntfy.IsConfigured
            ? ntfy.IsEnabled ? "Status: Enabled" : "Status: Configured, disabled"
            : "Status: Not configured";
        root.DropDownItems.Add(new ToolStripMenuItem(statusText) { Enabled = false });
        root.DropDownItems.Add(new ToolStripSeparator());

        if (!ntfy.IsConfigured)
        {
            AddGenerateTopic(root);
            AddSeparator(root);
            AddGuides(root);
            return;
        }

        // Show current topic (read-only)
        root.DropDownItems.Add(new ToolStripMenuItem($"Topic: {ntfy.Topic}") { Enabled = false });
        root.DropDownItems.Add(new ToolStripSeparator());

        // Enable / Disable
        if (ntfy.IsEnabled)
        {
            AddItem(root, "Disable ntfy notifications", () =>
                _settings.UpdateNtfySettings(s => s.IsEnabled = false));
        }
        else
        {
            AddItem(root, "Enable ntfy notifications", () =>
                _settings.UpdateNtfySettings(s => s.IsEnabled = true));
        }

        // Test notification
        AddItem(root, "Send test notification", () =>
        {
            _ = SendTestAsync();
        });

        AddSeparator(root);
        AddGenerateTopic(root, label: "Regenerate topic");
        AddSeparator(root);
        AddGuides(root);
    }

    // ── Actions ──────────────────────────────────────────────────────────────

    private void AddGenerateTopic(ToolStripMenuItem root, string label = "Generate topic")
    {
        AddItem(root, label, () =>
        {
            string topic = NtfyTopicGenerator.Generate();
            _settings.UpdateNtfySettings(s =>
            {
                s.Topic     = topic;
                s.IsEnabled = false; // require explicit re-enable after regen
            });
            MessageBox.Show(
                $"New topic generated:\n\n{topic}\n\nSubscribe to this topic in the ntfy app on your phone using server ntfy.sh, then enable the integration.",
                "Mobile notifications — new topic",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        });
    }

    private async Task SendTestAsync()
    {
        bool ok = await _ntfyChannel.SendTestNotificationAsync().ConfigureAwait(false);
        string msg = ok
            ? "Test notification sent. Check your phone."
            : "Failed to send test notification. Check your internet connection and verify the topic is correct.";
        MessageBox.Show(msg, "Mobile notifications — test",
            MessageBoxButtons.OK,
            ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private static void AddGuides(ToolStripMenuItem root)
    {
        AddItem(root, "Android setup guide",  () => OpenUrl("https://docs.ntfy.sh/subscribe/phone/"));
        AddItem(root, "iPhone setup guide",   () => OpenUrl("https://docs.ntfy.sh/subscribe/phone/"));
        AddItem(root, "ntfy integration guide", () => OpenUrl("https://ntfy.sh"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void AddItem(ToolStripMenuItem parent, string text, Action onClick)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) =>
        {
            try { onClick(); }
            catch (Exception ex) { Debug.WriteLine($"[NtfyMenu] Click fault on '{text}': {ex}"); }
        };
        parent.DropDownItems.Add(item);
    }

    private static void AddSeparator(ToolStripMenuItem parent)
        => parent.DropDownItems.Add(new ToolStripSeparator());

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NtfyMenu] OpenUrl fault: {ex}");
        }
    }
}
