using System.Management;

namespace BTChargeTrayWatcher;

public class DeviceDumper
{
    private const string BatteryPKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    private readonly ClassicBatteryPropertyReader _batteryPropertyReader = new();

    public async Task DumpToDesktopAsync()
    {
        var lines = new List<string>();
        var devices = new List<(string? Name, string? InstanceId, string? Status)>();

        lines.Add("=== WMI BTHENUM PnP Devices ===");

        await Task.Run(() =>
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DeviceID, Status FROM Win32_PnPEntity " +
                "WHERE DeviceID LIKE 'BTHENUM%'");

            foreach (ManagementObject obj in searcher.Get())
            {
                devices.Add((
                    obj["Name"]?.ToString(),
                    obj["DeviceID"]?.ToString(),
                    obj["Status"]?.ToString()));
            }
        });

        foreach (var (name, instanceId, status) in devices)
        {
            lines.Add($"  Name={name}  Status={status}");
            lines.Add($"  InstanceId={instanceId}");

            if (instanceId is not null)
            {
                int val = await Task.Run(() => _batteryPropertyReader.ReadBatteryProperty(instanceId));
                lines.Add($"    {BatteryPKey} => {val}");
            }

            lines.Add("");
        }

        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "BTBatteryDump.txt");

        await File.WriteAllLinesAsync(path, lines);
    }
}
