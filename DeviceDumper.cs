using System.Management;

namespace BTChargeTrayWatcher;

public class DeviceDumper
{
    private const string BatteryPKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    public async Task DumpToDesktopAsync()
    {
        var lines = new List<string>();

        lines.Add("=== WMI BTHENUM PnP Devices ===");
        await Task.Run(() =>
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DeviceID, Status FROM Win32_PnPEntity " +
                "WHERE DeviceID LIKE 'BTHENUM%'");

            foreach (ManagementObject obj in searcher.Get())
            {
                string? name = obj["Name"]?.ToString();
                string? instanceId = obj["DeviceID"]?.ToString();
                string? status = obj["Status"]?.ToString();

                lines.Add($"  Name={name}  Status={status}");
                lines.Add($"  InstanceId={instanceId}");

                if (instanceId is not null)
                {
                    int val = ClassicBatteryReader.QueryPnpBattery(instanceId);
                    lines.Add($"    {BatteryPKey} => {val}");
                }
                lines.Add("");
            }
        });

        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "BTBatteryDump.txt");

        await File.WriteAllLinesAsync(path, lines);
    }
}
