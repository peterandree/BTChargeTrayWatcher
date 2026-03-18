using Microsoft.Win32;

namespace BTChargeTrayWatcher;

public class ThresholdSettings
{
    private const string RegKey = @"Software\BTChargeTrayWatcher";
    private const int DefaultLow = 20;
    private const int DefaultHigh = 80;
    private const int MinThreshold = 0;
    private const int MaxThreshold = 100;

    public int Low { get; private set; } = DefaultLow;
    public int High { get; private set; } = DefaultHigh;

    public event Action? Changed;

    public ThresholdSettings()
    {
        Load();
    }

    public void SetLow(int value)
    {
        ValidateRange(value, nameof(value));

        if (value >= High)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"Low threshold ({value}) must be strictly less than High threshold ({High}).");
        }

        Low = value;
        Save();
        Changed?.Invoke();
    }

    public void SetHigh(int value)
    {
        ValidateRange(value, nameof(value));

        if (value <= Low)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"High threshold ({value}) must be strictly greater than Low threshold ({Low}).");
        }

        High = value;
        Save();
        Changed?.Invoke();
    }

    private void Load()
    {
        Low = DefaultLow;
        High = DefaultHigh;

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegKey);
            if (key is null)
            {
                return;
            }

            int? low = ReadIntValue(key, "Low");
            int? high = ReadIntValue(key, "High");

            if (low is int parsedLow)
            {
                Low = Clamp(parsedLow);
            }

            if (high is int parsedHigh)
            {
                High = Clamp(parsedHigh);
            }

            Normalize();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BTChargeTrayWatcher] ThresholdSettings.Load fault: {ex}");
            Low = DefaultLow;
            High = DefaultHigh;
        }
    }

    private void Save()
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegKey);
        key.SetValue("Low", Low, RegistryValueKind.DWord);
        key.SetValue("High", High, RegistryValueKind.DWord);
    }

    private static int? ReadIntValue(RegistryKey key, string name)
    {
        object? raw = key.GetValue(name, null);
        if (raw is null)
        {
            return null;
        }

        return raw switch
        {
            int i => i,
            byte b => b,
            short s => s,
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            string text when int.TryParse(text, out int parsed) => parsed,
            _ => null
        };
    }

    private void Normalize()
    {
        Low = Clamp(Low);
        High = Clamp(High);

        if (Low >= High)
        {
            Low = DefaultLow;
            High = DefaultHigh;
        }
    }

    private static void ValidateRange(int value, string paramName)
    {
        if (value < MinThreshold || value > MaxThreshold)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                $"Threshold must be between {MinThreshold} and {MaxThreshold}.");
        }
    }

    private static int Clamp(int value) =>
        Math.Min(MaxThreshold, Math.Max(MinThreshold, value));
}
