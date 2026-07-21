using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DisplaySourcePilot.Core.Models;

namespace DisplaySourcePilot.Core.Discovery;

public sealed class SamsungNetworkDiscovery
{
    private const int ScanConcurrency = 64;

    public async Task<SamsungIdentity?> DiscoverAtAddressAsync(
        string address,
        CancellationToken cancellationToken = default)
    {
        if (!IPAddress.TryParse(address, out var parsed) || !NetworkAddressHelper.IsPrivate(parsed))
        {
            throw new ArgumentException("Enter a private IPv4 address, such as 192.168.1.100.", nameof(address));
        }

        using var reader = new SamsungIdentityReader(TimeSpan.FromSeconds(2));
        return await reader.ReadAsync(parsed, cancellationToken);
    }

    public async Task<IReadOnlyList<SamsungIdentity>> DiscoverAsync(
        IProgress<DiscoveryStatus>? progress = null,
        IEnumerable<string>? preferredAddresses = null,
        CancellationToken cancellationToken = default)
    {
        var candidates = new HashSet<IPAddress>();
        if (preferredAddresses is not null)
        {
            foreach (var value in preferredAddresses)
            {
                if (IPAddress.TryParse(value, out var address) && NetworkAddressHelper.IsPrivate(address))
                {
                    candidates.Add(address);
                }
            }
        }

        progress?.Report(new DiscoveryStatus("Checking the local network...", 0));
        foreach (var address in await FindSsdpCandidatesAsync(cancellationToken))
        {
            candidates.Add(address);
        }

        var localAddresses = NetworkAddressHelper.GetLocalIpv4Addresses();
        foreach (var address in localAddresses.SelectMany(NetworkAddressHelper.EnumerateClassCSubnet))
        {
            candidates.Add(address);
        }

        var found = new ConcurrentDictionary<string, SamsungIdentity>(StringComparer.OrdinalIgnoreCase);
        using var reader = new SamsungIdentityReader(TimeSpan.FromMilliseconds(550));
        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions { MaxDegreeOfParallelism = ScanConcurrency, CancellationToken = cancellationToken },
            async (address, token) =>
            {
                var identity = await reader.ReadAsync(address, token);
                if (identity is not null && found.TryAdd(identity.Id, identity))
                {
                    progress?.Report(new DiscoveryStatus("Samsung display found.", found.Count));
                }
            });

        return found.Values.OrderBy(identity => identity.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static async Task<IReadOnlyList<IPAddress>> FindSsdpCandidatesAsync(CancellationToken cancellationToken)
    {
        var found = new HashSet<IPAddress>();
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        var request = string.Join("\r\n",
            "M-SEARCH * HTTP/1.1",
            "HOST: 239.255.255.250:1900",
            "MAN: \"ssdp:discover\"",
            "MX: 1",
            "ST: ssdp:all",
            string.Empty,
            string.Empty);
        var payload = Encoding.ASCII.GetBytes(request);
        await udp.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(900));
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                var response = await udp.ReceiveAsync(timeout.Token);
                if (NetworkAddressHelper.IsPrivate(response.RemoteEndPoint.Address))
                {
                    found.Add(response.RemoteEndPoint.Address);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
        }

        return found.ToArray();
    }
}
