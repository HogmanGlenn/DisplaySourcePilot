namespace DisplaySourcePilot.Core.Models;

public sealed record InputSource(
    string Id,
    string Label,
    InputKind Kind,
    uint? VcpCode = null,
    RemoteRoute? Route = null);
