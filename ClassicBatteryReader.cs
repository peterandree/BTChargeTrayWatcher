using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;

namespace BTChargeTrayWatcher;

public class ClassicBatteryReader
{
    private const string HfagPattern = "_HCIBYPASS_";

    private static readonly Guid BatteryPropertyGuid =
        new("104EA319-6EE2-4701-BD47-8DDBF425BBE5");

    private static readonly Regex MacRegex =
        new(@"&0&([0-9A-Fa-f]{12})_", RegexOptions.Compiled);

    public Task<List<(string Name, int Battery)>> ReadAllAsync() =>
        ReadAllAsync(CancellationToken.None);

    public Task<List<(string Name, int Battery)>> ReadAllAsync(CancellationToken cancellationToken) =>
        Task.Run(() => ReadAllAsync_Internal(cancellationToken), cancellationToken);

    private static async Task<List<(string Name, int Battery)>> ReadAllAsync_Internal(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var candidates = EnumerateCandidates();
        if (candidates.Count == 0) return new();

        cancellationToken.ThrowIfCancellationRequested();

        var connectTasks = candidates.Select(async c =>
            (c, connected: await IsConnectedAsync(c.Address, cancellationToken).ConfigureAwait(false)));

        var connected = (await Task.WhenAll(connectTasks).ConfigureAwait(false))
            .Where(r => r.connected)
            .Select(r => r.c)
            .ToList();

        if (connected.Count == 0) return new();

        var results = new List<(string, int)>();
        foreach (var (name, instanceId, _) in connected)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int battery = ReadBatteryProperty(instanceId);
            if (battery >= 0)
                results.Add((name, battery));
        }

