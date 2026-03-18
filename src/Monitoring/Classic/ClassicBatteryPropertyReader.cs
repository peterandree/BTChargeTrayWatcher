using System.Runtime.InteropServices;

namespace BTChargeTrayWatcher;

internal sealed class ClassicBatteryPropertyReader
{
    private static readonly Guid BatteryPropertyGuid =
        new("104EA319-6EE2-4701-BD47-8DDBF425BBE5");

    public int ReadBatteryProperty(string instanceId)
    {
        IntPtr devList = SetupApiNative.SetupDiGetClassDevsW(
            IntPtr.Zero,
            "BTHENUM",
            IntPtr.Zero,
            SetupApiNative.DIGCF_ALLCLASSES | SetupApiNative.DIGCF_PRESENT);

        if (devList == SetupApiNative.INVALID_HANDLE_VALUE)
        {
            return -1;
        }

        try
        {
            var devData = new SetupApiNative.SP_DEVINFO_DATA
            {
                cbSize = (uint)Marshal.SizeOf<SetupApiNative.SP_DEVINFO_DATA>()
            };

            for (uint i = 0; SetupApiNative.SetupDiEnumDeviceInfo(devList, i, ref devData); i++)
            {
                string? id = GetInstanceId(devList, ref devData);
                if (id is null || !id.Equals(instanceId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return ReadBatteryFromDevInfo(devList, ref devData);
            }
        }
        finally
        {
            SetupApiNative.SetupDiDestroyDeviceInfoList(devList);
        }

        return -1;
    }

    private static string? GetInstanceId(IntPtr devList, ref SetupApiNative.SP_DEVINFO_DATA devData)
    {
        var sb = new System.Text.StringBuilder(512);
        return SetupApiNative.SetupDiGetDeviceInstanceIdW(
            devList,
            ref devData,
            sb,
            (uint)sb.Capacity,
            out _)
            ? sb.ToString()
            : null;
    }

    private static int ReadBatteryFromDevInfo(
        IntPtr devList,
        ref SetupApiNative.SP_DEVINFO_DATA devData)
    {
        var key = new SetupApiNative.DEVPROPKEY
        {
            fmtid = BatteryPropertyGuid,
            pid = 2
        };

        byte[] buf = new byte[64];
        if (!SetupApiNative.SetupDiGetDevicePropertyW(
                devList,
                ref devData,
                ref key,
                out uint propType,
                buf,
                (uint)buf.Length,
                out _,
                0))
        {
            return -1;
        }

        int raw = propType switch
        {
            SetupApiNative.DEVPROP_TYPE_BYTE => buf[0],
            SetupApiNative.DEVPROP_TYPE_SBYTE => (sbyte)buf[0],
            SetupApiNative.DEVPROP_TYPE_UINT16 => BitConverter.ToUInt16(buf, 0),
            SetupApiNative.DEVPROP_TYPE_INT16 => BitConverter.ToInt16(buf, 0),
            SetupApiNative.DEVPROP_TYPE_UINT32 => checked((int)BitConverter.ToUInt32(buf, 0)),
            SetupApiNative.DEVPROP_TYPE_INT32 => BitConverter.ToInt32(buf, 0),
            _ => -1
        };

        return NormalizeBatteryPercent(raw);
    }

    private static int NormalizeBatteryPercent(int value) =>
        value is >= 0 and <= 100 ? value : -1;
}
