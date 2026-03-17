using System.Diagnostics;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Devices.Bluetooth;

namespace BTChargeTrayWatcher;

public class ClassicBatteryReader
{
    private const string BatteryPKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string HfagPattern = "_HCIBYPASS_";

    // MAC extracted from InstanceId: BTHENUM\...\7&xxx&0&7CC95E99FBB3_C00000000
    private static readonly Regex MacRegex =
        new(@"&0&([0-9A-Fa-f]{12})_", RegexOptions.Compiled);

    public Task<List<(string Name, int Battery)>> ReadAllAsync() =>
        Task.Run(ReadAllAsync_Internal);

    private static async Task<List<(string Name, int Battery)>> ReadAllAsync_Internal()
    {
        var candidates = new List<(string Name, string InstanceId, ulong Address)>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT Name, DeviceID FROM Win32_PnPEntity " +
                $"WHERE DeviceID LIKE 'BTHENUM%{HfagPattern}%'");

            foreach (ManagementObject obj in searcher.Get())
            {
                string? rawName = obj["Name"]?.ToString();
                string? instanceId = obj["DeviceID"]?.ToString();
                if (rawName is null || instanceId is null) continue;

                Match match = MacRegex.Match(instanceId);
                if (!match.Success) continue;

                ulong address = Convert.ToUInt64(match.Groups[1].Value, 16);

                string name = Regex.Replace(
                    rawName,
                    @"\s*(Hands-Free AG|HFP|AG)$",
                    "",
                    RegexOptions.IgnoreCase).Trim();

                candidates.Add((name, instanceId, address));
            }
        }
        catch
        {
        }

        if (candidates.Count == 0) return new();

        var connectTasks = candidates.Select(async c => (c, connected: await IsConnectedAsync(c.Address)));
        var connectResults = await Task.WhenAll(connectTasks);

        var connected = connectResults
            .Where(r => r.connected)
            .Select(r => r.c)
            .ToList();

        if (connected.Count == 0) return new();

        var batteryMap = await BatchQueryPnpBatteryAsync(
            connected.Select(c => c.InstanceId).ToList());

        var results = new List<(string, int)>();
        foreach (var (name, instanceId, _) in connected)
        {
            if (batteryMap.TryGetValue(instanceId, out int battery) && battery >= 0)
                results.Add((name, battery));
        }

        return results;
    }

    private static async Task<bool> IsConnectedAsync(ulong bluetoothAddress)
    {
        try
        {
            using BluetoothDevice? device =
                await BluetoothDevice.FromBluetoothAddressAsync(bluetoothAddress)
                    .AsTask().ConfigureAwait(false);

            return device?.ConnectionStatus == BluetoothConnectionStatus.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static Task<Dictionary<string, int>> BatchQueryPnpBatteryAsync(List<string> instanceIds) =>
        Task.Run(() => BatchQueryPnpBattery(instanceIds));

    private static Dictionary<string, int> BatchQueryPnpBattery(List<string> instanceIds)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (instanceIds.Count == 0) return result;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
            sb.AppendLine("$key = '" + BatteryPKey + "'");
            sb.AppendLine("$ids = @(");
            foreach (string id in instanceIds)
                sb.AppendLine($"  '{id.Replace("'", "''")}'");
            sb.AppendLine(")");
            sb.AppendLine("foreach ($id in $ids) {");
            sb.AppendLine("  $val = (Get-PnpDeviceProperty -InstanceId $id -KeyName $key -ErrorAction SilentlyContinue).Data");
            sb.AppendLine("  Write-Output \"$id=$val\"");
            sb.AppendLine("}");

            string script = Convert.ToBase64String(
                Encoding.Unicode.GetBytes(sb.ToString()));

            using var ps = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -EncodedCommand {script}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            ps.Start();
            string output = ps.StandardOutput.ReadToEnd();
            ps.WaitForExit();

            foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = line.LastIndexOf('=');
                if (eq < 0) continue;

                string id = line[..eq].Trim();
                string val = line[(eq + 1)..].Trim();

                if (int.TryParse(val, out int pct) && pct is >= 0 and <= 100)
                    result[id] = pct;
            }
        }
        catch
        {
        }

        return result;
    }

    public static async Task<int> QueryPnpBatteryAsync(string instanceId)
    {
        var map = await BatchQueryPnpBatteryAsync(new List<string> { instanceId });
        return map.TryGetValue(instanceId, out int v) ? v : -1;
    }
}