        return results;
    }

    private static List<(string Name, string InstanceId, ulong Address)> EnumerateCandidates()
    {
        var results = new List<(string Name, string InstanceId, ulong Address)>();

        IntPtr devList = NativeMethods.SetupDiGetClassDevsW(
            IntPtr.Zero,
            "BTHENUM",
            IntPtr.Zero,
            NativeMethods.DIGCF_ALLCLASSES | NativeMethods.DIGCF_PRESENT);

        if (devList == NativeMethods.INVALID_HANDLE_VALUE)
            return results;

        try
        {
            var devData = new NativeMethods.SP_DEVINFO_DATA
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.SP_DEVINFO_DATA>()
            };

            for (uint i = 0; NativeMethods.SetupDiEnumDeviceInfo(devList, i, ref devData); i++)
            {
                string? instanceId = GetInstanceId(devList, ref devData);
                if (instanceId is null) continue;
                if (!instanceId.Contains(HfagPattern, StringComparison.OrdinalIgnoreCase)) continue;

                Match match = MacRegex.Match(instanceId);
                if (!match.Success) continue;

                ulong address = Convert.ToUInt64(match.Groups[1].Value, 16);

                string rawName = GetDeviceDescription(devList, ref devData) ?? instanceId;
                string name = Regex.Replace(
                    rawName,
                    @"\s*(Hands-Free AG|HFP|AG)$",
                    "",
                    RegexOptions.IgnoreCase).Trim();

                results.Add((name, instanceId, address));
            }
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(devList);
        }

        return results;
    }

    private static string? GetInstanceId(IntPtr devList, ref NativeMethods.SP_DEVINFO_DATA devData)
    {
        var sb = new StringBuilder(512);
        return NativeMethods.SetupDiGetDeviceInstanceIdW(
            devList,
            ref devData,
            sb,
            (uint)sb.Capacity,
            out _)
            ? sb.ToString()
            : null;
    }

    private static string? GetDeviceDescription(IntPtr devList, ref NativeMethods.SP_DEVINFO_DATA devData)
    {
        byte[] buf = new byte[1024];
        var key = new NativeMethods.DEVPROPKEY
        {
            fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
            pid = 14
        };

        if (NativeMethods.SetupDiGetDevicePropertyW(
                devList,
                ref devData,
                ref key,
                out _,
                buf,
                (uint)buf.Length,
                out uint needed,
                0)
            && needed > 2)
        {
            return Encoding.Unicode.GetString(buf, 0, (int)needed - 2);
        }

        key.pid = 2;
        if (NativeMethods.SetupDiGetDevicePropertyW(
                devList,
                ref devData,
                ref key,
                out _,
                buf,
                (uint)buf.Length,
                out needed,
                0)
            && needed > 2)
        {
            return Encoding.Unicode.GetString(buf, 0, (int)needed - 2);
        }

        return null;
    }

    public static int ReadBatteryProperty(string instanceId)
    {
        IntPtr devList = NativeMethods.SetupDiGetClassDevsW(
            IntPtr.Zero,
            "BTHENUM",
            IntPtr.Zero,
            NativeMethods.DIGCF_ALLCLASSES | NativeMethods.DIGCF_PRESENT);

        if (devList == NativeMethods.INVALID_HANDLE_VALUE)
            return -1;

        try
        {
            var devData = new NativeMethods.SP_DEVINFO_DATA
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.SP_DEVINFO_DATA>()
            };

            for (uint i = 0; NativeMethods.SetupDiEnumDeviceInfo(devList, i, ref devData); i++)
            {
                string? id = GetInstanceId(devList, ref devData);
                if (id is null || !id.Equals(instanceId, StringComparison.OrdinalIgnoreCase))
                    continue;

                return ReadBatteryFromDevInfo(devList, ref devData);
            }
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(devList);
        }

        return -1;
    }

    private static int ReadBatteryFromDevInfo(
        IntPtr devList,
        ref NativeMethods.SP_DEVINFO_DATA devData)
    {
        var key = new NativeMethods.DEVPROPKEY
        {
            fmtid = BatteryPropertyGuid,
            pid = 2
        };

        byte[] buf = new byte[64];
        if (!NativeMethods.SetupDiGetDevicePropertyW(
                devList,
                ref devData,
                ref key,
                out uint propType,
                buf,
                (uint)buf.Length,
                out _,
                0))
            return -1;

        int raw = propType switch
        {
            NativeMethods.DEVPROP_TYPE_BYTE => buf[0],
            NativeMethods.DEVPROP_TYPE_SBYTE => (sbyte)buf[0],
            NativeMethods.DEVPROP_TYPE_UINT16 => BitConverter.ToUInt16(buf, 0),
            NativeMethods.DEVPROP_TYPE_INT16 => BitConverter.ToInt16(buf, 0),
            NativeMethods.DEVPROP_TYPE_UINT32 => checked((int)BitConverter.ToUInt32(buf, 0)),
            NativeMethods.DEVPROP_TYPE_INT32 => BitConverter.ToInt32(buf, 0),
            _ => -1
        };

        return NormalizeBatteryPercent(raw);
    }

    private static int NormalizeBatteryPercent(int value) =>
        value is >= 0 and <= 100 ? value : -1;

    private static async Task<bool> IsConnectedAsync(
        ulong bluetoothAddress,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            using BluetoothDevice? device =
                await BluetoothDevice.FromBluetoothAddressAsync(bluetoothAddress)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);

            return device?.ConnectionStatus == BluetoothConnectionStatus.Connected;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return false;
        }
    }

    public static Task<int> QueryPnpBatteryAsync(string instanceId) =>
        Task.Run(() => ReadBatteryProperty(instanceId));
}

internal static class NativeMethods
{
    public const uint DIGCF_PRESENT = 0x00000002;
    public const uint DIGCF_ALLCLASSES = 0x00000004;
    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    public const uint DEVPROP_TYPE_SBYTE = 0x00000002;
    public const uint DEVPROP_TYPE_BYTE = 0x00000003;
    public const uint DEVPROP_TYPE_INT16 = 0x00000004;
    public const uint DEVPROP_TYPE_UINT16 = 0x00000005;
    public const uint DEVPROP_TYPE_INT32 = 0x00000006;
    public const uint DEVPROP_TYPE_UINT32 = 0x00000007;

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevsW(
        IntPtr ClassGuid,
        string? Enumerator,
        IntPtr hwndParent,
        uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInfo(
        IntPtr DeviceInfoSet,
        uint MemberIndex,
        ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiGetDeviceInstanceIdW(
        IntPtr DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData,
        StringBuilder DeviceInstanceId,
        uint DeviceInstanceIdSize,
        out uint RequiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiGetDevicePropertyW(
        IntPtr DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData,
        ref DEVPROPKEY PropertyKey,
        out uint PropertyType,
        byte[] PropertyBuffer,
        uint PropertyBufferSize,
        out uint RequiredSize,
        uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);
}
