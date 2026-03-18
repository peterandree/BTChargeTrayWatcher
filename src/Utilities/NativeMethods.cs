using System;
using System.Runtime.InteropServices;

namespace BTChargeTrayWatcher;

internal static class NativeMethods
{
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern bool DestroyIcon(IntPtr handle);
}
