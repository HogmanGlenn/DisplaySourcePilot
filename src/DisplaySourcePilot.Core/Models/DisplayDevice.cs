namespace DisplaySourcePilot.Core.Models;

public sealed record DisplayDevice(
    string Id,
    string Name,
    string Detail,
    ControlMethod Method,
    bool IsPrimary,
    IReadOnlyList<InputSource> Inputs,
    string? Address = null,
    int Port = 0,
    string? Model = null,
    string? HardwareId = null,
    bool RequiresCalibration = false,
    int SuggestedMenuDelayMilliseconds = 700);
