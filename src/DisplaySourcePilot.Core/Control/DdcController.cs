using System.ComponentModel;
using System.Runtime.InteropServices;
using DisplaySourcePilot.Core.Configuration;
using DisplaySourcePilot.Core.Discovery;
using DisplaySourcePilot.Core.Interop;
using DisplaySourcePilot.Core.Models;

namespace DisplaySourcePilot.Core.Control;

public sealed class DdcController
{
    public Task SwitchAsync(
        ConfiguredDisplay display,
        InputSource input,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Switch(display, input, cancellationToken), cancellationToken);

    private static void Switch(ConfiguredDisplay display, InputSource input, CancellationToken cancellationToken)
    {
        if (input.VcpCode is not { } inputCode)
        {
            throw new InvalidOperationException($"{input.Label} does not have a DDC/CI input code.");
        }

        var found = false;
        var canceled = false;
        Exception? failure = null;
        var callback = new DdcNative.MonitorEnumProc((logicalMonitor, _, _, _) =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
                return false;
            }

            if (found)
            {
                return true;
            }

            var monitorInfo = new DdcNative.MonitorInfoEx
            {
                Size = (uint)Marshal.SizeOf<DdcNative.MonitorInfoEx>()
            };

            if (!DdcNative.GetMonitorInfo(logicalMonitor, ref monitorInfo))
            {
                return true;
            }

            if (!DdcNative.GetNumberOfPhysicalMonitorsFromHMONITOR(logicalMonitor, out var count) || count == 0)
            {
                return true;
            }

            var monitors = new DdcNative.PhysicalMonitor[count];
            if (!DdcNative.GetPhysicalMonitorsFromHMONITOR(logicalMonitor, count, monitors))
            {
                return true;
            }

            try
            {
                for (var index = 0; index < monitors.Length; index++)
                {
                    var hardwareId = DdcMonitorIdentity.Read(monitorInfo.DeviceName, index);
                    if (!string.Equals(hardwareId, display.HardwareId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    found = true;
                    if (!DdcNative.SetVCPFeature(monitors[index].Handle, 0x60, inputCode))
                    {
                        failure = new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            $"{display.Name} did not accept the input command. Check that DDC/CI is enabled in the monitor menu.");
                    }

                    break;
                }

                return true;
            }
            finally
            {
                DdcNative.DestroyPhysicalMonitors(count, monitors);
            }
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

        if (failure is not null)
        {
            throw failure;
        }

        if (!found)
        {
            throw new InvalidOperationException($"{display.Name} is no longer connected.");
        }
    }

}
