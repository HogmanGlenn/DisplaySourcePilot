using System.Runtime.InteropServices;
using DisplaySourcePilot.Core.Interop;

namespace DisplaySourcePilot.Core.Discovery;

internal static class DdcMonitorIdentity
{
    public static string? Read(string logicalDeviceName, int physicalMonitorIndex)
    {
        var device = new DdcNative.NativeDisplayDevice
        {
            Size = (uint)Marshal.SizeOf<DdcNative.NativeDisplayDevice>()
        };

        if (!DdcNative.EnumDisplayDevices(
                logicalDeviceName,
                checked((uint)physicalMonitorIndex),
                ref device,
                DdcNative.GetDeviceInterfaceName))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(device.DeviceId) ? null : device.DeviceId.Trim();
    }
}
