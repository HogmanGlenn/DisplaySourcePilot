using DisplaySourcePilot.Core.Models;

namespace DisplaySourcePilot.Core.Control;

public sealed record SamsungSourceProfile(
    IReadOnlyList<string> ModelPrefixes,
    InputSource Hdmi,
    InputSource DisplayPort,
    int RenderBudgetMilliseconds,
    int MinimumDelayMilliseconds = 120,
    int MaximumDelayMilliseconds = 1200)
{
    public bool Matches(string model) => ModelPrefixes.Any(prefix =>
        model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
