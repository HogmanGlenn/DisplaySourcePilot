namespace DisplaySourcePilot.Core.Models;

public sealed record DiscoveryStatus(string Message, int DevicesFound, bool IsComplete = false);
