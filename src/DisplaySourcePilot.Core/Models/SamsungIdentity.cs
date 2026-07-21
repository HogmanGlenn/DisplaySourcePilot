namespace DisplaySourcePilot.Core.Models;

public sealed record SamsungIdentity(
    string Id,
    string Name,
    string Model,
    string Address,
    bool SupportsTokenAuthentication,
    IReadOnlyList<TimeSpan> ProbeRoundTrips);
