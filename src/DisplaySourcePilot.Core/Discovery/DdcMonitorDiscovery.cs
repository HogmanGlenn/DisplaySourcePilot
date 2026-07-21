using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using DisplaySourcePilot.Core.Interop;
using DisplaySourcePilot.Core.Models;

namespace DisplaySourcePilot.Core.Discovery;

public sealed class DdcMonitorDiscovery
{
    public Task<IReadOnlyList<DisplayDevice>> DiscoverAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<DisplayDevice>>(() => Discover(cancellationToken), cancellationToken);

    private static List<DisplayDevice> Discover(CancellationToken cancellationToken)
    {
        var devices = new List<DisplayDevice>();
        var canceled = false;
        var callback = new DdcNative.MonitorEnumProc((logicalMonitor, _, _, _) =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
                return false;
            }

            AddPhysicalMonitors(logicalMonitor, devices);
            return true;
        });

        var enumerated = DdcNative.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        if (canceled)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (!enumerated)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not enumerate connected displays.");
        }

        return devices;
    }

    private static void AddPhysicalMonitors(IntPtr logicalMonitor, List<DisplayDevice> devices)
    {
        var monitorInfo = new DdcNative.MonitorInfoEx
        {
            Size = (uint)Marshal.SizeOf<DdcNative.MonitorInfoEx>()
        };

        if (!DdcNative.GetMonitorInfo(logicalMonitor, ref monitorInfo) ||
            !DdcNative.GetNumberOfPhysicalMonitorsFromHMONITOR(logicalMonitor, out var count) ||
            count == 0)
        {
            return;
        }

        var monitors = new DdcNative.PhysicalMonitor[count];
        if (!DdcNative.GetPhysicalMonitorsFromHMONITOR(logicalMonitor, count, monitors))
        {
            return;
        }

        try
        {
            for (var index = 0; index < monitors.Length; index++)
            {
                var monitor = monitors[index];
                var hardwareId = DdcMonitorIdentity.Read(monitorInfo.DeviceName, index);
                if (hardwareId is null)
                {
                    continue;
                }

                var inputs = ReadInputs(monitor.Handle);
                if (inputs.Count == 0)
                {
                    continue;
                }

                var description = string.IsNullOrWhiteSpace(monitor.Description)
                    ? "External display"
                    : monitor.Description.Trim();
                var isPrimary = (monitorInfo.Flags & DdcNative.MonitorInfoPrimary) != 0;
                devices.Add(new DisplayDevice(
                    $"ddc:{hardwareId}",
                    description,
                    isPrimary ? "Primary display - DDC/CI" : "DDC/CI",
                    ControlMethod.Ddc,
                    isPrimary,
                    inputs,
                    HardwareId: hardwareId));
            }
        }
        finally
        {
            DdcNative.DestroyPhysicalMonitors(count, monitors);
        }
    }

    private static IReadOnlyList<InputSource> ReadInputs(IntPtr physicalMonitor)
    {
        if (!DdcNative.GetCapabilitiesStringLength(physicalMonitor, out var length) || length is 0 or > 65_536)
        {
            return [];
        }

        var capabilities = new StringBuilder((int)length);
        return DdcNative.CapabilitiesRequestAndCapabilitiesReply(physicalMonitor, capabilities, length)
            ? DdcCapabilitiesParser.ParseInputs(capabilities.ToString())
            : [];
    }
}
