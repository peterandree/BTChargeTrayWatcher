namespace BTChargeTrayWatcher;

public interface IClassicBatteryPropertyReader
{
    Dictionary<string, (int Battery, bool? IsCharging)> ReadBatteryProperties(IEnumerable<string> instanceIds);
}
