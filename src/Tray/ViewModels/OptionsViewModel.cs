// src/Tray/ViewModels/OptionsViewModel.cs
// Presentation logic for the General and Notifications tabs of OptionsForm.
// No WinForms dependency — fully unit-testable.
namespace BTChargeTrayWatcher;

internal sealed class OptionsViewModel
{
    private readonly ThresholdSettings _settings;
    private readonly INotificationService? _notifier;

    // ── General thresholds ────────────────────────────────────────────────

    public int GlobalLow
    {
        get => _settings.Low;
        set => _settings.Low = value;
    }

    public int GlobalHigh
    {
        get => _settings.High;
        set => _settings.High = value;
    }

    public int LaptopLow
    {
        get => _settings.LaptopLow;
        set => _settings.LaptopLow = value;
    }

    public int LaptopHigh
    {
        get => _settings.LaptopHigh;
        set => _settings.LaptopHigh = value;
    }

    public bool ExcludeLaptopFromTrayIconOverlay
    {
        get => _settings.ExcludeLaptopFromTrayIconOverlay;
        set => _settings.ExcludeLaptopFromTrayIconOverlay = value;
    }

    // ── Auto-start (Windows startup) ─────────────────────────────────────
    public bool AutoStartEnabled
    {
        get => StartupRegistration.IsEnabled;
        set
        {
            if (value) StartupRegistration.Enable();
            else StartupRegistration.Disable();
        }
    }

    // ── ntfy ─────────────────────────────────────────────────────────────

    public bool NtfyEnabled
    {
        get => _settings.GetNtfySettings().IsEnabled;
        set => _settings.UpdateNtfySettings(s => s.IsEnabled = value);
    }

    public string NtfyTopic => _settings.GetNtfySettings().Topic ?? string.Empty;

    /// <summary>
    /// Generates a new random ntfy topic, disables the integration until the
    /// user re-enables it, and returns the new topic string.
    /// </summary>
    public string RegenerateTopic()
    {
        string topic = NtfyTopicGenerator.Generate();
        _settings.UpdateNtfySettings(s =>
        {
            s.Topic = topic;
            s.IsEnabled = false;
        });
        return topic;
    }

    /// <summary>
    /// Sends a test ntfy notification using the current settings.
    /// Returns true on success, false on failure.
    /// </summary>
    public async Task<bool> SendTestAsync()
    {
        var channel = new NtfyNotificationChannel(_settings.GetNtfySettings());
        return await channel.SendTestNotificationAsync().ConfigureAwait(false);
    }

    public OptionsViewModel(ThresholdSettings settings, INotificationService? notifier = null)
    {
        _settings = settings;
        _notifier = notifier;
    }
}
