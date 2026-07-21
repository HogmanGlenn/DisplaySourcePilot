using DisplaySourcePilot.Core.Control;
using DisplaySourcePilot.Core.Models;

namespace DisplaySourcePilot.Core.Discovery;

public sealed class DisplayDiscoveryService
{
    private readonly DdcMonitorDiscovery _ddcDiscovery = new();
    private readonly SamsungNetworkDiscovery _samsungDiscovery = new();

    public async Task<IReadOnlyList<DisplayDevice>> DiscoverAsync(
        IProgress<DiscoveryStatus>? progress = null,
        IEnumerable<string>? preferredAddresses = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new DiscoveryStatus("Checking connected displays...", 0));
        var ddcTask = _ddcDiscovery.DiscoverAsync(cancellationToken);
        var samsungTask = _samsungDiscovery.DiscoverAsync(progress, preferredAddresses, cancellationToken);

        IReadOnlyList<DisplayDevice> ddcDevices;
        try
        {
            ddcDevices = await ddcTask;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ddcDevices = [];
            progress?.Report(new DiscoveryStatus("Cable-connected displays could not be checked.", 0));
        }

        IReadOnlyList<SamsungIdentity> samsungDisplays;
        try
        {
            samsungDisplays = await samsungTask;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            samsungDisplays = [];
            progress?.Report(new DiscoveryStatus("Network displays could not be checked.", ddcDevices.Count));
        }

        var devices = new List<DisplayDevice>(ddcDevices);
        foreach (var identity in samsungDisplays)
        {
            devices.Add(CreateSamsungDevice(identity));
        }

        var ordered = devices
            .OrderByDescending(device => device.IsPrimary)
            .ThenBy(device => device.Method)
            .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        progress?.Report(new DiscoveryStatus(
            ordered.Length == 0 ? "No compatible displays found." : $"Found {ordered.Length} compatible display{(ordered.Length == 1 ? string.Empty : "s")}.",
            ordered.Length,
            true));
        return ordered;
    }

    public async Task<DisplayDevice> DiscoverSamsungAtAddressAsync(
        string address,
        CancellationToken cancellationToken = default)
    {
        var identity = await _samsungDiscovery.DiscoverAtAddressAsync(address.Trim(), cancellationToken);
        return identity is null
            ? throw new InvalidOperationException("No compatible Samsung monitor responded at that address.")
            : CreateSamsungDevice(identity);
    }

    private static DisplayDevice CreateSamsungDevice(SamsungIdentity identity)
    {
        var profile = SamsungSourceProfiles.Find(identity.Model);
        var menuDelay = profile is null
            ? TimingOptimizer.CalculateMenuDelay(650, identity.ProbeRoundTrips, 700, 1200)
            : TimingOptimizer.CalculateMenuDelay(profile, identity.ProbeRoundTrips);
        var inputs = profile is null
            ? new[]
            {
                new InputSource("hdmi", "HDMI", InputKind.Hdmi),
                new InputSource("displayport", "DisplayPort", InputKind.DisplayPort)
            }
            : new[] { profile.Hdmi, profile.DisplayPort };

        return new DisplayDevice(
            $"samsung:{identity.Id}",
            identity.Name,
            $"{identity.Model} - Local network",
            ControlMethod.SamsungTizen,
            false,
            inputs,
            identity.Address,
            identity.SupportsTokenAuthentication ? 8002 : 8001,
            identity.Model,
            identity.Id,
            profile is null,
            menuDelay);
    }
}
