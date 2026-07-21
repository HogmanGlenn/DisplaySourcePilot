using System.Runtime.InteropServices;
using System.Text;

namespace DisplaySourcePilot.Core.Interop;

internal static class DdcNative
{
    internal const uint MonitorInfoPrimary = 0x00000001;
    internal const uint GetDeviceInterfaceName = 0x00000001;

    [return: MarshalAs(UnmanagedType.Bool)]
    internal delegate bool MonitorEnumProc(
        IntPtr monitor,
        IntPtr deviceContext,
        IntPtr monitorRectangle,
        IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfoEx
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct PhysicalMonitor
    {
        public IntPtr Handle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NativeDisplayDevice
    {
        public uint Size;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public uint StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayMonitors(
        IntPtr deviceContext,
        IntPtr clippingRectangle,
        MonitorEnumProc callback,
        IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx monitorInfo);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayDevices(
        string device,
        uint deviceNumber,
        ref NativeDisplayDevice displayDevice,
        uint flags);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(
        IntPtr monitor,
        out uint numberOfPhysicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetPhysicalMonitorsFromHMONITOR(
        IntPtr monitor,
        uint physicalMonitorArraySize,
        [Out] PhysicalMonitor[] physicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyPhysicalMonitors(
        uint physicalMonitorArraySize,
        PhysicalMonitor[] physicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCapabilitiesStringLength(
        IntPtr monitor,
        out uint capabilitiesStringLengthInCharacters);

    [DllImport("dxva2.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CapabilitiesRequestAndCapabilitiesReply(
        IntPtr monitor,
        StringBuilder capabilitiesString,
        uint capabilitiesStringLengthInCharacters);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetVCPFeature(IntPtr monitor, byte vcpCode, uint newValue);
}
