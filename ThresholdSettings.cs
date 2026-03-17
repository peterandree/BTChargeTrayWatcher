using Microsoft.Win32;

namespace BTChargeTrayWatcher;

public class ThresholdSettings
{
    private const string RegKey = @"Software\BTChargeTrayWatcher";

    public int Low { get; private set; } = 20;
    public int High { get; private set; } = 80;

    public event Action? Changed;

    public ThresholdSettings()
    {
        Load();
    }

    public void SetLow(int value)
    {
        if (value >= High)
            throw new ArgumentOutOfRangeException(nameof(value),
                $"Low threshold ({value}) must be strictly less than High threshold ({High}).");
        Low = value;
        Save();
        Changed?.Invoke();
    }

    public void SetHigh(int value)
    {
        if (value <= Low)
            throw new ArgumentOutOfRangeException(nameof(value),
                $"High threshold ({value}) must be strictly greater than Low threshold ({Low}).");
        High = value;
        Save();
        Changed?.Invoke();
    }

    private void Load()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegKey);
        if (key is null) return;
        Low = (int)(key.GetValue("Low", Low) ?? Low);
        High = (int)(key.GetValue("High", High) ?? High);
    }

    private void Save()
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegKey);
        key.SetValue("Low", Low, RegistryValueKind.DWord);
        key.SetValue("High", High, RegistryValueKind.DWord);
    }
}
