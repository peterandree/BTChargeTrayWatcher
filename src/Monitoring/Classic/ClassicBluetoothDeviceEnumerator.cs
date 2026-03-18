using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace BTChargeTrayWatcher;

internal sealed class ClassicBluetoothDeviceEnumerator
{
    private const string HfagPattern = "_HCIBYPASS_";

    private static readonly Regex MacRegex =
        new(@"&0&([0-9A-Fa-f]{12})_", RegexOptions.Compiled);

    public List<ClassicBluetoothCandidate> EnumerateCandidates()
    {
        var results = new List<ClassicBluetoothCandidate>();

        IntPtr devList = SetupApiNative.SetupDiGetClassDevsW(
            IntPtr.Zero,
            "BTHENUM",
            IntPtr.Zero,
            SetupApiNative.DIGCF_ALLCLASSES | SetupApiNative.DIGCF_PRESENT);

        if (devList == SetupApiNative.INVALID_HANDLE_VALUE)
            return results;

        try
        {
            var devData = new SetupApiNative.SP_DEVINFO_DATA
            {
                cbSize = (uint)Marshal.SizeOf<SetupApiNative.SP_DEVINFO_DATA>()
            };

            for (uint i = 0; SetupApiNative.SetupDiEnumDeviceInfo(devList, i, ref devData); i++)
            {
                string? instanceId = GetInstanceId(devList, ref devData);
                if (instanceId is null)
                    continue;

                if (!instanceId.Contains(HfagPattern, StringComparison.OrdinalIgnoreCase))
                    continue;

                Match match = MacRegex.Match(instanceId);
                if (!match.Success)
                    continue;

                ulong address = Convert.ToUInt64(match.Groups[1].Value, 16);

                string rawName = GetDeviceDescription(devList, ref devData) ?? instanceId;
                string name = Regex.Replace(
                    rawName,
                    @"\s*(Hands-Free AG|HFP|AG)$",
                    "",
                    RegexOptions.IgnoreCase).Trim();

                results.Add(new ClassicBluetoothCandidate(name, instanceId, address));
            }
        }
        finally
        {
            SetupApiNative.SetupDiDestroyDeviceInfoList(devList);
        }

        return results;
    }

    private static string? GetInstanceId(IntPtr devList, ref SetupApiNative.SP_DEVINFO_DATA devData)
    {
        var sb = new StringBuilder(512);
        return SetupApiNative.SetupDiGetDeviceInstanceIdW(
            devList,
            ref devData,
            sb,
            (uint)sb.Capacity,
            out _)
            ? sb.ToString()
            : null;
    }

    private static string? GetDeviceDescription(IntPtr devList, ref SetupApiNative.SP_DEVINFO_DATA devData)
    {
        byte[] buf = new byte[1024];
        var key = new SetupApiNative.DEVPROPKEY
        {
            fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
            pid = 14
        };

        if (SetupApiNative.SetupDiGetDevicePropertyW(
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
        if (SetupApiNative.SetupDiGetDevicePropertyW(
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
}

internal readonly record struct ClassicBluetoothCandidate(
    string Name,
    string InstanceId,
    ulong Address);
