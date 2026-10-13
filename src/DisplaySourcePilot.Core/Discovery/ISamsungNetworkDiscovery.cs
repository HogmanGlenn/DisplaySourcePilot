using DisplaySourcePilot.Core.Models;

namespace DisplaySourcePilot.Core.Discovery;

public interface ISamsungNetworkDiscovery
{
    Task<IReadOnlyList<SamsungIdentity>> DiscoverAsync(
        IProgress<DiscoveryStatus>? progress = null,
        IEnumerable<string>? preferredAddresses = null,
        CancellationToken cancellationToken = default);
}
