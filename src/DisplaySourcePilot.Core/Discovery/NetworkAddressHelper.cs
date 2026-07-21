using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DisplaySourcePilot.Core.Discovery;

internal static class NetworkAddressHelper
{
    public static IReadOnlyList<IPAddress> GetLocalIpv4Addresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up &&
                              network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork &&
                              IsPrivate(address.Address))
            .Select(address => address.Address)
            .Distinct()
            .ToArray();

    public static IEnumerable<IPAddress> EnumerateClassCSubnet(IPAddress localAddress)
    {
        var bytes = localAddress.GetAddressBytes();
        if (bytes.Length != 4)
        {
            yield break;
        }

        for (var host = 1; host < 255; host++)
        {
            if (host != bytes[3])
            {
                yield return new IPAddress([bytes[0], bytes[1], bytes[2], (byte)host]);
            }
        }
    }

    public static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 &&
               (bytes[0] == 10 ||
                (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168));
    }
}
